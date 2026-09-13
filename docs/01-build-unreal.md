# Build del lato Unreal, passo per passo

Scritto assumendo che tu non conosca Unreal. Ogni passo dice anche **come
verificare** che sia andato a buon fine, perché in Unreal un passo mancato si
manifesta tre passi dopo con un errore che non c'entra.

---

## 0. Prerequisiti

| Cosa | Note |
|---|---|
| **Visual Studio 2022** | Installer → workload **Desktop development with C++**. Nei componenti singoli servono anche **Windows 11 SDK** e **MSVC v143**. La Community Edition va benissimo. |
| **Unreal Engine 5.7** | Da Epic Games Launcher → Unreal Engine → Library → `+`. |
| **~120 GB liberi** | Non è un refuso: engine, derived data cache e intermediate. |

> Se usi Visual Studio Code o Rider invece di Visual Studio, ti serve **comunque**
> l'installazione di Visual Studio: Unreal usa il compilatore MSVC e i suoi SDK,
> non l'IDE.

---

## 1. Verifica che D3D11 esista davvero nella tua 5.7

**Fallo adesso**, prima di compilare qualunque cosa. Tre controlli:

1. **La cartella dell'RHI c'è?**
   ```
   <installazione UE>\Engine\Source\Runtime\Windows\D3D11RHI\
   ```
   Se esiste, l'RHI è nell'engine.

2. **L'opzione è nel menù?** Apri un qualunque progetto, poi
   *Edit → Project Settings → Platforms → Windows*.
   Nella tendina **Default RHI** deve comparire **DirectX 11**.

3. **Si avvia davvero?** Lancia l'editor con `-d3d11` e cerca in
   `Saved/Logs/<Progetto>.log` righe con categoria `LogD3D11RHI`.
   Devono esserci, e **non** deve esserci nessuna `LogD3D12RHI`.

Se uno dei tre fallisce, fermati: questo spike parte dal presupposto che la
texture RHI di Unreal **sia già** una `ID3D11Texture2D`.

---

## 2. Genera i file di progetto di Visual Studio

Un progetto Unreal C++ non ha un `.sln` versionato: si rigenera.

1. Tasto destro su `unreal/GpuShareSpike/GpuShareSpike.uproject`
2. **Generate Visual Studio project files**

**Se la voce non compare nel menù contestuale**, l'associazione delle estensioni
non è registrata. Rimediala così:

```bat
"C:\Program Files\Epic Games\UE_5.7\Engine\Binaries\Win64\UnrealVersionSelector.exe" /fileassociations
```

**Se compare ma fallisce**, quasi sempre è il campo `EngineAssociation` nel
`.uproject` che non combacia con la versione installata. Aprilo con un editor di
testo e controlla che dica `"5.7"`.

Al termine trovi `GpuShareSpike.sln` accanto al `.uproject`.

---

## 3. Compila

### Da Visual Studio

1. Apri `GpuShareSpike.sln`
2. In alto: configurazione **Development Editor**, piattaforma **Win64**
3. `Build → Build Solution` (la prima volta sono 5–20 minuti)

### Da riga di comando

```bat
"C:\Program Files\Epic Games\UE_5.7\Engine\Build\BatchFiles\Build.bat" ^
    GpuShareSpikeEditor Win64 Development ^
    -Project="%CD%\unreal\GpuShareSpike\GpuShareSpike.uproject" -WaitMutex
```

> **Nota su UnrealHeaderTool.** Prima di compilare il C++ vero, Unreal esegue
> UHT, che analizza gli header contenenti `UCLASS`/`USTRUCT` e genera i file
> `*.generated.h`. Se vedi errori tipo *"Unknown class specifier"* o
> *"Missing generated header"*, è UHT che si è fermato: il problema è quasi
> sempre in un header, non nel `.cpp`. In questo progetto gli header che UHT
> analizza (`GpuShareCaptureActor.h`, `GpuShareSettings.h`,
> `GpuShareSceneSubsystem.h`) **non includono niente di D3D11** apposta: UHT si
> confonde con le intestazioni Windows.

---

## 4. Crea la mappa (l'unico passo manuale)

Tutta la scena — piano, cubi, luci, attore di cattura — è costruita in C++ da
`UGpuShareSceneSubsystem`. Serve però **un** livello in cui costruirla, e un
`.umap` è un file binario che non ha senso versionare.

1. Avvia l'editor (tasto `F5` da Visual Studio, o doppio clic sul `.uproject`)
2. `File → New Level… → Empty Level`
3. `File → Save Current Level As…` → cartella `Maps`, nome **`SpikeMap`**

