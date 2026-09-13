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

## Le due "camere", che non c'entrano niente l'una con l'altra

È il punto in cui ci si confonde di più.

| | Cosa fa | Cosa NON fa |
|---|---|---|
| **Camera virtuale** (`VirtualCameraDriver`) | È solo una posizione + rotazione, spedita a Unreal via UDP. È **il punto di vista nel mondo 3D**: Unreal ci mette lì la sua camera e renderizza da lì. | Non renderizza niente. Non è un `Camera` di Unity. |
| **Present camera** (`ShareClient.PresentCamera`) | È ortografica e disegna il quad con la texture che arriva da Unreal. | Non ha alcun rapporto col punto di vista 3D. Muoverla non cambia l'inquadratura. |

Quello che vedi a schermo **è** la vista della camera di Unreal. Si muove da
sola perché il driver è in `DeterministicSweep`.

## Far seguire a Unreal un oggetto della tua scena Unity

Su `VirtualCameraDriver`:

1. **Mode** → `FollowTransform`
2. **Source Transform** → il GameObject che rappresenta il punto di vista
3. **Source Camera** *(opzionale)* → una `Camera` da cui prendere FOV verticale,
   near e far, così Unreal renderizza con gli stessi parametri che la logica di
   Unity crede di avere

> **Non usare la Present Camera come Source Transform.** Il quad le è figlio,
> quindi la seguirebbe e a schermo non cambierebbe nulla. `ShareClient` se ne
> accorge e logga un errore, ma tanto vale saperlo prima.
>
> Usa un GameObject separato: un player controller, un rig, più avanti l'XR rig.
> Può anche avere una `Camera` disattivata sopra, serve solo per i parametri.

## Tasti e parametri utili

| | |
|---|---|
| **F1** | mostra/nasconde l'HUD |
| `ShareClient → Debug Mode` | `0` colore, `1` depth in scala di grigi, `2` zoom sugli 8 pixel del marker |
| `ShareClient → Srgb Decode` | inverti se i colori sembrano sbagliati |
| `VirtualCameraDriver → Mode` | `DeterministicSweep` (default, per misurare), `Manual` (WASD + mouse) o `FollowTransform` (segue un oggetto della scena) |
| `VirtualCameraDriver → Sweep Hz` | più è alto, più la latenza è visibile a occhio |
