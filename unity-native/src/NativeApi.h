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
    uint64_t acquire_timeouts;        // consume con status fresco ma buffer non ancora
                                      // acquisibile: dovrebbe restare a zero
    uint64_t marker_reads;
    uint64_t marker_invalid;
    uint64_t stale_drained;           // buffer mai consumati restituiti al produttore
    uint64_t last_consumed_sequence;  // sequenza MAIN dell'ultimo consume riuscito
    uint32_t device_ready;
    uint32_t channels_open;
};

#pragma pack(pop)

static_assert(sizeof(GpuShareFrameInfo) == 56, "GpuShareFrameInfo deve essere 56 byte");
static_assert(sizeof(GpuShareNativeStats) == 64, "GpuShareNativeStats deve essere 64 byte");

/**
 * Eventi passati a GL.IssuePluginEvent dal C#.
 *
 * L'ID DELL'EVENTO TRASPORTA I DATI DEL CONSUME, non solo il tipo:
 *
 *     bit  0..3   tipo (GpuShareRenderEvent)
 *     bit  4..5   indice del buffer da consumare (0 o 1)
 *     bit  6      drena l'altro buffer (vedi sotto)
 *     bit  8..30  sequenza del frame (23 bit bastano: sono ore a 1 kHz)
 *
 * PERCHE' NON UNA VARIABILE CONDIVISA COME IN v1:
 * il render thread di Unity esegue il frame N mentre il main thread sta gia'
 * preparando il frame N+1. Con una variabile globale "indice pronto", il main
 * thread del frame N+1 la sovrascriveva prima che il render thread avesse
 * consumato il frame N: il frame N veniva consumato con l'indice sbagliato. In
 * VR, dove l'immagine si riproietta con la pose del frame che si sta mostrando,
 * quell'errore diventa un sobbalzo visibile. Codificando i dati nell'evento,
 * ogni consume porta con se' esattamente l'indice deciso per il suo frame.
 *
 * IL DRENAGGIO:
 * se Unity e' piu' lento di Unreal, capita di ricevere due STATUS nello stesso
 * frame (sequenze n su buffer A, n+1 su buffer B). Si consuma solo B, il piu'
 * recente. Ma A resta "pronto per il consumatore" per sempre: Unreal non puo'
 * piu' riscriverlo e, a ogni frame, perde il timeout dell'acquire (2 ms) prima
 * di ripiegare su B. Il bit di drenaggio dice al plugin di prendere A e
 * restituirlo subito al produttore senza copiarlo. E' sicuro: finche' A e'
 * nello stato "consumatore", Unreal non puo' averci scritto sopra nulla di
 * nuovo, quindi contiene per certo il frame vecchio.
 */
enum GpuShareRenderEvent : int32_t
{
    GS_EVENT_CONFIGURE    = 1,   // apre le shared texture
    GS_EVENT_CONSUME_MAIN = 2,   // acquisisce e copia il gruppo MAIN (colore + depth, 1 o 2 viste)
    GS_EVENT_CONSUME_CUBE = 3,   // acquisisce e copia l'atlas cubemap
    GS_EVENT_SHUTDOWN     = 4,
};

static inline int32_t GpuShareEventType(int32_t EventId)        { return EventId & 0xF; }
static inline uint32_t GpuShareEventReadyIndex(int32_t EventId) { return ((uint32_t)EventId >> 4) & 0x3u; }
static inline int GpuShareEventDrainOther(int32_t EventId)      { return (((uint32_t)EventId >> 6) & 0x1u) != 0; }
static inline uint32_t GpuShareEventSequence(int32_t EventId)   { return ((uint32_t)EventId >> 8) & 0x7FFFFFu; }
