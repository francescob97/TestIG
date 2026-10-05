# DevConsole: la console in gioco, stile Unreal

Una console da aprire con un tasto mentre il gioco gira, per scrivere comandi:
accendere e spegnere GameObject e componenti, chiamare metodi, mandare
messaggi, leggere e cambiare valori. Come la `~` di Unreal.

Codice in [`unity/GpuShareSpike/Assets/Scripts/DevConsole/`](../unity/GpuShareSpike/Assets/Scripts/DevConsole/).
La cartella non dipende da nient'altro dello spike: si copia così com'è in
qualunque progetto Unity.

---

## Aprirla

| Tasto | |
|---|---|
| **`\`** | il tasto a sinistra dell'**1** sulla tastiera italiana |
| **`` ` ``** o **`~`** | su tastiera US, come in Unreal |
| **F2** | sempre, qualunque sia il layout |
| **Esc** | chiude |

**Non c'è niente da montare.** Nell'editor e nei *Development Build* la
console si crea da sola al primo caricamento di scena, e sopravvive ai cambi di
scena. In un build di release non c'è, come in Shipping di Unreal.

- Per averla anche in release: aggiungi il componente `DevConsole` a un
  GameObject radice della prima scena.
- Per non volerla mai creata da sola: aggiungi `DEVCONSOLE_NO_AUTOCREATE` in
  *Project Settings → Player → Scripting Define Symbols*.
- Per cambiare tasti, altezza, font o scala: in Play seleziona
  `[DevConsole]` sotto *DontDestroyOnLoad* nella Hierarchy; per renderlo
  permanente metti tu il componente in scena e regolalo lì.

---

## Dentro la console

