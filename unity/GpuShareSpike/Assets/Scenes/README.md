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

## Tasti e parametri utili

| | |
|---|---|
| **F1** | mostra/nasconde l'HUD |
| `ShareClient → Debug Mode` | `0` colore, `1` depth in scala di grigi, `2` zoom sugli 8 pixel del marker |
| `ShareClient → Srgb Decode` | inverti se i colori sembrano sbagliati |
| `VirtualCameraDriver → Mode` | `DeterministicSweep` (default, per misurare) oppure `Manual` (WASD + mouse) |
| `VirtualCameraDriver → Sweep Hz` | più è alto, più la latenza è visibile a occhio |
