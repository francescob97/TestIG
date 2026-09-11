# Build del lato Unity, passo per passo

---

## 0. Prerequisiti

| Cosa | Note |
|---|---|
| **Unity 2022.3 LTS** | Da Unity Hub. Serve il modulo *Windows Build Support (IL2CPP)* solo se vuoi buildare un Player. |
| **Visual Studio 2022** | Lo stesso che usi per Unreal: serve a compilare il plugin nativo. |
| **CMake ≥ 3.20** | [cmake.org](https://cmake.org/download/) oppure `winget install Kitware.CMake`. |

---

## 1. Crea il progetto Unity

Unity Hub → **New project** → template **3D (URP)** → posizionalo in
`unity/GpuShareSpike`.

> I file `ProjectSettings/*.asset` di Unity sono YAML fragili e non sono
> versionati in questo repository: generare a mano un progetto Unity valido
> produrrebbe errori opachi al primo avvio. Crealo dal Hub e applica la
> checklist qui sotto — sono due minuti e sai esattamente cosa hai impostato.

---

## 2. Checklist di Project Settings

**Ogni riga conta.** Sono tutte cose che, se sbagliate, producono un sintomo
che sembra un bug del trasporto.

### Player → Other Settings

| Impostazione | Valore | Perché |
|---|---|---|
| **Color Space** | `Linear` | Default di URP. Determina come va interpretato il canale COLOR. |
| **Auto Graphics API for Windows** | ☐ **disattivato** | |
| **Graphics APIs for Windows** | **solo `Direct3D11`** | Con D3D12 o Vulkan il plugin non trova un `ID3D11Device` e si ferma con un errore esplicito. |
| **Api Compatibility Level** | `.NET Standard 2.1` | Serve per `Span<T>` e `BinaryPrimitives`. |

### Player → Resolution and Presentation

| Impostazione | Valore | Perché |
|---|---|---|
| **Run In Background** | ☑ **attivo** | Senza questo, Unity si ferma appena dai il fuoco a Unreal — e tu passerai il tempo ad alternare le due finestre. |
| **Fullscreen Mode** | `Windowed` | Più comodo con due processi a schermo. |

### Quality

| Impostazione | Valore | Perché |
|---|---|---|
| **V Sync Count** | `Don't Sync` | Il vsync aggiunge un'attesa che non c'entra con l'anello misurato. |

### Graphics

**Always Included Shaders** → aggiungi **`GpuShare/Present`**.

Serve solo nel Player buildato (nell'Editor `Shader.Find` trova tutto). In
alternativa assegna lo shader al campo `Present Shader` del componente
`ShareClient` nell'inspector, che funziona in entrambi i casi ed è più esplicito.

---

## 3. Compila il plugin nativo

Da una **x64 Native Tools Command Prompt for VS 2022**:

```bat
cd unity-native
cmake -B build -A x64 -DUNITY_PLUGIN_API_DIR="C:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/PluginAPI"
cmake --build build --config Release
```

Sostituisci `2022.3.62f1` con la tua versione. La DLL viene copiata da sola in
`unity/GpuShareSpike/Assets/Plugins/x86_64/UnityGpuShare.dll`.

> ### ⚠ L'editor di Unity tiene la DLL aperta
>
> Se ricompili mentre l'editor è aperto, o la copia fallisce con *access denied*,
> o riesce ma **l'editor continua a eseguire la versione vecchia**. Il secondo
> caso è il peggiore perché non dà nessun errore.
>
> Ciclo corretto: **chiudi l'editor → ricompila → riapri**.
>
> Se stai iterando spesso sul nativo, lavora su un **Player buildato**: carica la
> DLL all'avvio e la rilascia alla chiusura.

### Impostazioni di import della DLL

Seleziona `UnityGpuShare.dll` nel Project window e nell'inspector:

- **Select platforms for plugin**: ☑ `Editor`, ☑ `Standalone`
- **Platform settings → CPU**: `x86_64`
- **Editor settings → OS**: `Windows`

---

## 4. Copia gli script

Copia in `Assets/`:

```
Assets/Scripts/*.cs       (7 file)
Assets/Shaders/GpuSharePresent.shader
```

Sono già nelle posizioni giuste se hai creato il progetto Unity dentro
`unity/GpuShareSpike`.

---

## 5. Monta la scena (6 passi)

1. `File → New Scene` → template **Basic (URP)**
2. Seleziona **Main Camera** nella Hierarchy
   *(non devi cambiare niente: `ShareClient` la mette in ortografica da solo)*
3. `GameObject → Create Empty`, rinominalo **`GpuShareClient`**
4. Con quello selezionato, `Add Component` tre volte:
   - `ShareClient`
   - `VirtualCameraDriver`
   - `HudOverlay`
5. Nell'inspector di `ShareClient`:
   - **Present Camera** → trascinaci la `Main Camera`
   - **Present Shader** → trascinaci `GpuSharePresent`
6. `File → Save As…` → `Assets/Scenes/SpikeScene.unity`

Il quad a schermo intero viene creato a runtime da `ShareClient` e agganciato
alla camera: non c'è niente da posizionare a mano.

---

## 6. Ordine di avvio

**Unreal per primo, poi Unity** — anche se non è obbligatorio: Unity ripete
l'`HELLO` ogni mezzo secondo finché Unreal non risponde, quindi puoi avviarli in
qualunque ordine e riavviare Unreal senza riavviare Unity.

1. Unreal: **Standalone Game**, aspetta la riga `Canale di controllo in ascolto`
2. Unity: **Play**

Entro un secondo l'HUD deve passare da *"in attesa dell'handshake"* a
*"in esecuzione"* e mostrare i numeri.

---

## 7. Cosa leggi nell'HUD

```
ANELLO POSE -> PIXEL
  latenza          23.41 ms   (media 24.08)
  min / max        18.77 / 41.25 ms
  eta' del frame       2 frame Unity
  di cui Unreal     9.12 ms fino alla submit
  frame_id         18342
```

| Riga | Cosa significa davvero |
|---|---|
| **latenza** | `qpc_consume − marker.qpc_pose_send`. Il tempo tra quando Unity ha spedito quella pose e quando i pixel corrispondenti sono diventati disponibili a Unity. **Misurato**, non stimato: entrambi i timestamp vengono dallo stesso QPC di sistema. |
| **eta' del frame** | Quanti frame di Unity sono passati tra l'invio della pose e l'arrivo dei suoi pixel. |
| **di cui Unreal** | `marker.qpc_render_end − marker.qpc_pose_send`, cioè la parte spesa da Unreal fino alla **submit** della copia. Non è il completamento GPU: serve solo a spezzare l'intervallo. |
| **ripetizioni** | Quante volte il consume è andato in timeout, cioè quante volte Unity ha ridisegnato il frame precedente perché non ce n'era uno nuovo. |
| **ritardo lettura** | Con quanti eventi di ritardo abbiamo *saputo* il valore. **Non entra nella misura**: sposta solo quando la leggiamo. |

Tasti utili: **F1** nasconde l'HUD. Il campo `Debug Mode` di `ShareClient`
(0/1/2) commuta tra colore, depth in scala di grigi e zoom sugli 8 pixel del
marker.

---

## 8. Attivare depth e cubemap

- **Depth**: è già attivo da entrambi i lati. Metti `Debug Mode = 1` per vederlo.
- **Cubemap**:
  1. lato Unreal, *Project Settings → Plugins → GPU Share Spike →*
     **Enable Cube Channel** ☑, e riavvia lo Standalone;
  2. lato Unity, su `ShareClient`, **Want Cube** ☑.

  `CubemapAssembler` ricompone una `Cubemap` vera dall'atlas 3×2 e la installa
  come `RenderSettings.customReflectionTexture`: da quel momento gli oggetti
  disegnati da Unity riflettono il mondo di Unreal.

  Ricorda che ogni cattura cubemap costa **sei render completi** della scena
  lato Unreal.
