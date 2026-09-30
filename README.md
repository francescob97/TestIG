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

## Modalità

| | Viste Unreal | |
|---|---|---|
| **Desktop** | 1 | la camera di Unity è il punto di vista, il quad le è figlio |
| **VR mono** | 1, dal centro della testa | stessa immagine nei due occhi, all'infinito; metà costo |
| **VR stereo** | 2, una per occhio | frustum asimmetrico esatto di ciascun occhio; profondità vera |

Si sceglie in Unity (inspector, riga di comando, o **F2** a runtime); Unreal
ricrea le sue superfici senza essere riavviato. In VR l'immagine viene
**riproiettata per rotazione**: resta ferma nel mondo anche se la testa gira dopo
il render di Unreal. Dettagli in [`docs/06-vr.md`](docs/06-vr.md).

---

## Cosa viene trasportato

| Gruppo | Canali | Formato | Cadenza |
|---|---|---|---|
| `MAIN` | `COLOR` + `DEPTH` per ogni vista (1 o 2) | `B8G8R8A8_UNORM` + `R32_FLOAT`, risoluzione chiesta da Unity | free-run |
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
- **[`docs/06-vr.md`](docs/06-vr.md)** — setup OpenXR, VR mono e stereo, riproiezione

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

## Cosa è verificato e cosa no

Questo codice è scritto in un ambiente Linux senza Unreal, senza Unity e senza
GPU. Quello che si poteva verificare lì è stato verificato davvero:

| Verificato | Come |
|---|---|
| Layout binario del protocollo | `static_assert` + offset di ogni campo stampati da gcc e clang |
| Plugin nativo Unity | **compilato** con mingw-w64 contro gli header D3D11 reali, `-Wall -Wextra`, zero warning; export della DLL confrontati con i `DllImport` del C# |
| Script C# di Unity | **compilati** con l'SDK .NET (netstandard2.1, C# 9, come Unity 2022) contro stub delle API Unity usate: zero errori, zero warning |
| Confine C# ↔ C | serializzazione **eseguita** in C# e decodificata in C (HELLO, POSE mono e stereo, 48 codifiche di evento) e viceversa (HANDSHAKE, STATUS): tutto coerente |
| Geometria tra i due motori | la stessa direzione nel mondo cade sullo stesso pixel in Unity e in Unreal, su 2000 pose e frustum asimmetrici casuali (errore ~1e-14) |
| Logica di scambio dei buffer | simulata con keyed mutex, frequenze diverse, render thread in ritardo, STATUS persi: correttezza sempre rispettata; ha trovato ed eliminato uno stallo |

| **Non** verificato | |
|---|---|
| Il modulo **Unreal** | non compilato: serve l'engine. Le API più a rischio tra versioni sono isolate e commentate |
| Gli **shader** | non compilati: serve Unity |
| Il comportamento **a runtime** | tutto ciò che dipende da GPU, visore e runtime XR si vede solo sulla macchina vera |

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
| Scena costruita **in C++** | Nessun Blueprint; lato Unreal la scena nasce dal codice. |
| Frustum come **quattro tangenti** | L'unica forma che descrive un occhio di un visore (asimmetrico) e che non dipende dalle convenzioni di clip space dei due motori. |
| VR presentato **per raggio**, non con un quad | Un quad non può essere giusto per due occhi insieme; la ricostruzione per direzione è esatta in stereo e diventa riproiezione rotazionale gratis. |
| Consume **solo su STATUS nuovo**, dati nell'id dell'evento | Si sa sempre quale pose sta mostrando il frame, ed è ciò che serve per riproiettarlo. |
| Timeout del produttore **0** | Misurato in simulazione: aspettare bloccava Unreal fino a metà del tempo senza dare frame in più. |