| | |
|---|---|
| **Tab** / **Shift+Tab** | completa e scorre i candidati: comandi, nomi di GameObject, componenti, metodi, campi, valori degli enum |
| **↑ / ↓** | cronologia (resta anche tra una sessione e l'altra) |
| **PagSu / PagGiù**, rotella | scorre il log |
| `"Main Camera"` | virgolette per i nomi con spazi |
| `cmd1 ; cmd2` | più comandi sulla stessa riga |

Mentre scrivi il nome di un comando compaiono i comandi che corrispondono;
dopo il nome, la sua riga d'uso. In console finiscono anche i `Debug.Log`, i
warning e gli errori di Unity.

---

## Comandi predefiniti

`<go>` è un nome (`Player`) o un percorso nella gerarchia
(`Level/Doors/Door 2`). **Trova anche gli oggetti inattivi**, a differenza di
`GameObject.Find`.

| Comando | Cosa fa |
|---|---|
| `help [comando\|testo]` | elenca i comandi, o mostra come si usa uno |
| `find [testo]` | cerca GameObject per nome; senza testo elenca le radici delle scene |
| `inspect <go>` (`ls`) | stato, componenti, figli, posizione |
| `enable <go> [Componente]` | attiva il GameObject, o abilita un suo componente |
| `disable <go> [Componente]` | lo spegne |
| `toggle <go> [Componente]` | inverte |
| `call <go> [Componente] <metodo> [arg...]` | chiama un metodo, **anche privato**. Se il metodo restituisce un valore lo stampa; se è una coroutine la avvia |
| `send <go\|*> <messaggio> [arg...]` | come `GameObject.SendMessage`: chiama il metodo su ogni script del GameObject. `*` = tutti gli oggetti attivi |
| `broadcast <go> <messaggio> [arg...]` | come `BroadcastMessage`: anche sui figli attivi |
| `get <go> <Componente> [campo]` | legge un campo o una proprietà; senza campo li elenca |
| `set <go> <Componente> <campo> <valore>` | li scrive, **anche privati** |
| `timescale [valore]` (`slomo`) | `Time.timeScale`; `0` = pausa |
| `scene <nome\|indice>`, `reload` | carica una scena delle Build Settings, ricarica quella attiva |
| `echo`, `clear` (`cls`), `quit` | |

Al posto del nome di un componente si può scrivere **`GameObject`** per
l'oggetto stesso: `set Cube GameObject layer 8`, `call Cube GameObject SetActive false`.

### Esempi

```
find porta
disable Enemy
disable Player PlayerMovement
call Player TakeDamage 10
call Player Rigidbody AddForce 0,500,0
send Door Open
send * OnLevelReset
get Player PlayerHealth
set Player PlayerHealth hp 100
set "Main Camera" Camera fieldOfView 40
set Cube Transform position 0 2 0
timescale 0.2 ; disable HUD
```

### Come vengono letti gli argomenti

| Tipo | Si scrive |
|---|---|
| `int`, `float` | `10`, `0.5`; anche `0,5` |
| `bool` | `true`/`false`, `1`/`0`, `on`/`off`, `si`/`no` |
| enum | il nome del valore, maiuscole indifferenti (Tab li completa) |
| `Vector2/3/4` | `1,2,3` oppure `"1 2 3"`; un numero solo = tutti uguali |
| `Quaternion` | 3 numeri = angoli di Eulero in gradi |
| `Color` | `#ff8800`, `red`, oppure `1,0.5,0` / `255,128,0` |
| `GameObject`, `Transform`, componenti | il nome del GameObject |
| `string` | il testo; se è l'ultimo parametro raccoglie tutto il resto della riga |

Con più overload, `call` usa il primo a cui gli argomenti si convertono; se
nessuno va bene stampa tutte le firme disponibili.

> **`send` e `SendMessage`.** `send` segue le regole di `SendMessage` (nome
> esatto, metodi anche privati, script anche disabilitati) ma converte gli
> argomenti nei tipi dei parametri e ne accetta più di uno. Con il vero
> `SendMessage`, `send Door Open 5` fallirebbe su un `Open(float)` perché `5`
> sarebbe un `int`.

---

## Aggiungere comandi propri

### Con un attributo (consigliato)

```csharp
using DevTools;

public static class CheatCommands
{
    [ConsoleCommand("god", "invulnerabilità on/off")]
    private static void God(bool on) { ... }

    // parametri con default = opzionali; il valore restituito viene stampato
    [ConsoleCommand("spawn", "crea N nemici")]
    private static int Spawn(string prefab, int count = 1) { ...; return spawned; }
}

public class Door : MonoBehaviour
{
    // metodo d'istanza: chiamato su TUTTE le porte attive in scena
    [ConsoleCommand("doors.open", "apre tutte le porte")]
    private void OpenFromConsole() => Open();
}
```

Non si registra niente a mano: all'avvio la console trova da sola i metodi con
`[ConsoleCommand]`. Il comando si completa con Tab, compare in `help` con la
riga d'uso generata dai parametri, e un parametro enum propone i suoi valori.

Per un errore pulito, senza stack trace: `throw new ConsoleException("...")`.

### A runtime

```csharp
DevConsole.Register("fps", "mostra gli fps", args => DevConsole.Log($"{1f / Time.smoothDeltaTime:F0}"));
```

### Da codice, e all'avvio

- `DevConsole.Execute("timescale 0.5; disable HUD")` esegue una riga come se
  l'avessi scritta.
- Il campo **Startup Commands** del componente esegue una lista di comandi
  all'avvio, uno per riga.
- `DevConsole.Log/LogWarning/LogError` scrivono solo in console, non nella
  Console di Unity.

Gli esempi pronti dello spike sono in
[`SpikeConsoleCommands.cs`](../unity/GpuShareSpike/Assets/Scripts/SpikeConsoleCommands.cs):
`gs.hud`, `gs.view 0|1|2`, `gs.cam Manual|DeterministicSweep|FollowTransform`.

---

## Cose da sapere

- **Il vecchio Input Manager vede i tasti anche mentre scrivi.** Gli script che
  leggono `Input.GetKey` devono ignorarli quando `DevConsole.IsOpen` è vero.
  `VirtualCameraDriver` lo fa già: in modalità `Manual` WASD e mouse non
  muovono la camera mentre la console è aperta. La console invece funziona sia
  con il vecchio Input Manager sia con il nuovo Input System: legge la
  tastiera dagli eventi di `OnGUI`.
- **Il cursore** viene sbloccato all'apertura e rimesso com'era alla chiusura
  (`Release Cursor`).
- **Build IL2CPP**: con il *Managed Stripping Level* alto il linker può
  eliminare metodi e campi usati solo via reflection. Nell'editor e con Mono non
  succede; in IL2CPP marca con `[UnityEngine.Scripting.Preserve]` quello che
  vuoi raggiungere con `call`/`get`/`set`/`[ConsoleCommand]`.
- `get` senza campo legge le proprietà pubbliche del componente, salta quelle
  che creerebbero copie (`material`, `mesh`...), ma un getter scritto con
  effetti collaterali verrebbe eseguito.
- È **IMGUI**, come `HudOverlay`: niente Canvas né prefab, e disegna sopra
  all'HUD.
