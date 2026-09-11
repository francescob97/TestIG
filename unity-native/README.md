# UnityGpuShare — plugin nativo

Lato consumatore del trasporto: apre le texture condivise create da Unreal, le
copia in texture di proprietà di Unity sotto keyed mutex, e legge i 32 byte del
marker di strumentazione dai pixel.

## Build

Serve **Visual Studio 2022** (workload *Desktop development with C++*) e **CMake ≥ 3.20**.

Da una *x64 Native Tools Command Prompt for VS 2022*:

```bat
cd unity-native
cmake -B build -A x64 -DUNITY_PLUGIN_API_DIR="C:/Program Files/Unity/Hub/Editor/2022.3.62f1/Editor/Data/PluginAPI"
cmake --build build --config Release
```

Sostituisci `2022.3.62f1` con la versione che hai davvero installata.
La DLL finisce da sola in `unity/GpuShareSpike/Assets/Plugins/x86_64/UnityGpuShare.dll`.

## Il problema del lock della DLL (leggilo prima di perderci tempo)

**L'editor di Unity tiene la DLL aperta per tutta la sessione.** Se ricompili
mentre l'editor è aperto ottieni uno di questi due comportamenti, entrambi
confusi:

- la copia post-build fallisce con *access denied*;
- la copia riesce ma l'editor continua a eseguire la versione **vecchia**
  finché non lo riavvii.

Ciclo di iterazione corretto:

1. chiudi l'editor di Unity;
2. `cmake --build build --config Release`;
3. riapri l'editor.

Se stai iterando spesso sul codice nativo conviene lavorare su un **Player
buildato** (`Build And Run`) invece che nell'editor: il player carica la DLL
all'avvio e la rilascia alla chiusura, quindi basta ribuildare ed eseguire.

## Struttura

| File | Ruolo |
|---|---|
| `src/NativeApi.h` | struct scambiate con il C# (`GpuShareFrameInfo`, `GpuShareNativeStats`) |
| `src/Exports.h` | superficie C esportata, è il contratto che `NativeBridge.cs` rispecchia |
| `src/UnityPluginEntry.cpp` | hook di Unity, stato globale, implementazione degli export |
| `src/ChannelConsumer.*` | `OpenSharedResource1`, keyed mutex, copia verso le texture Unity |
| `src/MarkerReader.*` | ring di staging 8×1 + `Map` non bloccante, decodifica byte grezzi |

`ShareProtocol.h` arriva da `../shared/` — è **lo stesso identico file** incluso
dal modulo Unreal. Se cambi il protocollo, cambialo lì e basta: gli
`static_assert` sui `sizeof` fanno fallire la compilazione di entrambi i lati
se qualcosa non torna.

## Convenzione di thread

- Tutte le funzioni `GpuShare_*` si chiamano dal **main thread** di Unity.
- Il lavoro D3D11 avviene **solo** nella callback restituita da
  `GpuShare_GetRenderEventFunc()`, che Unity esegue sul **render thread** quando
  il C# chiama `GL.IssuePluginEvent`.

Non è pedanteria: l'immediate context D3D11 di Unity non è thread-safe, e
usarlo dal main thread produce corruzione casuale, non un errore.
