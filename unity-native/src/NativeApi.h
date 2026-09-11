// ============================================================================
//  Strutture scambiate tra il plugin nativo e il C# di Unity.
//  Replicate in unity/GpuShareSpike/Assets/Scripts/ShareProtocol.cs con
//  verifica di sizeof all'avvio.
// ============================================================================

#pragma once

#include <stdint.h>
#include "ShareProtocol.h"

#pragma pack(push, 1)

/**
 * Cio' che il plugin ha letto da un frame effettivamente consumato.
 *
 * PERCHE' qpc_consume E' IL NUMERO CHE CONTA:
 * la lettura del marker avviene per forza con qualche frame di ritardo (si usa
 * una staging texture mappata senza bloccare, altrimenti si stallerebbe la
 * pipeline). Ma qpc_consume viene timbrato NEL MOMENTO GIUSTO, cioe' quando
 * l'AcquireSync di Unity ritorna: e' li' che i pixel di quel frame sono
 * davvero disponibili a Unity.
 * Quindi la latenza pose->pixel e':
 *       qpc_consume - marker.qpc_pose_send
 * misurata, non stimata, e non inquinata dal ritardo di readback: quel ritardo
 * influenza solo QUANDO lo veniamo a sapere, non il valore.
 */
struct GpuShareFrameInfo
{
    GpuShareMarker marker;         // 32 byte
    int64_t  qpc_consume;          // QPC al ritorno dell'AcquireSync
    uint64_t unity_frame_index;    // frame di Unity al momento del consume
    uint32_t marker_valid;         // magic + checksum tornano?
    uint32_t readback_lag_events;  // quanti eventi di consume fa e' stato letto
};

struct GpuShareNativeStats
{
    uint64_t consume_attempts;
    uint64_t consume_success;
    uint64_t acquire_timeouts;   // il produttore non aveva ancora pubblicato
    uint64_t marker_reads;
    uint64_t marker_invalid;
    uint32_t device_ready;
    uint32_t channels_open;
};

#pragma pack(pop)

static_assert(sizeof(GpuShareFrameInfo) == 56, "GpuShareFrameInfo deve essere 56 byte");
static_assert(sizeof(GpuShareNativeStats) == 48, "GpuShareNativeStats deve essere 48 byte");

/** ID degli eventi passati a GL.IssuePluginEvent dal C#. */
enum GpuShareRenderEvent : int32_t
{
    GS_EVENT_CONFIGURE    = 1,   // apre le shared texture (una volta sola)
    GS_EVENT_CONSUME_MAIN = 2,   // acquisisce e copia COLOR + DEPTH
    GS_EVENT_CONSUME_CUBE = 3,   // acquisisce e copia l'atlas cubemap
    GS_EVENT_SHUTDOWN     = 4,
};
