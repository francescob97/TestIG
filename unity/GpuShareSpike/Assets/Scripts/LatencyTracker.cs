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
//         Time.frameCount al consume - frame_id della pose di quel frame
//     Calcolata nel C# al momento in cui si decide il consume: lo STATUS dice
//     quale pose (frame_id) contiene il buffer, e il frame_id E' il
//     Time.frameCount di quando la pose e' partita.
//     (In v1 veniva timbrata dal render thread con un indice di frame scritto
//     dal main thread, che nel frattempo era gia' avanti di uno: l'eta' era
//     sistematicamente gonfiata. Ora non dipende piu' da quella corsa.)
//
//  3) FPS DEI DUE PROCESSI
//     Unity dai propri delta; Unreal dal contatore di frame nei pacchetti
//     STATUS, diviso per il delta di QPC tra due pacchetti.
//
//  4) RIPRESENTAZIONI DELLO STESSO FRAME
//     Ogni frame di Unity in cui non e' arrivato uno STATUS con una sequenza
//     nuova e' un frame in cui l'immagine precedente viene ridisegnata. Si
//     contano nel C#, dove la decisione viene presa.
// ============================================================================

using System.Diagnostics;

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

        // --- Frame nuovi e ripetuti (lato C#) ---------------------------------
        public ulong FramesRunning { get; private set; }
        public ulong NewFrames { get; private set; }
        public ulong RepeatedFrames { get; private set; }
        public double RepeatRatio => FramesRunning > 0 ? (double)RepeatedFrames / FramesRunning : 0.0;

        // --- Dal plugin nativo (cumulativi) -----------------------------------
        /// <summary>Consume chiesti con uno STATUS fresco ma buffer non acquisibile: deve restare a zero.</summary>
        public ulong ConsumeFailures { get; private set; }
        /// <summary>Buffer mai consumati restituiti a Unreal (Unity piu' lento di Unreal).</summary>
        public ulong StaleDrained { get; private set; }
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

        /// <summary>Da chiamare a ogni riconnessione / cambio di modalita'.</summary>
        public void Reset()
        {
            HasSample = false;
            LatencyMs = 0.0;
            LatencyMsSmoothed = 0.0;
            LatencyMsMin = double.MaxValue;
            LatencyMsMax = 0.0;
            AgeFrames = 0;
            FramesRunning = 0;
            NewFrames = 0;
            RepeatedFrames = 0;
            _hasPrevUeSample = false;
        }

        public void CountNewFrame(long ageFrames)
        {
            ++FramesRunning;
            ++NewFrames;
            AgeFrames = ageFrames;
        }

        public void CountRepeat()
        {
            ++FramesRunning;
            ++RepeatedFrames;
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
            ConsumeFailures = stats.AcquireTimeouts;
            StaleDrained    = stats.StaleDrained;
            MarkerReads     = stats.MarkerReads;
            MarkerInvalid   = stats.MarkerInvalid;
        }
    }
}
