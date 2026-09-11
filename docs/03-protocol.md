# Protocollo binario

Sorgente unica: [`shared/ShareProtocol.h`](../shared/ShareProtocol.h).
Quel file è incluso **tale e quale** dal modulo Unreal e dal plugin nativo
Unity; il C# lo rispecchia in `ShareProtocol.cs` con verifica di `sizeof`
all'avvio.

Se cambi il protocollo, cambialo **lì**. Gli `static_assert` su ogni struct
fanno fallire la compilazione di entrambi i lati nativi se le dimensioni non
tornano; `Protocol.SelfTest()` fa fallire l'avvio lato C#.

- **Little endian** ovunque (x86/x64 nativo; il C# lo scrive esplicito con
  `BinaryPrimitives`, non si affida all'endianness della macchina).
- `#pragma pack(1)`, con i campi già allineati naturalmente: il pack non costa
  niente e rende il layout indipendente dal compilatore.
- Trasporto **UDP su localhost**. Unreal ascolta su `45001` e risponde
  all'indirizzo da cui è arrivato l'`HELLO`, quindi Unity non ha bisogno di una
  porta fissa.

---

## Header comune — 8 byte

| Offset | Tipo | Campo |
|---|---|---|
| 0 | `uint32` | `magic` = `0x47535031` (`'GSP1'`) |
| 4 | `uint16` | `version` = 1 |
| 6 | `uint16` | `type` |

`type`: 1 `HELLO`, 2 `HANDSHAKE`, 3 `POSE`, 4 `STATUS`, 5 `BYE`.

---

## HELLO — Unity → Unreal — 24 byte

| Offset | Tipo | Campo |
|---|---|---|
| 0 | | header |
| 8 | `uint32` | `unity_pid` — serve a Unreal per `DuplicateHandle` |
| 12 | `uint32` | `adapter_luid_low` |
| 16 | `int32` | `adapter_luid_high` |
| 20 | `uint32` | `flags` — bit0 `WANT_DEPTH`, bit1 `WANT_CUBE` |

Ripetuto ogni 0.5 s finché non arriva l'`HANDSHAKE`. Questo rende l'ordine di
avvio dei due processi irrilevante e permette di riavviare Unreal senza
riavviare Unity.

---

## HANDSHAKE — Unreal → Unity — 256 byte

| Offset | Tipo | Campo |
|---|---|---|
| 0 | | header |
| 8 | `uint32` | `ue_pid` |
| 12 | `uint32` | `adapter_luid_low` |
| 16 | `int32` | `adapter_luid_high` |
| 20 | `uint32` | `channel_count` |
| 24 | `uint32` | `handle_mode` — 0 duplicato, 1 nominato |
| 28 | `uint32` | riservato |
| 32 | `char[64]` | `name_prefix` (solo se `handle_mode == 1`) |
| 96 | `ChannelDesc[4]` | 40 byte ciascuno |

### ChannelDesc — 40 byte

| Offset | Tipo | Campo |
|---|---|---|
| +0 | `uint32` | `channel_id` — 0 COLOR, 1 DEPTH, 2 CUBE |
| +4 | `uint32` | `group_id` — 0 MAIN, 1 CUBE |
| +8 | `uint32` | `width` |
| +12 | `uint32` | `height` |
| +16 | `uint32` | `dxgi_format` — valore numerico di `DXGI_FORMAT` |
| +20 | `uint32` | `flags` — bit0 `HAS_MARKER` |
| +24 | `uint64` | `shared_handles[0]` |
| +32 | `uint64` | `shared_handles[1]` |

Gli handle sono **valori validi nello spazio del processo Unity**: Unreal li ha
già duplicati con `DuplicateHandle`. In modalità nominata valgono 0 e Unity
ricostruisce i nomi come `<name_prefix>_ch<channel_id>_b<index>`.

I due LUID sono la prima cosa che Unity confronta. Se divergono, la condivisione
non può funzionare e il sintomo — senza questo controllo — sarebbe solo un
`HRESULT` di errore da `OpenSharedResource1`.

---

## POSE — Unity → Unreal — 68 byte

| Offset | Tipo | Campo |
|---|---|---|
| 0 | | header |
| 8 | `uint64` | `frame_id` — **è `Time.frameCount` di Unity** |
| 16 | `int64` | `qpc` — `QueryPerformanceCounter` all'invio |
| 24 | `float[3]` | posizione |
| 36 | `float[4]` | quaternione `(x, y, z, w)` |
| 52 | `float` | `fov_y_deg` — **verticale** |
| 56 | `float` | `aspect` |
| 60 | `float` | `near_m` |
| 64 | `float` | `far_m` — 0 = infinito |

### Convenzione di coordinate

I valori sono in **spazio Unity**: metri, Y-up, Z-forward, left-handed.
La conversione avviene in **un unico punto**, `FGpuSharePoseState::ToUnrealTransform()`.

```
Unity : X destra,  Y alto,   Z avanti,  metri
Unreal: X avanti,  Y destra, Z alto,    centimetri

posizione:    UE = (U.z, U.x, U.y) * 100
quaternione:  UE = (U.q.z, U.q.x, U.q.y, U.q.w)
```

La regola del quaternione vale perché la mappa tra le due basi è una
permutazione **ciclica** degli assi, cioè una rotazione propria (determinante
+1), ed entrambi i sistemi sono left-handed: l'asse si permuta come un vettore
qualsiasi e l'angolo — quindi `w` — non cambia.

Attenzione anche al **FOV**: Unity usa il verticale (`Camera.fieldOfView`),
`USceneCaptureComponent2D::FOVAngle` usa l'orizzontale. La conversione
(`tan(h/2) = tan(v/2) · aspect`) è in `AGpuShareCaptureActor::ApplyPose`.

### Latest-wins

Unreal **non accoda** le pose: un unico slot che il thread di rete sovrascrive.
Se tra due tick di Unreal ne arrivano cinque, ne viene usata una e le altre
quattro vengono buttate.

Accodarle farebbe l'opposto di quello che serve: se Unity manda più in fretta di
quanto Unreal renderizza, la coda cresce e la latenza aumenta senza limite.
Sovrascrivendo, la latenza resta limitata dal tempo di un frame.

---

## STATUS — Unreal → Unity — 256 byte

| Offset | Tipo | Campo |
|---|---|---|
| 0 | | header |
| 8 | `uint64` | `ue_frame_counter` — per calcolare gli FPS di Unreal |
| 16 | `int64` | `qpc` all'invio |
| 24 | `uint32` | `group_count` |
| 28 | `uint32` | riservato |
| 32 | `GroupStatus[2]` | 40 byte ciascuno |
| 112 | `float[16]` | `ue_view_matrix` — **diagnostica** |
| 176 | `float[16]` | `ue_proj_matrix` — **diagnostica** |
| 240 | `float` | `applied_fov_y_deg` |
| 244 | `float` | `applied_near_cm` |
| 248 | `float` | `render_fov_y_deg` |
| 252 | `float` | `depth_scale_to_meters` = 0.01 |

### GroupStatus — 40 byte

| Offset | Tipo | Campo |
|---|---|---|
| +0 | `uint32` | `group_id` |
| +4 | `uint32` | `ready_index` — quale dei due buffer contiene il frame pronto |
| +8 | `uint64` | `frame_id` della pose applicata |
| +16 | `int64` | `qpc_pose_send` (eco) |
| +24 | `int64` | `qpc_render_end` |
| +32 | `uint32` | `sequence` |
| +36 | `uint32` | `valid` |

### Le matrici sono diagnostiche, non da usare

Sono in **convenzione Unreal**: left-handed Z-up, centimetri, reversed-Z con far
infinito. Darle in pasto a Unity così com'è non funziona.

Il percorso supportato per ricostruire la camera lato Unity è: **eco della pose**
(che è già in spazio Unity, l'ha mandata Unity stessa) + `fov` + `near` + **depth
lineare**. Le matrici servono a confrontare quando qualcosa non torna, e sono già
lì per quando servirà davvero (reproiezione).

---

## MARKER — dentro i pixel — 32 byte = 8 pixel BGRA8

Scritto da Unreal nei primi 8 pixel in alto a sinistra del canale COLOR.

| Offset | Tipo | Campo |
|---|---|---|
| 0 | `uint32` | `magic` = `0x4D524B31` (`'MRK1'`) |
| 4 | `uint64` | `frame_id` |
| 12 | `int64` | `qpc_pose_send` — **eco del QPC che Unity ha messo nella POSE** |
| 20 | `int64` | `qpc_render_end` |
| 28 | `uint32` | `checksum` — FNV-1a 32 sui byte 0..27 |

### Perché nei pixel e non solo nel pacchetto UDP

Il pacchetto `STATUS` dice *"ho pubblicato il frame N"*. Non dice che i pixel
che Unity sta **mostrando** siano quelli del frame N. Il marker viaggia dentro
l'immagine, quindi misura l'anello vero e non una sua approssimazione.

### Come vengono protetti i bit

Trentadue byte codificati in pixel devono arrivare **bit-esatti**. Qualunque
conversione sRGB, filtraggio bilineare o tonemapping li distrugge. Quindi:

1. **Scritti dopo la `CopyResource`**, direttamente nella shared texture: non
   passano dal post-processing di Unreal.
2. **Letti come byte grezzi** da una staging texture mappata in CPU dentro il
   plugin nativo. Non toccano mai un sampler, quindi nessuna conversione di
   colore può avvenire. Questo è anche il motivo per cui la lettura sta nel
   nativo e non in C#.
3. `magic` + `checksum` distinguono un marker valido da memoria sporca o da bit
   alterati lungo la catena. Se l'HUD dice *"marker non valido"*, qualcosa nel
   percorso sta modificando i pixel.

### La misura

```
latenza pose → pixel = qpc_consume − marker.qpc_pose_send
```

- `marker.qpc_pose_send` l'ha scritto **Unity** nel pacchetto POSE ed è tornato
  indietro dentro l'immagine;
- `qpc_consume` lo timbra **Unity** nel momento in cui l'`AcquireSync` ritorna,
  cioè quando quei pixel sono davvero disponibili.

Entrambi vengono dallo **stesso time base**: QPC su Windows è un contatore di
sistema, coerente tra processi sulla stessa macchina. Nessuna sincronizzazione
di clock, nessuna stima.

La lettura del marker avviene con qualche evento di ritardo (staging non
bloccante), ma **quel ritardo non entra nel valore**: sposta solo il momento in
cui lo veniamo a sapere.

`qpc_render_end` è invece il timestamp della **submit CPU** lato Unreal, non del
completamento GPU. Serve a spezzare l'intervallo, non è un dato di latenza: nel
codice e nell'HUD è etichettato così.
