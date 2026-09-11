// ============================================================================
//  Calcolo delle metriche. E' il motivo per cui esiste tutto il resto.
//
//  COSA MISURA E COME, senza scorciatoie:
//
//  1) LATENZA POSE->PIXEL (ms)
//         qpc_consume - marker.qpc_pose_send
//     Il secondo valore e' stato scritto da Unity nel pacchetto POSE, e' tornato
//     indietro DENTRO I PIXEL, e viene confrontato con un timestamp preso nel
//     momento in cui l'AcquireSync di Unity e' ritornato, cioe' quando quei
//     pixel sono diventati disponibili a Unity.
//     Entrambi i valori vengono dallo STESSO time base (QPC e' di sistema, non
//     di processo), quindi non c'e' nessuna sincronizzazione di clock da fare e
//     nessuna stima. La lettura arriva con qualche frame di ritardo, ma il
//     ritardo di lettura non entra nel valore: sposta solo QUANDO lo sappiamo.
//
//  2) ETA' IN FRAME
//         unity_frame_index_al_consume - marker.frame_id
//     Funziona perche' il frame_id della pose E' Time.frameCount di Unity.
//
//  3) FPS DEI DUE PROCESSI
//     Unity dai propri delta; Unreal dal contatore di frame nei pacchetti
//     STATUS, diviso per il delta di QPC tra due pacchetti.
//
//  4) RIPRESENTAZIONI DELLO STESSO FRAME
//     Dalle statistiche native: consume_attempts - consume_success. Ogni
//     tentativo di consume che va in timeout e' un frame di Unity in cui non
//     c'era nulla di nuovo, quindi l'immagine precedente e' stata ripresentata.
//     Contarlo dal marker sarebbe SBAGLIATO: il marker si aggiorna solo quando
//     la lettura asincrona riesce, quindi conterebbe ripetizioni inesistenti.
// ============================================================================

using System.Diagnostics;
using UnityEngine;

namespace GpuShareSpike
{
    public sealed class LatencyTracker
    {
        // --- Latenza -----------------------------------------------------------
        public bool   HasSample { get; private set; }
        public double LatencyMs { get; private set; }
        public double LatencyMsSmoothed { get; private set; }
        public double LatencyMsMin { get; private set; } = double.MaxValue;
        public double LatencyMsMax { get; private set; }
        public long   AgeFrames { get; private set; }
        public ulong  LastFrameId { get; private set; }
        public uint   ReadbackLagEvents { get; private set; }
        public bool   MarkerValid { get; private set; }

        /// <summary>Scomposizione: quanto di quella latenza e' stato speso da Unreal fino alla submit.</summary>
        public double UnrealSubmitMs { get; private set; }

        // --- Frequenze ---------------------------------------------------------
        public double UnityFps { get; private set; }
        public double UnrealFps { get; private set; }

        // --- Ripetizioni -------------------------------------------------------
        public ulong ConsumeAttempts { get; private set; }
        public ulong ConsumeSuccess { get; private set; }
        public ulong RepeatedFrames { get; private set; }
        public double RepeatRatio => ConsumeAttempts > 0 ? (double)RepeatedFrames / ConsumeAttempts : 0.0;

        public ulong MarkerReads { get; private set; }
        public ulong MarkerInvalid { get; private set; }

        private double _unityFpsAccumulator;
        private int _unityFpsFrames;
        private double _unityFpsTimer;

        private ulong _prevUeFrameCounter;
        private long _prevUeQpc;
        private bool _hasPrevUeSample;
        private double _ueFpsTimer;

        private const double SmoothingAlpha = 0.1;

        public void Reset()
        {
            HasSample = false;
            LatencyMsMin = double.MaxValue;
            LatencyMsMax = 0.0;
            LatencyMsSmoothed = 0.0;
            _hasPrevUeSample = false;
        }

        public void TickUnityFps(float unscaledDeltaTime)
        {
            _unityFpsAccumulator += unscaledDeltaTime;
            ++_unityFpsFrames;
            _unityFpsTimer += unscaledDeltaTime;

            if (_unityFpsTimer >= 0.25 && _unityFpsAccumulator > 0.0)
            {
                UnityFps = _unityFpsFrames / _unityFpsAccumulator;
                _unityFpsAccumulator = 0.0;
                _unityFpsFrames = 0;
                _unityFpsTimer = 0.0;
            }
        }

        public void TickUnrealFps(ulong ueFrameCounter, long ueQpc, float unscaledDeltaTime)
        {
            _ueFpsTimer += unscaledDeltaTime;

            if (!_hasPrevUeSample)
            {
                _prevUeFrameCounter = ueFrameCounter;
                _prevUeQpc = ueQpc;
                _hasPrevUeSample = true;
                return;
            }

            if (_ueFpsTimer < 0.25) return;

            long deltaTicks = ueQpc - _prevUeQpc;
            ulong deltaFrames = ueFrameCounter >= _prevUeFrameCounter
                ? ueFrameCounter - _prevUeFrameCounter
                : 0;

            if (deltaTicks > 0)
            {
                double seconds = (double)deltaTicks / Stopwatch.Frequency;
                if (seconds > 0.0)
                {
                    UnrealFps = deltaFrames / seconds;
                }
            }

            _prevUeFrameCounter = ueFrameCounter;
            _prevUeQpc = ueQpc;
            _ueFpsTimer = 0.0;
        }

        public void TickFrameInfo(in NativeFrameInfo info)
        {
            MarkerValid = info.MarkerValid != 0;
            ReadbackLagEvents = info.ReadbackLagEvents;

            if (!MarkerValid) return;

            // Nuovo campione solo quando il frame_id cambia: altrimenti
            // ricalcoleremmo min/max sullo stesso dato.
            if (HasSample && info.Marker.FrameId == LastFrameId) return;

            LastFrameId = info.Marker.FrameId;

            long latencyTicks = info.QpcConsume - info.Marker.QpcPoseSend;
            LatencyMs = ControlChannelClient.QpcToMilliseconds(latencyTicks);

            long submitTicks = info.Marker.QpcRenderEnd - info.Marker.QpcPoseSend;
            UnrealSubmitMs = ControlChannelClient.QpcToMilliseconds(submitTicks);

            // unity_frame_index e' il frame in cui il consume e' avvenuto;
            // marker.frame_id E' Time.frameCount di quando la pose e' partita.
            AgeFrames = (long)info.UnityFrameIndex - (long)info.Marker.FrameId;

            if (!HasSample)
            {
                LatencyMsSmoothed = LatencyMs;
                HasSample = true;
            }
            else
            {
                LatencyMsSmoothed += (LatencyMs - LatencyMsSmoothed) * SmoothingAlpha;
            }

            if (LatencyMs < LatencyMsMin) LatencyMsMin = LatencyMs;
            if (LatencyMs > LatencyMsMax) LatencyMsMax = LatencyMs;
        }

        public void TickNativeStats(in NativeStats stats)
        {
            ConsumeAttempts = stats.ConsumeAttempts;
            ConsumeSuccess  = stats.ConsumeSuccess;
            RepeatedFrames  = stats.AcquireTimeouts;
            MarkerReads     = stats.MarkerReads;
            MarkerInvalid   = stats.MarkerInvalid;
        }
    }
}
