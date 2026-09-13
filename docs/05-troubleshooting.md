# Troubleshooting

Tabella sintomo → causa → rimedio. I sintomi sono ordinati per quanto è probabile
che ci sbatti contro.

---

## Adapter e GPU

### `OpenSharedResource1 fallita: hr=0x80070057` (E_INVALIDARG) o `0x80070005` (E_ACCESSDENIED)

**Causa quasi certa: i due processi sono su GPU diverse.** Tipico su laptop
ibridi (Intel iGPU + NVIDIA/AMD dGPU): Windows manda Unreal sulla discreta e
Unity sull'integrata, o viceversa.

L'HUD di Unity te lo dice esplicitamente (*"MISMATCH DI ADAPTER"*) perché i due
LUID viaggiano nell'handshake apposta.

**Rimedio.** *Impostazioni Windows → Sistema → Schermo → Impostazioni grafica*
(su Windows 11: *Schermo → Grafica*). Aggiungi **entrambi** gli eseguibili e
imposta la **stessa** preferenza GPU:

- `...\unreal\GpuShareSpike\Binaries\Win64\GpuShareSpike.exe`
- `...\Unity\Hub\Editor\<versione>\Editor\Unity.exe` (o il tuo Player buildato)

In alternativa, lato Unreal puoi forzare l'adapter da riga di comando:

```
-graphicsadapter=0
```

