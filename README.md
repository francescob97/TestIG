# GPU Share Spike

Spike per validare un'architettura: **un generatore di immagini in Unreal
Engine 5 che alimenta un client Unity**, con la vista trasportata da un processo
all'altro **sulla GPU**, senza mai passare dalla memoria di sistema.

Non è un prodotto. È un banco di misura: l'obiettivo dichiarato è **misurare**
l'anello pose → pixel, non stimarlo.

```
┌──────────────────────────┐                      ┌──────────────────────────┐
│  Unreal Engine 5.7       │   texture condivise  │  Unity 2022 URP          │
│  C++, RHI D3D11          │  ─────────────────▶  │  D3D11                   │
│                          │  NT handle + keyed   │                          │
│  SceneCapture2D ×2       │  mutex, doppio buf   │  plugin nativo C++       │
│  SceneCaptureCube        │                      │  quad a schermo intero   │
│                          │   ◀── UDP pose ───   │                          │
│  piano + 3 cubi, in C++  │   ─── UDP status ─▶  │  HUD con le metriche     │
└──────────────────────────┘                      └──────────────────────────┘
```

---

## Cosa viene trasportato

| Gruppo | Canali | Formato | Cadenza |
|---|---|---|---|
| `MAIN` | `COLOR` + `DEPTH` | `B8G8R8A8_UNORM` 1920×1080 + `R32_FLOAT` | free-run |
| `CUBE` | atlas 3×2 delle 6 facce | `B8G8R8A8_UNORM` 1536×1024 | 5 Hz, spento di default |

Un **gruppo** è l'unità di pubblicazione atomica: i suoi canali provengono
sempre dallo stesso frame. `COLOR` e `DEPTH` stanno insieme perché un
compositing con colore del frame N e depth del frame N−1 si rompe in modo
intermittente, cioè nel modo peggiore da diagnosticare.

Aggiungere un canale è una voce nella tabella dei canali, non un refactor.

---

## Come viene misurata la latenza

Unreal codifica **32 byte nei primi 8 pixel** in alto a sinistra del canale
colore: `frame_id`, il `QueryPerformanceCounter` che **Unity** aveva messo nel
pacchetto di pose, il timestamp di fine rendering, e un checksum.

```
latenza pose → pixel  =  qpc_consume  −  marker.qpc_pose_send
                         ▲                ▲
                         │                └─ scritto da Unity, tornato dentro i pixel
                         └─ timbrato da Unity quando l'AcquireSync ritorna
```

Entrambi i valori vengono dallo **stesso time base**: QPC su Windows è un
contatore di sistema, coerente tra processi sulla stessa macchina. Nessuna
sincronizzazione di clock, nessuna stima.

I 32 byte sono letti **in CPU come byte grezzi** dal plugin nativo, da una
staging texture mappata senza bloccare. Mai da uno shader: in un progetto Linear
color space un sampler applicherebbe la conversione sRGB e distruggerebbe i bit.

L'HUD di Unity mostra: latenza in ms (istantanea, media, min/max), età del frame
in frame di Unity, FPS dei due processi, e quante volte Unity ha ripresentato lo
stesso `frame_id`.

---

## Struttura del repository

```
shared/ShareProtocol.h      sorgente unica del protocollo, inclusa da entrambi i lati
unreal/GpuShareSpike/       progetto Unreal (C++, zero Blueprint, zero asset binari)
unity-native/               plugin nativo C++ per Unity (CMake)
unity/GpuShareSpike/Assets/ script C# e shader
docs/                       build, protocollo, canali, troubleshooting
```

---

## Partire

1. **[`docs/01-build-unreal.md`](docs/01-build-unreal.md)** — da Visual Studio al primo frame
2. **[`docs/02-build-unity.md`](docs/02-build-unity.md)** — plugin nativo + progetto URP
3. Avvia Unreal in **Standalone**, poi premi **Play** in Unity

Poi, quando serve:

- **[`docs/03-protocol.md`](docs/03-protocol.md)** — layout binario byte per byte
- **[`docs/04-channels.md`](docs/04-channels.md)** — canali, gruppi, keyed mutex, migrazione a D3D12
- **[`docs/05-troubleshooting.md`](docs/05-troubleshooting.md)** — sintomo → causa → rimedio

---

## Perché D3D11 e non D3D12

Perché **il keyed mutex esiste solo in D3D11**. In D3D12 la sincronizzazione
cross-processo si fa con fence condivise: circa il triplo del codice, e un
deadlock non dà un errore ma un freeze. Per uno spike il cui scopo è validare il
trasporto, è il percorso sbagliato.

Inoltre, con Unreal su D3D11 la texture del render target **è già** una
`ID3D11Texture2D` sullo stesso device: la `CopyResource` verso la superficie
condivisa è diretta.

Il prezzo: niente Nanite, Virtual Shadow Maps, Lumen o ray tracing — tutte
feature SM6. Per un piano e tre cubi non importa. Per il prodotto finale
importerà, e allora si migra: il trasporto è isolato dietro
`ISharedSurfaceTransport`, e protocollo, strumentazione e **tutto il lato Unity**
restano invariati.

> **Nota su UE 5.7.** D3D11/SM5 c'è ancora ed è selezionabile, ma è un percorso
> poco testato: in 5.7 Lumen su SM5 è rotto (uno shader `StochasticLighting`
> richiede 8 UAV contro il limite hardware di 4 di SM5, regressione rispetto a
> 5.6). Qui Lumen è disattivato esplicitamente nel `DefaultEngine.ini`, e il
> modulo si rifiuta di partire se l'RHI attivo non è D3D11.

---

## Scelte progettuali, in breve

| Scelta | Perché |
|---|---|
| Pose **latest-wins**, mai in coda | Una coda farebbe crescere la latenza senza limite quando Unity manda più in fretta di quanto Unreal renderizzi. |
| Unity **copia** e rilascia subito il mutex | Tenere il mutex per tutto il frame bloccherebbe Unreal; al primo frame perso i due processi si incastrano. |
| Timeout **0** sull'acquire lato Unity | Aspettare mascherebbe le ripetizioni, che sono uno dei dati da misurare. |
| Unreal **salta** il frame se i buffer sono occupati | Un Unity lento o morto non deve poter bloccare il render thread di Unreal. |
| Marker letto in **CPU**, mai campionato | Un sampler applicherebbe la conversione sRGB e distruggerebbe i bit. |
| Cubemap via **atlas 2D** | Le shared resource D3D11 richiedono `ArraySize == 1`; una `TextureCube` ne ha 6. |
| **LUID** dell'adapter nell'handshake | Un mismatch di GPU altrimenti si manifesta solo come un `HRESULT` muto. |
| Scena costruita **in C++** | Nessun Blueprint, nessun `.umap` o `.unity` binario nel repository. |
