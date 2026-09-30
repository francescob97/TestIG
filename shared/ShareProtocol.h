// ============================================================================
//  ShareProtocol.h  --  SORGENTE UNICA DI VERITA' del protocollo binario.
//
//  Questo file e' incluso TALE E QUALE da:
//    - il modulo Unreal   (unreal/GpuShareSpike/Source/GpuShareSpike/...)
//    - il plugin nativo   (unity-native/src/...)
//  e replicato a mano in C# in unity/GpuShareSpike/Assets/Scripts/ShareProtocol.cs
//  (con verifica di sizeof a runtime, cosi' una divergenza esplode subito).
//
//  REGOLE:
//   - little endian (x86/x64 nativo: nessuno swap, ma lo verifichiamo)
//   - #pragma pack(1) + campi gia' allineati naturalmente: il pack non costa
//     nulla ma rende il layout indipendente dal compilatore
//   - ogni struct ha uno static_assert sulla sua dimensione. Se aggiungi un
//     campo e non aggiorni l'assert, non compila. E' voluto.
// ============================================================================

#pragma once

#include <stdint.h>

// ---------------------------------------------------------------------------
//  Costanti di protocollo
// ---------------------------------------------------------------------------

#define GPUSHARE_PROTOCOL_MAGIC        0x47535031u   // 'GSP1' little endian
// v2: viste multiple (VR stereo), HELLO/POSE/HANDSHAKE estesi, config_id e
//     request_id per le riconfigurazioni. Incompatibile con v1: i due lati
//     rifiutano i pacchetti dell'altra versione e lo dicono nel log/HUD.
#define GPUSHARE_PROTOCOL_VERSION      2

// Il marker di strumentazione occupa i primi 8 pixel in alto a sinistra del
// canale COLOR. 8 pixel BGRA8 = 32 byte esatti.
#define GPUSHARE_MARKER_MAGIC          0x4D524B31u   // 'MRK1'
#define GPUSHARE_MARKER_PIXELS         8
#define GPUSHARE_MARKER_BYTES          32

#define GPUSHARE_MAX_CHANNELS          6             // 2 viste x (colore+depth) + cube + riserva
#define GPUSHARE_MAX_VIEWS             2             // 1 = mono (desktop o VR mono), 2 = VR stereo
#define GPUSHARE_BUFFERS_PER_CHANNEL   2             // doppio buffer
#define GPUSHARE_GROUP_COUNT           2             // MAIN + CUBE

// Chiavi del keyed mutex. Convenzione condivisa dai due processi:
//   KEY_PRODUCER = il buffer e' libero, Unreal puo' scriverci
//   KEY_CONSUMER = il buffer contiene un frame pronto, Unity puo' leggerlo
// Un IDXGIKeyedMutex appena creato e' nello stato "rilasciato con chiave 0",
// quindi la primissima AcquireSync(KEY_PRODUCER) del produttore riesce.
#define GPUSHARE_KEY_PRODUCER          0ull
#define GPUSHARE_KEY_CONSUMER          1ull

// Nome del canale di controllo. Unreal ascolta su questa porta; risponde
// all'indirizzo/porta da cui e' arrivato il pacchetto (quindi Unity non deve
// avere una porta fissa).
#define GPUSHARE_DEFAULT_UE_PORT       45001

#pragma pack(push, 1)

// ---------------------------------------------------------------------------
//  Enum
// ---------------------------------------------------------------------------

enum GpuSharePacketType : uint16_t
{
    GS_PKT_HELLO     = 1,   // Unity -> Unreal : mi presento, ecco il mio PID
    GS_PKT_HANDSHAKE = 2,   // Unreal -> Unity : ecco la tabella dei canali
    GS_PKT_POSE      = 3,   // Unity -> Unreal : pose della camera
    GS_PKT_STATUS    = 4,   // Unreal -> Unity : ho pubblicato questo frame
    GS_PKT_BYE       = 5,   // Unity -> Unreal : mi stacco
};