Il percorso deve risultare `/Game/Maps/SpikeMap`, che è quello già scritto in
`Config/DefaultEngine.ini`. Se lo salvi altrove, aggiorna quelle due righe.

---

## 5. Verifica la configurazione

*Edit → Project Settings*:

| Percorso | Valore atteso |
|---|---|
| Platforms → Windows → **Default RHI** | **DirectX 11** |
| Plugins → **GPU Share Spike** | la pagina dei parametri dello spike |

Se cambi il Default RHI, l'editor chiede di riavviare: la scelta dell'RHI si fa
all'avvio del processo, non si può cambiare a caldo.

---

## 6. Avvia in Standalone

**Non usare Play In Editor** per le misure: il PIE condivide il renderer con
l'editor e ti aggiunge diversi millisecondi che non c'entrano nulla con l'anello
che stai misurando.

Dalla freccetta accanto al pulsante **Play** → **Standalone Game**.

### Riduci il costo della finestra principale

Anche in Standalone, Unreal renderizza *anche* la vista della finestra
principale, oltre alle nostre SceneCapture. È utile per vedere cosa succede, ma
è lavoro in più che gonfia il frame time di Unreal.

Per una misura pulita, rimpicciolisci quella finestra:
*Editor Preferences → Level Editor → Play → **Additional Launch Parameters***:

```
-ResX=640 -ResY=360 -windowed
```

---

## 7. Conferma che sia partito

In `Saved/Logs/GpuShareSpike.log` devi trovare, in quest'ordine:

```
LogGpuShare: RHI attivo: 'D3D11'  (feature level max: ...)
LogGpuShare: Adapter D3D11 di Unreal: '<nome GPU>' LUID=...
LogGpuShare: [gruppo 0] canale 0 creato: 1920x1080, DXGI_FORMAT=87, marker=si
LogGpuShare: [gruppo 0] canale 1 creato: 1920x1080, DXGI_FORMAT=41, marker=no
LogGpuShare: Superfici condivise pronte.
LogGpuShare: Canale di controllo in ascolto su UDP 0.0.0.0:45001
LogGpuShare: Scena costruita: piano + 3 cubi + luce + cielo + attore di cattura.
```

`DXGI_FORMAT=87` è `B8G8R8A8_UNORM`, `41` è `R32_FLOAT`.

Quando Unity si collega:

```
LogGpuShare: HELLO da Unity: pid=... LUID=... flags=0x1
LogGpuShare: [gruppo 0] handle duplicati nel processo Unity pid=...
LogGpuShare: HANDSHAKE spedito (256 byte, 2 canali)
```

Se ti fermi prima di una di queste righe, vai a
[`05-troubleshooting.md`](05-troubleshooting.md).

---

## 8. Parametri che vorrai toccare

*Project Settings → Plugins → GPU Share Spike*:

| Parametro | Default | A cosa serve |
|---|---|---|
| `Main Capture Hz` | 0 | Cadenza del gruppo COLOR+DEPTH. **0 = free-run**, una cattura per frame del motore. Mettilo a 30 o 60 per disaccoppiare la cattura dal frame rate. |
| `Engine Max FPS` | 0 | Cap del frame rate di Unreal (`t.MaxFPS`). 0 = illimitato. |
| `Enable Depth Channel` | ✅ | Secondo canale, `R32_FLOAT`, depth lineare in centimetri. |
| `Enable Cube Channel` | ❌ | **Costa sei render completi della scena** per cattura. |
| `Cube Capture Hz` | 5 | Cadenza del gruppo CUBE, indipendente da quella del MAIN. |
| `Producer Acquire Timeout Ms` | 2 | Quanto Unreal aspetta un buffer libero prima di **saltare** il frame. Non alzarlo troppo: bloccare il render thread di Unreal per colpa di Unity è peggio che perdere un frame. |
| `Use Named Shared Handles` | ❌ | Fallback se `DuplicateHandle` fallisce. Vedi troubleshooting. |
| `Render Fov Margin Deg` | 0 | Renderizza più largo di quanto Unity mostra, per il re-crop tardivo. |
| `Pose Relative To Anchor` | ✅ | La pose di Unity è **relativa** all'attore di cattura, che fa da **ancora**: l'origine del mondo Unity coincide con la transform di quell'attore, e quella transform resta libera per chi la deve comandare. Spegnilo per avere pose di mondo assolute. |
