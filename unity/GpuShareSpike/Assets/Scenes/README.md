# Montaggio della scena (6 passi)

I file `.unity` non sono versionati: sono YAML fragili, e generarne uno a mano
produrrebbe errori opachi al primo caricamento. Il montaggio è di due minuti.

1. `File → New Scene` → template **Basic (URP)**
2. Seleziona la **Main Camera**.
   Non devi cambiare niente: `ShareClient` la mette in ortografica da solo.
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
alla camera: non c'è niente da posizionare.

## Verifica

Con Unreal già avviato in Standalone, premi **Play**. Entro un secondo l'HUD in
alto a sinistra deve passare da *"in attesa dell'handshake di Unreal..."* a
*"in esecuzione"* e mostrare i numeri di latenza.

Se resta in attesa, vedi [`docs/05-troubleshooting.md`](../../../../docs/05-troubleshooting.md).

## Come funziona, in una frase

**La tua camera di Unity è il punto di vista.** Il quad le viene creato figlio:
è uno schermo incollato davanti all'occhio. Muovendo la camera si muovono
entrambi, quindi il quad resta a riempire lo schermo — quello che cambia è il
**contenuto** della texture, perché la pose della camera va a Unreal e Unreal
rirenderizza da lì.

Unity comanda la vista di Unreal, e con quella vista ci vede.

Di default `ShareClient` aggancia il driver alla `PresentCamera` stessa: non
devi fare niente. Assegna un `Source Transform` diverso solo se il punto di
vista è un altro oggetto — un character controller, un rig, più avanti l'XR rig.

## Le due origini che si corrispondono

La pose spedita è **relativa** a `Origin Transform` (vuoto = origine del mondo
Unity). Dall'altra parte Unreal la applica **in relativo all'attore di cattura**,
che fa da **ancora**:

```
origine di Unity   ==   transform dell'attore ancora nel mondo Unreal
```

```
        UNITY                                  UNREAL
   OriginTransform  ─────── stesso punto ────  AGpuShareCaptureActor  (ancora)
   (o l'origine)                                 │   ← muovila con quello che vuoi:
        │                                        │     Blueprint, C++, un componente
        └── Camera (WASD) ── pose relativa ──►   └── CameraRoot
              └── quad                                 └── SceneCapture ×N
```

Unity lavora sempre in uno spazio locale piccolo, in metri, vicino all'origine.
L'ancora lo colloca dove serve nel mondo enorme di Unreal, e **chi muove
l'ancora si porta dietro tutto lo spazio di Unity**. È lo stesso pattern del
georeference di Cesium, ed è il motivo per cui la precisione in singola non
degrada quando ti allontani dall'origine.

L'attore di cattura nasce all'origine del mondo Unreal, quindi di partenza le
due origini coincidono. Da lì in poi la transform dell'attore è **libera**:
questo codice non la tocca mai.

Se ti serve invece che la pose sia una transform di mondo assoluta, spegni
`bPoseRelativeToAnchor` nei Project Settings di Unreal.

## Una camera o due?

`ShareClient.Projection`:

| | Quando usarla |
|---|---|
| **Orthographic** *(default)* | Il quad riempie lo schermo a prescindere dal FOV. Il FOV spedito a Unreal resta un parametro indipendente del driver. Robusto, disaccoppiato. |
| **Perspective** | **Una sola camera vera.** Il suo `fieldOfView` è quello che va a Unreal, e il quad riempie esattamente il frustum a `QuadDistance`. La corrispondenza tra ciò che Unreal renderizza e ciò che vedi è 1:1 — è la modalità giusta verso cui andare per il VR. |

## Tasti e parametri utili

| | |
|---|---|
| **F1** | mostra/nasconde l'HUD |
| `ShareClient → Debug Mode` | `0` colore, `1` depth in scala di grigi, `2` zoom sugli 8 pixel del marker |
| `ShareClient → Srgb Decode` | inverti se i colori sembrano sbagliati |
| `VirtualCameraDriver → Mode` | `Manual` (WASD + mouse, **muove** la camera), `DeterministicSweep` (default, oscillazione ripetibile per misurare, **muove** la camera), `FollowTransform` (**legge** e basta: usala quando a muovere la camera è altro) |
| `VirtualCameraDriver → Sweep Hz` | più è alto, più la latenza è visibile a occhio |