enum GpuShareChannelId : uint32_t
{
    // Numerazione di v1 invariata per i primi tre, cosi' il caso mono resta
    // identico a prima. Gli ID della vista 1 esistono solo in stereo.
    GS_CH_COLOR   = 0,      // vista 0: B8G8R8A8_UNORM, porta il marker
    GS_CH_DEPTH   = 1,      // vista 0: R32_FLOAT, depth lineare in CENTIMETRI
    GS_CH_CUBE    = 2,      // B8G8R8A8_UNORM, atlas 3x2 delle facce cubemap
    GS_CH_COLOR_1 = 3,      // vista 1 (occhio destro): B8G8R8A8_UNORM
    GS_CH_DEPTH_1 = 4,      // vista 1 (occhio destro): R32_FLOAT
};

// Vista 0 = mono, oppure occhio SINISTRO in stereo.
// Vista 1 = occhio DESTRO. Stessa convenzione di unity_StereoEyeIndex.
static inline uint32_t GpuShareColorChannelForView(uint32_t View) { return View == 0 ? GS_CH_COLOR : GS_CH_COLOR_1; }
static inline uint32_t GpuShareDepthChannelForView(uint32_t View) { return View == 0 ? GS_CH_DEPTH : GS_CH_DEPTH_1; }

enum GpuShareGroupId : uint32_t
{
    // Un GRUPPO e' l'unita' di pubblicazione ATOMICA: tutti i canali di un
    // gruppo provengono dallo stesso frame e vengono resi visibili insieme.
    // COLOR e DEPTH DEVONO stare nello stesso gruppo, altrimenti il
    // compositing lato Unity si rompe in modo sottile e difficile da trovare.
    GS_GROUP_MAIN = 0,      // COLOR + DEPTH, cadenza alta
    GS_GROUP_CUBE = 1,      // CUBE, cadenza bassa e indipendente
};

enum GpuShareChannelFlags : uint32_t
{
    GS_CHFLAG_NONE        = 0,
    GS_CHFLAG_HAS_MARKER  = 1u << 0,   // in questo canale sono scritti gli 8 pixel
};

enum GpuShareHelloFlags : uint32_t
{
    GS_HELLOFLAG_NONE        = 0,
    GS_HELLOFLAG_WANT_DEPTH  = 1u << 0,
    GS_HELLOFLAG_WANT_CUBE   = 1u << 1,
};

enum GpuShareHandleMode : uint32_t
{
    // Percorso primario: Unreal duplica l'NT handle nel processo Unity con
    // DuplicateHandle() e ne manda il valore numerico.
    GS_HANDLEMODE_DUPLICATED = 0,
    // Fallback: l'handle e' creato con un NOME e Unity lo apre con
    // ID3D11Device1::OpenSharedResourceByName. Non serve il PID ne' il
    // privilegio PROCESS_DUP_HANDLE. Vedi docs/05-troubleshooting.md.
    GS_HANDLEMODE_NAMED      = 1,
};

// ---------------------------------------------------------------------------
//  Header comune a tutti i pacchetti
// ---------------------------------------------------------------------------

struct GpuShareHeader
{
    uint32_t magic;     // GPUSHARE_PROTOCOL_MAGIC
    uint16_t version;   // GPUSHARE_PROTOCOL_VERSION
    uint16_t type;      // GpuSharePacketType
};
static_assert(sizeof(GpuShareHeader) == 8, "GpuShareHeader deve essere 8 byte");

// ---------------------------------------------------------------------------
//  HELLO : Unity -> Unreal
// ---------------------------------------------------------------------------

struct GpuShareHello
{
    GpuShareHeader header;

    uint32_t unity_pid;             // serve a Unreal per DuplicateHandle
    uint32_t adapter_luid_low;      // LUID dell'adapter D3D11 di Unity
    int32_t  adapter_luid_high;     // (i due processi DEVONO stare sulla stessa GPU)
    uint32_t flags;                 // GpuShareHelloFlags