(gli indici sono elencati nel log all'avvio, sotto `LogD3D11RHI`).

> Sul desktop finale con una sola GPU il problema sparisce. Ma **verificalo**
> comunque: un secondo monitor su un'uscita diversa può bastare a far cambiare
> idea a Windows.

---

## Handle e privilegi

### `OpenProcess(PROCESS_DUP_HANDLE, pid=...) fallita, GetLastError=5`

`5` è `ERROR_ACCESS_DENIED`. **Causa: i due processi hanno integrity level
diversi**, cioè uno gira come amministratore e l'altro no.

**Rimedio 1 (preferibile).** Non lanciare né l'editor di Unreal né Unity come
amministratore. Se hai lanciato Visual Studio come admin, anche l'editor che
avvia da lì eredita l'elevazione.

**Rimedio 2 (fallback).** *Project Settings → Plugins → GPU Share Spike →*
**Use Named Shared Handles** ☑. Passa agli handle **nominati**: Unreal li crea
con un nome e Unity li apre con `OpenSharedResourceByName`. Niente PID, niente
`DuplicateHandle`, nessun privilegio richiesto.

Il prefisso di default è `Local\GpuShareSpike`, e i nomi completi sono
`<prefisso>_ch<channel_id>_b<index>`. I due lati li costruiscono con la stessa
formula: se cambi il prefisso lato Unreal, arriva a Unity nell'handshake.

### `handle nullo per canale N buffer M: Unreal non ha duplicato nulla`

L'handshake è arrivato ma con handle a zero. Guarda il log di Unreal: la
duplicazione è fallita prima, e lì trovi il motivo vero.

---

## RHI

### `RHI 'D3D12' non supportato: serve D3D11`

Unreal sta girando su D3D12. La forzatura non ha avuto effetto.

**Rimedio, in ordine:**

1. *Project Settings → Platforms → Windows → Default RHI* = **DirectX 11**, poi
   **riavvia l'editor** (la scelta dell'RHI si fa all'avvio del processo).
2. Aggiungi `-d3d11` alla riga di comando.
3. Controlla che `Config/DefaultEngine.ini` contenga davvero:
   ```ini
   [/Script/WindowsTargetPlatform.WindowsTargetSettings]
   DefaultGraphicsRHI=DefaultGraphicsRHI_DX11
   ```
4. Verifica nel log: devono esserci righe `LogD3D11RHI` e **nessuna** `LogD3D12RHI`.

### Unity: `Unity sta girando su Direct3D12, serve Direct3D11`

*Player Settings → Other Settings*: togli **Auto Graphics API for Windows** e
lascia **solo** `Direct3D11` nella lista. Riavvia l'editor di Unity.

---

## Plugin nativo

### `DllNotFoundException: UnityGpuShare`

La DLL non è in `Assets/Plugins/x86_64/`, oppure le impostazioni di import non
includono la piattaforma giusta. Selezionala nel Project window e verifica:
☑ `Editor`, ☑ `Standalone`, CPU `x86_64`, OS `Windows`.

### `EntryPointNotFoundException` su una funzione che esiste nel sorgente

**Quasi sempre è una DLL vecchia rimasta in memoria.** L'editor di Unity tiene
la DLL aperta per tutta la sessione: se hai ricompilato con l'editor aperto, o
la copia è fallita, o l'editor sta ancora eseguendo la versione precedente.

**Rimedio:** chiudi l'editor → ricompila → riapri. Se iteri spesso sul codice
nativo, lavora su un **Player buildato**.

### Il plugin compila ma `GpuShare_IsDeviceReady()` resta 0

`UnityPluginLoad` non ha trovato un `ID3D11Device`. Cause possibili:

- Unity non è su D3D11 (vedi sopra);
- la DLL è a 32 bit: `cmake -B build -A x64`, non `-A Win32`.

---

## Immagine

### Schermo nero, ma l'HUD dice "in esecuzione" e i contatori salgono

Il trasporto funziona, il problema è nella presentazione.

1. Metti `Debug Mode = 2` (zoom sui pixel del marker): se vedi otto blocchi
   colorati che cambiano, i pixel **arrivano** e il problema è solo nel quad.
2. Controlla che la `Main Camera` non sia coperta da un'altra camera con
   priorità più alta.
3. Verifica che il materiale sia stato creato: se `Present Shader` è vuoto e lo
   shader non è negli *Always Included Shaders*, in un Player `Shader.Find`
   restituisce `null` (nell'Editor funziona — è il classico bug che appare solo
   in build).

### Immagine troppo scura o troppo chiara / slavata

È la gestione del colore. Prova a invertire **SRGB Decode** su `ShareClient`.

Il canale COLOR contiene il final color LDR di Unreal, che è codificato sRGB. La
texture esterna è creata con `linear: true`, quindi Unity **non** converte al
campionamento e la decodifica la fa il nostro shader. In un progetto Linear color
space il valore giusto è **acceso**; se hai cambiato il Color Space del progetto
a Gamma, va spento.

Questo non tocca la strumentazione: gli 8 pixel del marker sono letti in CPU come
byte grezzi e non passano mai da un sampler.

### L'HUD dice "marker non valido: magic/checksum non tornano"

I bit dei pixel vengono alterati lungo la catena. Da controllare, nell'ordine:

1. Il canale COLOR è davvero `B8G8R8A8_UNORM` (`dxgi_format = 87` nel log di
   Unreal)?
2. Qualcosa ha reintrodotto un filtraggio: `filterMode` deve restare `Point` e la
   lettura deve avvenire nella staging texture del nativo, non in uno shader.
3. Il `MarkerReader` sta leggendo la texture giusta (quella col flag
   `HAS_MARKER`).

### Il depth è tutto nero o tutto bianco

Il canale DEPTH è **lineare in centimetri**. Alza o abbassa `Depth Range M` su
`ShareClient`: il default (50 m) è tarato sulla scena di prova; su un mondo
grande serve molto di più.

---

## Prestazioni e latenza

### Latenza molto più alta del previsto (> 50 ms)

Da controllare, in ordine di probabilità:

1. **Vsync acceso da qualche parte.** Unreal: `r.VSync=0` e `bSmoothFrameRate=False`
   (già nel `DefaultEngine.ini`). Unity: *Quality → V Sync Count → Don't Sync*.
2. **Play In Editor invece di Standalone** lato Unreal: il PIE condivide il
   renderer con l'editor. Usa Standalone.
3. **La finestra principale di Unreal è grande.** Renderizza la scena in più,
   oltre alle SceneCapture. Rimpiccioliscila:
   `-ResX=640 -ResY=360 -windowed` nei launch parameters.
4. **Canale CUBE acceso** a cadenza alta: sono sei render completi per cattura.
5. `Main Capture Hz` impostato basso: stai cappando tu la cattura.

### Tutto scatta di brutto quando Unreal non ha il focus

Non è il trasporto: **è Unreal che si strozza da solo quando la sua finestra non
è in primo piano.** Guarda l'HUD mentre dai il fuoco a Unity — se gli **FPS di
Unreal** crollano, è throttling; se restano alti e salgono solo le
**ripetizioni**, allora è davvero il trasporto (ma non succederà).

Rimedi, in ordine:

1. **Editor Preferences → Performance → *Use Less CPU when in Background*** →
   **disattivalo**. È il colpevole numero uno quando lanci lo Standalone
   dall'editor: nonostante il nome parli dell'editor, è la cosa che la gente
   segnala come fix per le istanze Standalone non focalizzate.
2. **`t.IdleWhenNotForeground 0`** — questa CVar sospende *rendering e tick*
   quando la finestra non è in foreground. Verifica che non sia a 1.
3. Nei **build packaged** la voce dell'editor non ha effetto: lì il
   comportamento è a livello di engine/OS.
4. **Fai a meno della finestra di Unreal.** In questo spike Unreal non ha alcun
   bisogno di una finestra visibile: renderizza nelle SceneCapture. Prova ad
   avviarlo con `-RenderOffScreen`. Se funziona, il problema del focus sparisce
   del tutto perché non c'è più una finestra che possa perderlo — ed elimini
   anche il costo del render della viewport principale.
5. Come ripiego, tieni entrambe le finestre visibili e **non focalizzate**
   (clicca sul desktop): quando nessuna delle due ha il fuoco, il throttling non
   scatta su nessuna delle due.

### Tante ripetizioni (`ripetizioni` sale in fretta)

Unity gira più veloce di Unreal: ci sono più frame di Unity che frame prodotti da
Unreal, quindi Unity ridisegna l'ultimo che ha. **È il comportamento corretto**,
non un bug — ed è esattamente il numero che quel contatore esiste per darti.

Confronta i due FPS nell'HUD. Se vuoi allinearli, cappa Unity
(`Application.targetFrameRate`) o alza il frame rate di Unreal.

### Unreal logga tanti frame saltati

Il contrario: Unity è più lento e non libera i buffer abbastanza in fretta.
Unreal salta la pubblicazione invece di bloccare il proprio render thread — anche
questo è il comportamento voluto.

---

## Rete

### Unity resta in "in attesa dell'handshake di Unreal..."

1. Unreal è partito? Cerca nel suo log `Canale di controllo in ascolto su UDP`.
2. La porta combacia? Unreal `ListenPort` (default 45001) e Unity `UnrealPort`.
3. Firewall: su localhost di solito non interviene, ma qualche security suite sì.
   Prova a disattivarla temporaneamente per escluderla.
4. Un'altra istanza di Unreal sta già occupando la porta? Il log lo direbbe:
   `Impossibile aprire il socket UDP sulla porta 45001 (gia' occupata?)`.

### `status ricevuti` resta a 0 ma l'handshake è arrivato

Unreal manda lo `STATUS` solo dopo aver pubblicato un frame, e pubblica solo se
ha ricevuto almeno una pose **e** ha un client. Se `pose inviate` sale ma
`status ricevuti` no, guarda il log di Unreal: probabilmente la pubblicazione
fallisce (cerca righe di errore del gruppo MAIN).
