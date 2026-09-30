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
| `MAIN` (mono) | `COLOR` + `DEPTH` | chiesta da Unity (desktop 1920×1080) | `B8G8R8A8_UNORM` + `R32_FLOAT` | free-run, cap configurabile |
| `MAIN` (stereo) | `COLOR` + `DEPTH` + `COLOR_1` + `DEPTH_1` | per occhio, dall'eye texture del visore | idem | idem |
| `CUBE` | `CUBE_ATLAS` | 1536×1024 (3×2 × 512) | `B8G8R8A8_UNORM` | 5 Hz, configurabile |

In stereo i due occhi stanno **nello stesso gruppo**: vengono pubblicati
atomicamente, quindi Unity non può mai mostrare l'occhio sinistro del frame N
col destro del frame N−1 — errore che in un visore si percepisce subito come
fastidio agli occhi, prima ancora di capire cosa sia.

**Due canali per vista e non una texture side-by-side**: si debugga molto
meglio (un occhio alla volta) e non vincola i due occhi a stessa risoluzione e
formato. Il side-by-side dimezzerebbe le operazioni sui mutex; se mai quel costo
comparisse nelle misure, la macchina per farlo esiste già (è la stessa
dell'atlas cubemap).

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
  Se entrambi sono occupati (consumatore indietro o morto), **salta il frame**.
  Timeout **0**: vedi sotto perché.
- **Consumatore**: consuma **solo** quando arriva uno STATUS con una sequenza
  nuova. Unreal manda lo STATUS dopo aver rilasciato il buffer, quindi a quel
  punto il buffer è pronto e contiene proprio il frame annunciato. Nei frame
  senza sequenza nuova Unity ripresenta il precedente e conta una ripetizione.

### L'id dell'evento porta i dati del consume

Il render thread di Unity esegue il frame N mentre il main thread prepara già
il frame N+1. In v1 l'indice del buffer da consumare stava in una variabile
condivisa, che il main thread del frame N+1 sovrascriveva prima che il render
thread avesse consumato il frame N. Ora l'id passato a `GL.IssuePluginEvent`
codifica tipo, indice del buffer, richiesta di drenaggio e sequenza: ogni
consume porta con sé esattamente i dati decisi per il suo frame. La codifica è
testata sui casi limite, e in entrambe le direzioni tra C# e C.

### Drenaggio dei buffer mai consumati

Se Unity è più lento di Unreal, può ricevere due STATUS nello stesso frame
(sequenza *n* nel buffer A, *n+1* nel buffer B) e consumare solo B. Ma A resta
"pronto per il consumatore" per sempre: Unreal non può più riscriverlo. Il
consume di B chiede quindi al plugin di **drenare** A: acquisirlo e restituirlo
subito al produttore, senza copiarlo. È sicuro: finché A è nello stato
"consumatore", Unreal non può averci scritto nulla di nuovo.

Rete di sicurezza per quando lo STATUS che annunciava A va perso: il produttore
preferisce sempre il buffer diverso dall'ultimo, quindi se pubblica **tre volte
di fila nello stesso buffer** l'altro gli è inacquisibile, cioè è bloccato con un
frame vecchio. Anche in quel caso si drena.

### Verificato con una simulazione

La logica di scambio è stata simulata (porta fedele del codice di Unreal e di
Unity, keyed mutex modellati, render thread di Unity in ritardo di 0.3–0.9
frame, fino al 20% di STATUS persi). In tutti i casi:

- un consume riuscito copia **sempre** il frame annunciato dallo STATUS usato
  (altrimenti la riproiezione VR userebbe le matrici di un altro frame);
- un drenaggio non butta **mai** un frame più nuovo di quello consumato;
- nessuno stallo permanente.

La simulazione ha anche trovato un errore del design precedente: con il timeout
dell'acquire del produttore a 2 ms, quando Unreal è più veloce di Unity il
render thread di Unreal restava **bloccato fino a metà del tempo** (543 ms al
secondo nel caso peggiore), senza consegnare un solo frame in più. Con timeout
0 il blocco sparisce e throughput e freschezza restano identici o migliorano.
Da qui il default a 0.

**Limite noto del doppio buffer**: se Unreal è molto più veloce di Unity (per
esempio 200 contro 72 Hz) e il render thread di Unity è molto in ritardo, circa
un frame di Unity su quattro ripete il precedente, perché per buona parte del
frame entrambi i buffer sono occupati. Quando Unreal va alla stessa frequenza di
Unity o più piano — il caso tipico in VR stereo — ogni frame di Unreal arriva.
Rimedio immediato: cappare Unreal vicino alla frequenza del visore. Rimedio
strutturale: il **triplo buffer** (un buffer in scrittura, uno pronto, uno in
consumo), che richiede di estendere il protocollo a 3 handle per canale.

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