    // --- v2: e' Unity a decidere la forma dello stream ---------------------
    uint32_t view_count;            // 1 = mono, 2 = stereo
    uint32_t view_width;            // risoluzione PER VISTA richiesta; 0 = default di Unreal
    uint32_t view_height;           //   (Unreal la limita a MaxViewDimension)
    uint32_t request_id;            // cambia a ogni (ri)connessione di Unity.
                                    // Unreal lo rimanda nell'HANDSHAKE: Unity accetta
                                    // solo l'handshake che risponde alla SUA richiesta
                                    // corrente, e ignora quelli di richieste vecchie
                                    // ancora in volo dopo un cambio di modalita'.
};
static_assert(sizeof(GpuShareHello) == 40, "GpuShareHello deve essere 40 byte");

// ---------------------------------------------------------------------------
//  Descrittore di un canale (dentro l'handshake)
// ---------------------------------------------------------------------------

struct GpuShareChannelDesc
{
    uint32_t channel_id;            // GpuShareChannelId
    uint32_t group_id;              // GpuShareGroupId
    uint32_t width;
    uint32_t height;
    uint32_t dxgi_format;           // valore numerico di DXGI_FORMAT
    uint32_t flags;                 // GpuShareChannelFlags

    // Un handle per buffer del doppio buffer. In GS_HANDLEMODE_DUPLICATED
    // questi sono i valori degli NT handle VALIDI NELLO SPAZIO DEL PROCESSO
    // UNITY (gia' duplicati). In GS_HANDLEMODE_NAMED sono 0 e Unity ricostruisce
    // i nomi come  "<name_prefix>_ch<channel_id>_b<index>".
    uint64_t shared_handles[GPUSHARE_BUFFERS_PER_CHANNEL];
};
static_assert(sizeof(GpuShareChannelDesc) == 40, "GpuShareChannelDesc deve essere 40 byte");

// ---------------------------------------------------------------------------
//  HANDSHAKE : Unreal -> Unity
// ---------------------------------------------------------------------------

struct GpuShareHandshake
{
    GpuShareHeader header;

    uint32_t ue_pid;
    uint32_t adapter_luid_low;      // LUID dell'adapter D3D11 di Unreal.
    int32_t  adapter_luid_high;     // Unity lo confronta col proprio PRIMA di
                                    // provare ad aprire: se diverge, il fallimento
                                    // di OpenSharedResource1 sarebbe muto.
    uint32_t channel_count;
    uint32_t handle_mode;           // GpuShareHandleMode
    uint32_t view_count;            // viste effettivamente create (1 o 2)
    uint32_t config_id;             // cambia ogni volta che Unreal ricrea le superfici.
                                    // Gli STATUS lo riportano: Unity scarta quelli di
                                    // una configurazione diversa da quella aperta.
    uint32_t request_id;            // eco del request_id dell'HELLO a cui risponde

    char     name_prefix[64];       // usato solo in GS_HANDLEMODE_NAMED

    GpuShareChannelDesc channels[GPUSHARE_MAX_CHANNELS];
};
static_assert(sizeof(GpuShareHandshake) == 344, "GpuShareHandshake deve essere 344 byte");

// ---------------------------------------------------------------------------
//  POSE : Unity -> Unreal
//
//  CONVENZIONE DI COORDINATE: posizione e rotazione sono in SPAZIO UNITY,
//  relative all'origine scelta lato Unity (OriginTransform), che corrisponde
//  all'attore ANCORA lato Unreal.
//    Unity : X destra, Y alto, Z avanti, METRI,       left-handed
//    Unreal: X avanti, Y destra, Z alto, CENTIMETRI,  left-handed
//  La conversione avviene in un unico punto lato Unreal
//  (FGpuSharePoseState::ToUnrealTransform in Net/ControlChannel.h).
//
//  PROIEZIONE: quattro TANGENTI invece di fov + aspect.
//  E' l'unica rappresentazione che descrive un frustum ASIMMETRICO, cioe' cio'
//  che ogni occhio di un visore ha davvero; ed e' la stessa forma che usa
//  OpenXR (XrFovf, che pero' usa gli angoli). E' anche indipendente dalle
//  convenzioni di clip space dei due motori, che sono diverse: per questo non
//  si spedisce una matrice di proiezione.
//    tan_left  = tan(angolo del bordo sinistro)  -> NEGATIVO
//    tan_right = tan(angolo del bordo destro)    -> positivo
//    tan_down  = tan(angolo del bordo inferiore) -> NEGATIVO
//    tan_up    = tan(angolo del bordo superiore) -> positivo
//  misurati nello spazio della vista: X destra, Y alto, guardando avanti.
//  Un frustum simmetrico (desktop) ha tan_left = -tan_right, tan_down = -tan_up.
// ---------------------------------------------------------------------------

