# Canali e gruppi

## Il modello

```
CANALE  = una superficie condivisa (colore, depth, atlas cubemap, ...)
GRUPPO  = insieme di canali pubblicati ATOMICAMENTE, con doppio buffer
```

Il gruppo — non il canale — è l'unità di pubblicazione. Motivo: **COLOR e DEPTH
devono provenire dallo stesso frame**. Pubblicati indipendentemente, prima o poi
Unity leggerebbe il colore del frame N col depth del frame N−1, e il
compositing sarebbe sbagliato in modo intermittente, cioè nel modo peggiore da
diagnosticare. Pubblicandoli insieme il problema non esiste per costruzione.

| Gruppo | Canali | Risoluzione | Formato | Cadenza |
|---|---|---|---|---|
| `MAIN` | `COLOR` + `DEPTH` | 1920×1080 | `B8G8R8A8_UNORM` + `R32_FLOAT` | free-run, cap configurabile |
| `CUBE` | `CUBE_ATLAS` | 1536×1024 (3×2 × 512) | `B8G8R8A8_UNORM` | 5 Hz, configurabile |

Aggiungere un canale (motion vector, object ID, un G-buffer) è una voce nella
tabella dei canali, non un refactor: l'handshake la trasporta, Unity apre quello
che gli viene dichiarato.

---

## Protocollo del keyed mutex

```
chiave 0 (PRODUCER) = il buffer è libero, Unreal può scriverci
chiave 1 (CONSUMER) = il buffer contiene un frame pronto, Unity può leggerlo
```

```
Unreal:  AcquireSync(0) → CopyResource ×N → marker → ReleaseSync(1)
Unity:   AcquireSync(1) → CopyResource ×N → ReleaseSync(0)
```

Un `IDXGIKeyedMutex` appena creato è "rilasciato con chiave 0", quindi la prima
`AcquireSync(0)` del produttore riesce sempre.

### Perché Unity copia invece di usare direttamente la shared texture

Finché tieni il keyed mutex, il produttore non può scrivere. Se Unity lo
tenesse per tutta la durata del proprio frame, bloccherebbe Unreal, e al primo
frame perso i due processi si incastrerebbero a vicenda.

Copiando (~8 MB, molto meno di 0.1 ms su qualunque GPU moderna) il mutex si tiene
per pochi microsecondi e i due processi restano disaccoppiati. Il doppio buffer
fa il resto: Unreal scrive su A mentre Unity legge da B.

### Nessuno dei due si blocca mai

- **Produttore**: prova il buffer diverso dall'ultimo pubblicato, poi l'altro.
  Se entrambi sono occupati (consumatore indietro o morto), **salta il frame** e
  incrementa un contatore. Timeout configurabile, default 2 ms.
- **Consumatore**: timeout **0** — se il frame non c'è, non lo aspetta:
  ripresenta il precedente e conta una ripetizione. Aspettare mascherebbe
  proprio il dato che vogliamo misurare.

`WAIT_ABANDONED` (l'altro processo è morto tenendo il mutex) viene trattato come
acquisizione riuscita, perché è quello che dice la specifica D3D11, e loggato.

---

## Canale DEPTH

`ESceneCaptureSource::SCS_SceneDepth` su un render target `RTF_R32f`.

Due conseguenze, entrambe volute:

1. **La sorgente è già una texture colore `R32_FLOAT`**, non un depth-stencil
   buffer. La `CopyResource` verso la shared texture è quindi diretta e si evita
   tutto il campo minato dei formati depth-stencil (typeless, `D24_UNORM_S8_UINT`,
   viste tipizzate).
2. **Il valore è depth lineare in unità Unreal, cioè centimetri.** Unity non
   deve sapere niente di reversed-Z, near/far o matrice di proiezione:
   moltiplica per `depth_scale_to_meters` (0.01, che arriva nel pacchetto
   `STATUS`) e ha i metri.

### A cosa serve

- **Compositing corretto**: Unity può disegnare la propria geometria — marker,
  gizmo di editing, avatar, strumenti di misura — dentro l'immagine di Unreal con
  l'occlusione giusta. Senza depth, tutto ciò che disegna Unity galleggia sopra.
- **Reproiezione / late-latching**: con colore **+** depth puoi deformare in
  Unity il frame di Unreal verso una pose più recente, nascondendo gran parte
  della latenza. È la tecnica che rende praticabile uno split a due processi in
  VR. Col solo colore puoi reproiettare la rotazione ma non la traslazione.

---

## Canale CUBE

### Il vincolo che rende necessario l'atlas

Una shared resource D3D11 deve avere `ArraySize == 1` e `MipLevels == 1`.
Una `TextureCube` ha `ArraySize == 6`. **Una cubemap non è condivisibile**:
`CreateSharedHandle` fallisce e basta.

Soluzione: le 6 facce vengono impacchettate in **una sola texture 2D** disposta
3×2, con sei `CopySubresourceRegion`. Lato Unity, sei `Graphics.CopyTexture`
ricostruiscono una `Cubemap` vera — tutto sulla GPU, nessun readback.

```
+----+----+----+
| +X | -X | +Y |     riga 0 : facce 0,1,2
+----+----+----+
| -Y | +Z | -Z |     riga 1 : facce 3,4,5
+----+----+----+
```

L'ordine 0..5 è quello di D3D11 (e degli indici di subresource di una
`TextureCube`) ed è anche quello di `UnityEngine.CubemapFace`
(`PositiveX = 0` … `NegativeZ = 5`): la corrispondenza è 1:1.

Così l'atlas viaggia sul percorso di trasporto **generico**: stessa shared
texture, stesso keyed mutex, stesso doppio buffer.

### Il costo, che non è piccolo

Una `SceneCaptureComponentCube` costa **sei render completi della scena** per
cattura. Su un mondo streamato scala-Cesium è pesante. Per questo il canale è
**disattivato di default** e i parametri (dimensione faccia, cadenza) esistono:
512 px a 5 Hz è già abbastanza per una reflection probe.

### A cosa serve

`CubemapAssembler` installa la cubemap come
`RenderSettings.customReflectionTexture`. Da quel momento gli oggetti disegnati
da Unity sono illuminati e riflessi dal mondo di Unreal, invece di sembrare
incollati sopra.

---

## Cosa non c'è, e perché

| Canale | Perché no |
|---|---|
| **Motion vectors** | Pagano solo quando implementi davvero la reproiezione con gestione delle disocclusioni. Col modello a canali sono una voce di configurazione, non un refactor. |
| **G-buffer (normal, roughness, albedo)** | Vorrebbe dire ricostruire lo shading di Unreal dentro Unity: l'esatto opposto del motivo per cui esiste questo split. |
| **Object ID buffer** | Interessante per il picking senza raycast in un world builder, ma richiede un render pass custom lato Unreal. |

---

## Migrazione a D3D12

Se e quando servirà Nanite, Lumen o le altre feature SM6, cambia **solo**
l'implementazione dietro `ISharedSurfaceTransport`:

| Resta identico | Cambia |
|---|---|
| Modello a canali e gruppi | `CreateSharedHandle` su `ID3D12Device` |
| Protocollo UDP, intero | Keyed mutex → fence condivise (`ID3D12Fence` ↔ `ID3D11Fence` via `ID3D11Device5::OpenSharedFence`) |
| Strumentazione e marker | — |
| Tutto il lato Unity | — |

Nota non ovvia: con le fence la **misura migliora**. Puoi segnalare al vero
completamento GPU invece che alla submit CPU, quindi `qpc_render_end` — che oggi
va etichettato come "submit" — diventerebbe un dato di latenza reale.