struct GpuShareView
{
    float position[3];              // metri, spazio Unity, relativo all'origine
    float rotation[4];              // quaternione Unity (x, y, z, w)
    float tan_left;
    float tan_right;
    float tan_down;
    float tan_up;
};
static_assert(sizeof(GpuShareView) == 44, "GpuShareView deve essere 44 byte");

struct GpuSharePose
{
    GpuShareHeader header;

    uint64_t frame_id;              // Time.frameCount di Unity all'invio
    int64_t  qpc;                   // QueryPerformanceCounter all'invio.
                                    // QPC e' coerente tra processi sulla stessa
                                    // macchina: e' il time base condiviso su cui
                                    // si misura tutto l'anello pose->pixel.

    uint32_t view_count;            // quante viste sono valide (1 o 2)
    uint32_t flags;                 // riservato

    GpuShareView views[GPUSHARE_MAX_VIEWS];

    float    near_m;                // metri
    float    far_m;                 // metri (0 = infinito)
};
static_assert(sizeof(GpuSharePose) == 128, "GpuSharePose deve essere 128 byte");

// ---------------------------------------------------------------------------
//  STATUS : Unreal -> Unity
// ---------------------------------------------------------------------------

struct GpuShareGroupStatus
{
    uint32_t group_id;
    uint32_t ready_index;           // quale dei 2 buffer contiene il frame pronto
    uint64_t frame_id;              // frame_id della pose effettivamente applicata
    int64_t  qpc_pose_send;         // eco del qpc del pacchetto POSE usato
    int64_t  qpc_render_end;        // QPC Unreal alla SUBMIT della copia.
                                    // ATTENZIONE: e' la submit CPU, non il
                                    // completamento GPU. Il momento reale
                                    // "pixel pronti" e' quando l'AcquireSync di
                                    // Unity ritorna, ed e' Unity a timbrarlo.
    uint32_t sequence;              // +1 a ogni publish del gruppo
    uint32_t valid;                 // 0 finche' il gruppo non ha mai pubblicato
};
static_assert(sizeof(GpuShareGroupStatus) == 40, "GpuShareGroupStatus deve essere 40 byte");

struct GpuShareStatus
{
    GpuShareHeader header;

    uint64_t ue_frame_counter;      // frame del motore Unreal (per gli FPS di UE)
    int64_t  qpc;                   // QPC all'invio di questo pacchetto
    uint32_t group_count;
    uint32_t config_id;             // configurazione delle superfici a cui si riferisce

    GpuShareGroupStatus groups[GPUSHARE_GROUP_COUNT];

    // Matrici del frame servito, in CONVENZIONE UNREAL (row-major, left-handed,
    // Z-up, centimetri, reversed-Z con far infinito).
    // NON darle in pasto a Unity cosi' come sono: sono DIAGNOSTICHE.
    // Il percorso supportato per ricostruire la camera lato Unity e':
    // pose echo (gia' in spazio Unity) + fov/near + depth lineare.
    float    ue_view_matrix[16];
    float    ue_proj_matrix[16];

    float    applied_fov_y_deg;     // FOV verticale della vista 0 (atan(up) - atan(down))
    float    applied_near_cm;
    float    render_fov_y_deg;      // >= applied se usi il margine FOV per il
                                    // re-crop tardivo (RenderFovMarginDeg)
    float    depth_scale_to_meters; // 0.01 : moltiplica il canale DEPTH per avere metri
};
static_assert(sizeof(GpuShareStatus) == 256, "GpuShareStatus deve essere 256 byte");

// ---------------------------------------------------------------------------
//  BYE : Unity -> Unreal
// ---------------------------------------------------------------------------

struct GpuShareBye
{
    GpuShareHeader header;
    uint32_t unity_pid;
    uint32_t reserved0;
};
static_assert(sizeof(GpuShareBye) == 16, "GpuShareBye deve essere 16 byte");

// ---------------------------------------------------------------------------
//  MARKER : scritto da Unreal nei primi 8 pixel del canale COLOR
//
//  PERCHE' NEI PIXEL E NON SOLO NEL PACCHETTO UDP:
//  il pacchetto STATUS dice "ho pubblicato il frame N", ma non prova che i
//  pixel che Unity sta effettivamente MOSTRANDO siano quelli del frame N.
//  Il marker viaggia DENTRO l'immagine, quindi misura l'anello vero.
//
//  VINCOLO CRITICO: questi 32 byte devono arrivare BIT-ESATTI.
//  Qualunque conversione sRGB, filtraggio bilineare o tonemapping li distrugge.
//  Per questo:
//    - sono scritti DOPO la CopyResource, direttamente nella shared texture,
//      quindi non passano dal post-processing di Unreal;
//    - sono letti lato Unity come BYTE GREZZI da una staging texture mappata
//      in CPU dentro il plugin nativo, MAI campionati da uno shader.
// ---------------------------------------------------------------------------

struct GpuShareMarker
{
    uint32_t magic;                 // GPUSHARE_MARKER_MAGIC. Se non torna, il
                                    // buffer non e' ancora stato scritto o la
                                    // catena colore ha corrotto i bit.
    uint64_t frame_id;              // frame_id della pose applicata
    int64_t  qpc_pose_send;         // ECO del qpc che Unity ha messo nella POSE.
                                    // E' la chiave della misura: la latenza
                                    // pose->pixel e'
                                    //   QPC_unity_al_present - qpc_pose_send
                                    // con entrambi i valori nello STESSO time base.
    int64_t  qpc_render_end;        // submit CPU lato Unreal (vedi sopra)
    uint32_t checksum;              // FNV-1a 32 sui byte 0..27
};
static_assert(sizeof(GpuShareMarker) == GPUSHARE_MARKER_BYTES,
              "GpuShareMarker deve essere esattamente 32 byte = 8 pixel BGRA8");

#pragma pack(pop)

// ---------------------------------------------------------------------------
//  Helper condivisi (header-only, niente .cpp da linkare)
// ---------------------------------------------------------------------------

// FNV-1a 32 bit. Serve solo a distinguere un marker valido da memoria sporca,
// non e' una funzione crittografica.
static inline uint32_t GpuShareFnv1a32(const void* Data, uint32_t Length)
{
    const uint8_t* Bytes = (const uint8_t*)Data;
    uint32_t Hash = 2166136261u;
    for (uint32_t i = 0; i < Length; ++i)
    {
        Hash ^= Bytes[i];
        Hash *= 16777619u;
    }
    return Hash;
}

static inline uint32_t GpuShareMarkerChecksum(const GpuShareMarker* Marker)
{
    // I primi 28 byte: tutto tranne il campo checksum stesso.
    return GpuShareFnv1a32(Marker, 28);
}

static inline void GpuShareMarkerFinalize(GpuShareMarker* Marker)
{
    Marker->magic = GPUSHARE_MARKER_MAGIC;
    Marker->checksum = GpuShareMarkerChecksum(Marker);
}

static inline int GpuShareMarkerIsValid(const GpuShareMarker* Marker)
{
    return Marker->magic == GPUSHARE_MARKER_MAGIC
        && Marker->checksum == GpuShareMarkerChecksum(Marker);
}

static inline void GpuShareInitHeader(GpuShareHeader* Header, uint16_t Type)
{
    Header->magic   = GPUSHARE_PROTOCOL_MAGIC;
    Header->version = GPUSHARE_PROTOCOL_VERSION;
    Header->type    = Type;
}

static inline int GpuShareHeaderIsValid(const GpuShareHeader* Header)
{
    return Header->magic == GPUSHARE_PROTOCOL_MAGIC
        && Header->version == GPUSHARE_PROTOCOL_VERSION;
}
