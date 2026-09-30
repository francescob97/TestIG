// ============================================================================
//  ShareClient  --  orchestratore lato Unity.
//
//  ------------------------------------------------------------------------
//  MODALITA'
//
//    Desktop   una vista. La camera e' il punto di vista, il quad le e' figlio
//              (il comportamento "stabile" di sempre).
//    VrMono    una vista renderizzata da Unreal dal CENTRO della testa, con un
//              frustum che copre entrambi gli occhi. Mostrata in entrambi gli
//              occhi come immagine all'infinito: niente profondita' stereo,
//              meta' del costo di rendering lato Unreal.
//    VrStereo  due viste, una per occhio, ciascuna col frustum asimmetrico
//              esatto di quell'occhio. Profondita' vera, costo doppio.
//    Auto      VrStereo se all'avvio c'e' un visore attivo, altrimenti Desktop.
//
//  La modalita' si sceglie nell'inspector, da riga di comando
//  (-gpushare-mode desktop|vrmono|vrstereo|auto) o a runtime col tasto F2.
//  Unreal non va riavviato: riceve un HELLO con la nuova forma dello stream e
//  ricrea le sue superfici.
//
//  ------------------------------------------------------------------------
//  CICLO DI VITA DEL COLLEGAMENTO
//
//    WaitingHandshake  manda HELLO (ripetuto) con un request_id nuovo
//    OpeningTextures   handshake accettato, il plugin apre le texture condivise
//    Running           pose -> consume -> presentazione -> metriche
//
//  Ogni riconnessione (cambio modalita', Unreal riavviato, silenzio prolungato)
//  usa un request_id nuovo: gli handshake di richieste vecchie ancora in volo
//  vengono ignorati invece di aprire texture della configurazione sbagliata.
//
//  ------------------------------------------------------------------------
//  QUANDO SI CONSUMA UN FRAME
//
//  Solo quando arriva uno STATUS con una sequenza NUOVA. Unreal manda lo STATUS
//  dopo aver rilasciato il buffer, quindi a quel punto il buffer e' pronto e il
//  suo contenuto e' proprio il frame di quello STATUS: sappiamo con certezza
//  quale pose mostra, ed e' cio' che serve per riproiettarlo in VR.
//  Nei frame senza sequenza nuova Unity ripresenta il frame precedente: e' una
//  "ripetizione", e la contiamo.
// ============================================================================

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR;

namespace GpuShareSpike
{
    [DefaultExecutionOrder(-100)]
    public sealed class ShareClient : MonoBehaviour
    {
        public enum ViewMode
        {
            Auto,
            Desktop,
            VrMono,
            VrStereo,
        }

        public enum PresentProjection
        {
            /// <summary>Il quad riempie lo schermo a prescindere dal FOV. Robusto, disaccoppiato.</summary>
            Orthographic,
            /// <summary>Camera prospettica: il quad riempie esattamente il frustum. Il FOV della camera e' quello spedito a Unreal, quindi la corrispondenza e' 1:1.</summary>
            Perspective,
        }

        public enum SrgbDecodeMode
        {
            /// <summary>Decodifica solo se il progetto e' in Linear color space. E' la scelta corretta in entrambi i casi.</summary>
            Auto,
            On,
            Off,
        }

        public enum ReprojectionMode
        {
            /// <summary>L'immagine resta ferma nel mondo: si corregge la rotazione della testa avvenuta dopo il render di Unreal.</summary>
            Rotational,
            /// <summary>L'immagine segue rigidamente la testa. Solo per confronto: mostra la latenza senza maschera.</summary>
            Off,
        }

        [Header("Connessione")]
        public string UnrealHost = "127.0.0.1";
        public int UnrealPort = Protocol.DefaultUePort;
        [Tooltip("Secondi tra un HELLO e il successivo finche' Unreal non risponde.")]
        public float HelloRetrySeconds = 0.5f;
        [Tooltip("Senza STATUS da Unreal per questo tempo, si riconnette (Unreal riavviato, bloccato...).")]
        public float LinkTimeoutSeconds = 1.5f;

        [Header("Modalita'")]
        [Tooltip("Auto = VR stereo se c'e' un visore attivo all'avvio, altrimenti desktop. Sovrascrivibile con -gpushare-mode sulla riga di comando.")]
        public ViewMode Mode = ViewMode.Auto;
        [Tooltip("Tasto per ciclare Desktop -> VR mono -> VR stereo a runtime.")]
        public KeyCode CycleModeKey = KeyCode.F2;
        [Tooltip("Permette a questo script di avviare/fermare l'XR (serve XR Plug-in Management). Consigliato con 'Initialize XR on Startup' SPENTO.")]
        public bool AllowXrStartStop = true;

        [Header("Canali richiesti")]
        public bool WantDepth = true;
        public bool WantCube = false;

        [Header("Presentazione desktop")]
        [Tooltip("La camera che E' il punto di vista. In desktop il quad viene creato come sua figlia; in VR e' la camera dell'XR rig.")]
        public Camera PresentCamera;
        [Tooltip("Orthographic e' il default robusto. Perspective serve se vuoi UNA sola camera vera, con il suo FOV, che comanda anche l'inquadratura di Unreal.")]
        public PresentProjection Projection = PresentProjection.Orthographic;
        [Tooltip("Distanza del quad dalla camera, in metri. Rilevante solo in Perspective.")]
        public float QuadDistance = 1.0f;
        [Tooltip("Assegna GpuSharePresent.shader. Se lasci vuoto si prova Shader.Find, che in un Player funziona solo se lo shader e' negli Always Included Shaders.")]
        public Shader PresentShader;
        [Tooltip("Risoluzione della vista chiesta a Unreal in desktop.")]
        public Vector2Int DesktopResolution = new Vector2Int(1920, 1080);

        [Header("Presentazione VR")]
        [Tooltip("Assegna GpuShareVrPresent.shader.")]
        public Shader VrPresentShader;
        [Tooltip("Moltiplicatore della risoluzione per occhio chiesta a Unreal, rispetto a quella dell'eye texture del visore.")]
        [Range(0.25f, 2.0f)] public float VrResolutionScale = 1.0f;
        [Tooltip("Gradi di frustum in piu' su ogni lato: il bordo che la riproiezione usa quando la testa gira dopo il render di Unreal.")]
        [Range(0.0f, 30.0f)] public float VrFovMarginDeg = 8.0f;
        public ReprojectionMode Reprojection = ReprojectionMode.Rotational;
        [Tooltip("Colora le zone fuori dal frustum renderizzato (margine insufficiente) invece di lasciarle nere.")]
        public bool ShowReprojectionEdges = true;

        [Header("Colore e debug")]
        public SrgbDecodeMode SrgbDecoding = SrgbDecodeMode.Auto;
        [Tooltip("0 = colore, 1 = depth, 2 = zoom sui pixel del marker (solo desktop)")]
        [Range(0, 2)] public int DebugMode = 0;
        [Tooltip("Metri: fondo scala della visualizzazione del depth.")]
        public float DepthRangeM = 50.0f;

        [Header("Trasporto")]
        [Tooltip("Timeout dell'AcquireSync in ms. 0 e' il valore giusto: si consuma solo quando lo STATUS dice che il frame c'e'.")]
        public uint ConsumeTimeoutMs = 0;

        // --- stato pubblico (per l'HUD) -------------------------------------
        public ControlChannelClient Channel { get; private set; }
        public LatencyTracker Tracker { get; } = new LatencyTracker();
        public VirtualCameraDriver Driver => _driver;
        public string FatalError { get; private set; }
        public string Notice { get; private set; } = string.Empty;
        public string StatusLine { get; private set; } = "avvio...";
        public bool AdapterMismatch { get; private set; }
        public uint UnityLuidLow { get; private set; }
        public int UnityLuidHigh { get; private set; }

        public ViewMode ActiveMode => _activeMode;
        public int ActiveViewCount => _handshakeApplied ? _activeHandshake.ViewCount : 0;
        public Vector2Int RequestedViewSize { get; private set; }
        public string EyeSource => _eyes.Source;
        public bool EyeSourceIsFallback => _eyes.IsFallback;
        public bool IsVr => _activeMode == ViewMode.VrMono || _activeMode == ViewMode.VrStereo;
        public bool ColorDecodeActive => ShouldDecodeSrgb();

        public Texture2D ColorTexture => _colorTextures[0];
        public Texture2D ColorTextureRight => _colorTextures[1];
        public Texture2D DepthTexture => _depthTextures[0];
        public Texture2D CubeAtlasTexture { get; private set; }

        // --- stato interno ---------------------------------------------------
        private enum LinkState { Idle, StartingXr, WaitingHandshake, OpeningTextures, Running }

        /// <summary>Stato di un gruppo (MAIN o CUBE) ricostruito dagli STATUS.</summary>
        private sealed class GroupTrack
        {
            public uint LastConsumedSeq;
            public uint LatestSeq;
            public uint LatestReady;
            public ulong LatestFrameId;
            public readonly uint[] BufferSeq = new uint[Protocol.BuffersPerChannel];
            public readonly ulong[] BufferFrameId = new ulong[Protocol.BuffersPerChannel];

            /// <summary>Pubblicazioni consecutive nello STESSO buffer (normalmente si alternano).</summary>
            public int SameReadyStreak;

            public void Reset()
            {
                LastConsumedSeq = LatestSeq = LatestReady = 0;
                LatestFrameId = 0;
                SameReadyStreak = 0;
                Array.Clear(BufferSeq, 0, BufferSeq.Length);
                Array.Clear(BufferFrameId, 0, BufferFrameId.Length);
            }
        }

        /// <summary>
        /// Matrici con cui e' stata SPEDITA una pose, indicizzate per frame_id.
        /// Quando un frame di Unreal arriva sappiamo a quale pose risponde (lo
        /// dice lo STATUS): da qui recuperiamo le matrici esatte per riproiettarlo.
        /// </summary>
        private struct PoseRecord
        {
            public ulong FrameId;
            public Matrix4x4 ViewProjection0;
            public Matrix4x4 ViewProjection1;
            public bool Valid;
        }

        private const int PoseRingSize = 256;

        private LinkState _link = LinkState.Idle;
        private ViewMode _activeMode = ViewMode.Desktop;
        private uint _requestId;
        private bool _handshakeApplied;
        private Protocol.HandshakeInfo _activeHandshake;
        private bool _hasCubeChannel;
        private bool _switching;
        private float _helloTimer;
        private float _openingStartTime;
        private float _lastStatusTime;
        private ulong _displayedFrameId;

        private readonly GroupTrack[] _groups = { new GroupTrack(), new GroupTrack() };
        private readonly PoseRecord[] _poseRing = new PoseRecord[PoseRingSize];
        private readonly Protocol.ViewPose[] _viewPoses = new Protocol.ViewPose[Protocol.MaxViews];
        private readonly XrEyes _eyes = new XrEyes();

        private readonly Texture2D[] _colorTextures = new Texture2D[Protocol.MaxViews];
        private readonly Texture2D[] _depthTextures = new Texture2D[Protocol.MaxViews];

        private VirtualCameraDriver _driver;
        private CubemapAssembler _cubemapAssembler;
        private VrPresenter _vrPresenter;
        private GameObject _quad;
        private Material _material;
        private IntPtr _renderEventFunc = IntPtr.Zero;
        private NativeStats _stats;

        private static readonly int MainTexId    = Shader.PropertyToID("_MainTex");
        private static readonly int DepthTexId   = Shader.PropertyToID("_DepthTex");
        private static readonly int SrgbId       = Shader.PropertyToID("_SrgbDecode");
        private static readonly int DebugId      = Shader.PropertyToID("_DebugMode");
        private static readonly int DepthRangeId = Shader.PropertyToID("_DepthRangeM");
        private static readonly int DepthScaleId = Shader.PropertyToID("_DepthScaleToMeters");

        // =====================================================================
        //  Avvio
        // =====================================================================

        private void Awake()
        {
            _driver = GetComponent<VirtualCameraDriver>();
            if (_driver == null)
            {
                _driver = gameObject.AddComponent<VirtualCameraDriver>();
            }

            _cubemapAssembler = new CubemapAssembler();

            if (!Protocol.SelfTest(out string protocolError))
            {
                Fail("Self test del protocollo fallito: " + protocolError);
                return;
            }

            try
            {
                int apiVersion = NativeBridge.GpuShare_GetApiVersion();
                if (apiVersion != NativeBridge.ExpectedApiVersion)
                {
                    Fail($"UnityGpuShare.dll espone la ABI v{apiVersion}, il C# si aspetta v{NativeBridge.ExpectedApiVersion}. " +
                         "Ricompila il plugin nativo (unity-native/build.bat) A EDITOR CHIUSO.");
                    return;
                }
            }
            catch (DllNotFoundException)
            {
                Fail("UnityGpuShare.dll non trovata. Compilala con unity-native/ (vedi docs/02-build-unity.md) "
                   + "e verifica che sia in Assets/Plugins/x86_64/.");
                return;
            }
            catch (EntryPointNotFoundException exception)
            {
                Fail("UnityGpuShare.dll trovata ma una funzione manca: " + exception.Message
                   + ". Quasi sempre e' una DLL vecchia rimasta in memoria: chiudi l'editor, ricompila, riapri.");
                return;
            }

            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
            {
                Fail($"Unity sta girando su {SystemInfo.graphicsDeviceType}, serve Direct3D11. "
                   + "Player Settings > Other Settings: togli 'Auto Graphics API for Windows' e lascia solo Direct3D11.");
            }
        }

        private void Start()
        {
            if (FatalError != null) return;

            if (NativeBridge.GpuShare_IsDeviceReady() == 0)
            {
                Fail("Il plugin nativo non ha agganciato il device D3D11: " + NativeBridge.GetLastError());
                return;
            }

            if (NativeBridge.GpuShare_GetAdapterLuid(out uint luidLow, out int luidHigh) != 0)
            {
                UnityLuidLow = luidLow;
                UnityLuidHigh = luidHigh;
            }

            _renderEventFunc = NativeBridge.GpuShare_GetRenderEventFunc();
            NativeBridge.GpuShare_SetConsumeTimeoutMs(ConsumeTimeoutMs);

            if (PresentCamera == null) PresentCamera = Camera.main;
            if (PresentCamera == null)
            {
                Fail("Nessuna camera: assegna PresentCamera o metti una Main Camera in scena.");
                return;
            }

            // Per default IL PUNTO DI VISTA E' LA PRESENT CAMERA STESSA (desktop).
            // Assegna un SourceTransform diverso solo se il punto di vista e' un
            // altro oggetto (un rig, un character controller).
            if (_driver.SourceTransform == null)
            {
                _driver.SourceTransform = PresentCamera.transform;
            }
            if (_driver.SourceCamera == null && Projection == PresentProjection.Perspective)
            {
                _driver.SourceCamera = PresentCamera;
            }

            Channel = new ControlChannelClient();
            Channel.HandshakeReceived += OnHandshake;
            Channel.StatusReceived += OnStatus;
            if (!Channel.Open(UnrealHost, UnrealPort, out string networkError))
            {
                Fail("Socket UDP non aperto: " + networkError);
                return;
            }

            StartCoroutine(SwitchModeRoutine(ReadModeFromCommandLine(Mode)));
        }

        private static ViewMode ReadModeFromCommandLine(ViewMode fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
            {
                if (!string.Equals(args[i], "-gpushare-mode", StringComparison.OrdinalIgnoreCase)) continue;
                switch (args[i + 1].ToLowerInvariant())
                {
                    case "desktop":  return ViewMode.Desktop;
                    case "vrmono":   return ViewMode.VrMono;
                    case "vrstereo": return ViewMode.VrStereo;
                    case "auto":     return ViewMode.Auto;
                }
            }
            return fallback;
        }

        // =====================================================================
        //  Cambio di modalita'
        // =====================================================================

        public void RequestMode(ViewMode mode)
        {
            if (_switching || FatalError != null) return;
            StartCoroutine(SwitchModeRoutine(mode));
        }

        private IEnumerator SwitchModeRoutine(ViewMode requested)
        {
            _switching = true;
            ResetLink();
            Notice = string.Empty;

            ViewMode target = requested;
            if (target == ViewMode.Auto)
            {
                target = XrRuntime.IsActive ? ViewMode.VrStereo : ViewMode.Desktop;
            }

            bool wantXr = target == ViewMode.VrMono || target == ViewMode.VrStereo;

            if (wantXr && !XrRuntime.IsActive)
            {
                if (!AllowXrStartStop || !XrRuntime.CanControl)
                {
                    Notice = "VR richiesto ma l'XR non e' attivo e non posso avviarlo " +
                             (XrRuntime.CanControl ? "(AllowXrStartStop spento)." : "(XR Plug-in Management/OpenXR non configurato, vedi docs/06-vr.md).") +
                             " Resto in desktop.";
                    target = ViewMode.Desktop;
                    wantXr = false;
                }
                else
                {
                    _link = LinkState.StartingXr;
                    StatusLine = "avvio dell'XR...";
                    string xrError = null;
                    yield return XrRuntime.StartRoutine(error => xrError = error);

                    // Il display puo' metterci qualche frame a partire davvero.
                    float waited = 0.0f;
                    while (!XrRuntime.IsActive && waited < 5.0f)
                    {
                        waited += Time.unscaledDeltaTime;
                        yield return null;
                    }

                    if (!XrRuntime.IsActive)
                    {
                        Notice = (xrError ?? "L'XR non e' partito entro 5 secondi.") + " Resto in desktop.";
                        target = ViewMode.Desktop;
                        wantXr = false;
                    }
                }
            }
            else if (!wantXr && XrRuntime.IsActive)
            {
                if (AllowXrStartStop && XrRuntime.CanControl)
                {
                    XrRuntime.Stop();
                    yield return null;
                }
                else
                {
                    Notice = "Desktop richiesto ma l'XR e' attivo e non posso fermarlo: il quad finira' nel visore. " +
                             "Spegni 'Initialize XR on Startup' o abilita AllowXrStartStop.";
                }
            }

            if (wantXr)
            {
                // Un frame perche' le matrici per occhio siano disponibili.
                yield return null;
            }

            _activeMode = target;
            ConfigureCameraForMode();
            Tracker.Reset();

            _link = LinkState.WaitingHandshake;
            _helloTimer = HelloRetrySeconds;   // il primo HELLO parte subito
            StatusLine = "in attesa dell'handshake di Unreal...";
            _switching = false;
        }

        private void ConfigureCameraForMode()
        {
            PresentCamera.clearFlags = CameraClearFlags.SolidColor;
            PresentCamera.backgroundColor = Color.black;

            if (IsVr)
            {
                // In VR la camera e' guidata dal visore (Tracked Pose Driver):
                // il driver desktop non deve scriverle sopra.
                _driver.enabled = false;
                PresentCamera.orthographic = false;
                return;
            }

            _driver.enabled = true;

            if (Projection == PresentProjection.Orthographic)
            {
                // Il quad copre lo schermo a prescindere dal FOV: il FOV spedito
                // a Unreal resta un parametro indipendente del driver.
                PresentCamera.orthographic = true;
                PresentCamera.orthographicSize = 0.5f;
                PresentCamera.nearClipPlane = 0.01f;
                PresentCamera.farClipPlane = 10.0f;
            }
            else
            {
                // Una sola camera vera: il suo FOV e' quello che va a Unreal, e
                // il quad riempie esattamente il frustum a QuadDistance.
                PresentCamera.orthographic = false;
                PresentCamera.nearClipPlane = Mathf.Min(PresentCamera.nearClipPlane, QuadDistance * 0.5f);
                PresentCamera.farClipPlane = Mathf.Max(PresentCamera.farClipPlane, QuadDistance * 2.0f);
            }
        }

        /// <summary>Butta via texture e presentazione e prepara una richiesta nuova.</summary>
        private void ResetLink()
        {
            TearDownPresentation();

            if (_handshakeApplied && _renderEventFunc != IntPtr.Zero)
            {
                // Le Texture2D che avvolgevano le texture native sono gia' state
                // distrutte qui sopra: ora il plugin puo' rilasciarle.
                GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.Shutdown);
            }

            ++_requestId;
            _handshakeApplied = false;
            _hasCubeChannel = false;
            _link = LinkState.Idle;
            foreach (GroupTrack group in _groups) group.Reset();
        }

        // =====================================================================
        //  Frame
        // =====================================================================

        private void Update()
        {
            if (FatalError != null || Channel == null) return;

            Tracker.TickUnityFps(Time.unscaledDeltaTime);

            if (SafeInput.GetKeyDown(CycleModeKey))
            {
                RequestMode(NextMode(_activeMode));
            }

            Channel.Poll();   // gli handler OnHandshake/OnStatus girano qui dentro

            if (!string.IsNullOrEmpty(Channel.VersionMismatch))
            {
                Notice = Channel.VersionMismatch;
            }

            if (_switching) return;

            switch (_link)
            {
                case LinkState.WaitingHandshake:
                    _helloTimer += Time.unscaledDeltaTime;
                    if (_helloTimer >= HelloRetrySeconds)
                    {
                        _helloTimer = 0.0f;
                        SendHello();
                    }
                    break;

                case LinkState.OpeningTextures:
                    // La pose parte gia' ora: cosi' il primo frame di Unreal e'
                    // pronto appena le texture sono aperte.
                    SendPoseForThisFrame();
                    TryFinishOpening();
                    break;

                case LinkState.Running:
                    SendPoseForThisFrame();
                    ConsumeAndPresent();
                    CheckLinkAlive();
                    break;
            }
        }

        private static ViewMode NextMode(ViewMode current)
        {
            switch (current)
            {
                case ViewMode.Desktop: return ViewMode.VrMono;
                case ViewMode.VrMono:  return ViewMode.VrStereo;
                default:               return ViewMode.Desktop;
            }
        }

        // =====================================================================
        //  Handshake
        // =====================================================================

        private void SendHello()
        {
            var flags = Protocol.HelloFlags.None;
            if (WantDepth) flags |= Protocol.HelloFlags.WantDepth;
            if (WantCube) flags |= Protocol.HelloFlags.WantCube;

            int views = _activeMode == ViewMode.VrStereo ? 2 : 1;
            Vector2Int size = ComputeRequestedViewSize();
            RequestedViewSize = size;

            Channel.SendHello(UnityLuidLow, UnityLuidHigh, flags, views, size.x, size.y, _requestId);
        }

        /// <summary>
        /// Risoluzione per vista da chiedere a Unreal. In VR parte dall'eye
        /// texture del visore e la allarga in proporzione al frustum che
        /// chiediamo in piu' (margine di riproiezione, e in mono l'unione dei due
        /// occhi), cosi' la densita' di pixel resta quella del visore.
        /// </summary>
        private Vector2Int ComputeRequestedViewSize()
        {
            if (!IsVr)
            {
                return new Vector2Int(Mathf.Max(16, DesktopResolution.x), Mathf.Max(16, DesktopResolution.y));
            }

            int eyeWidth = XRSettings.eyeTextureWidth;
            int eyeHeight = XRSettings.eyeTextureHeight;
            if (eyeWidth <= 0 || eyeHeight <= 0 || !XrPoseSource.TryGetEyes(PresentCamera, _eyes))
            {
                return Vector2Int.zero;   // Unreal usera' il suo default
            }

            ViewMath.TangentsFromProjection(_eyes.Projection[0], out float l, out float r, out float d, out float u);
            float eyeSpanX = r - l;
            float eyeSpanY = u - d;

            if (_activeMode == ViewMode.VrMono)
            {
                ComputeMonoTangents(out l, out r, out d, out u);
            }
            ViewMath.ExpandTangents(ref l, ref r, ref d, ref u, VrFovMarginDeg);

            float scaleX = (r - l) / eyeSpanX;
            float scaleY = (u - d) / eyeSpanY;
            return new Vector2Int(
                Mathf.Max(16, Mathf.RoundToInt(eyeWidth * VrResolutionScale * scaleX)),
                Mathf.Max(16, Mathf.RoundToInt(eyeHeight * VrResolutionScale * scaleY)));
        }

        private void OnHandshake(Protocol.HandshakeInfo handshake)
        {
            // Solo la risposta alla richiesta CORRENTE. Handshake di richieste
            // vecchie (per esempio della modalita' precedente) arrivano ancora
            // per un po': aprirli significherebbe aprire la configurazione
            // sbagliata.
            if (handshake.RequestId != _requestId) return;
            if (_link != LinkState.WaitingHandshake) return;   // duplicato di un HELLO ripetuto
            ApplyHandshake(handshake);
        }

        private void ApplyHandshake(Protocol.HandshakeInfo handshake)
        {
            TearDownPresentation();

            _activeHandshake = handshake;

            // Se i due processi sono su GPU diverse, OpenSharedResource1 fallira'
            // con un HRESULT che non dice nulla. Lo diciamo noi, prima.
            AdapterMismatch = (UnityLuidLow != handshake.AdapterLuidLow || UnityLuidHigh != handshake.AdapterLuidHigh);
            if (AdapterMismatch)
            {
                Debug.LogError($"[GpuShare] MISMATCH DI ADAPTER: Unity {UnityLuidLow}:{UnityLuidHigh}, "
                             + $"Unreal {handshake.AdapterLuidLow}:{handshake.AdapterLuidHigh}. "
                             + "La condivisione non puo' funzionare tra GPU diverse: forza entrambi gli eseguibili "
                             + "sulla stessa GPU in Impostazioni Windows > Schermo > Grafica.");
            }

            if (handshake.Channels == null || handshake.Channels.Length == 0)
            {
                Notice = "Handshake senza canali: Unreal non e' riuscito a creare le superfici (controlla il suo log).";
                return;   // resta in attesa: il prossimo HELLO riprova
            }

            _hasCubeChannel = false;
            foreach (NativeChannelDesc channel in handshake.Channels)
            {
                if (channel.ChannelId == (uint)Protocol.ChannelId.Cube) _hasCubeChannel = true;
            }

            if (NativeBridge.GpuShare_Configure(handshake.Channels, handshake.Channels.Length,
                                                (uint)handshake.Mode, handshake.NamePrefix) == 0)
            {
                Notice = "GpuShare_Configure rifiutata: " + NativeBridge.GetLastError();
                return;
            }

            GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.Configure);

            foreach (GroupTrack group in _groups) group.Reset();
            _handshakeApplied = true;
            _link = LinkState.OpeningTextures;
            _openingStartTime = Time.realtimeSinceStartup;
            _lastStatusTime = Time.realtimeSinceStartup;
            StatusLine = "apertura delle texture condivise...";
        }

        private void TryFinishOpening()
        {
            if (NativeBridge.GpuShare_IsConfigured() == 0)
            {
                string error = NativeBridge.GetLastError();
                if (!string.IsNullOrEmpty(error))
                {
                    // Non fatale: tipicamente un handshake invalidato da un
                    // cambio di configurazione dall'altra parte. Si riprova con
                    // una richiesta nuova.
                    Notice = "Apertura delle texture fallita: " + error;
                    Relink();
                    return;
                }
                if (Time.realtimeSinceStartup - _openingStartTime > 5.0f)
                {
                    Notice = "Apertura delle texture senza risposta dal render thread per 5 s: riprovo.";
                    Relink();
                }
                return;
            }

            CreateTextures();
            if (!BuildPresentation()) return;

            _link = LinkState.Running;
            _lastStatusTime = Time.realtimeSinceStartup;
            StatusLine = "in esecuzione";
        }

        /// <summary>Riconnessione senza cambiare modalita'.</summary>
        private void Relink()
        {
            ResetLink();
            _link = LinkState.WaitingHandshake;
            _helloTimer = HelloRetrySeconds;
            StatusLine = "riconnessione...";
        }

        private void CheckLinkAlive()
        {
            if (Time.realtimeSinceStartup - _lastStatusTime <= LinkTimeoutSeconds) return;

            // Unreal e' stato riavviato o si e' bloccato. Teniamo a schermo
            // l'ultimo frame (le texture di Unity sono nostre, restano valide) e
            // ricominciamo il giro con una richiesta nuova.
            Notice = $"Nessuno STATUS da Unreal da {LinkTimeoutSeconds:F1} s: riconnessione...";
            ++_requestId;
            _link = LinkState.WaitingHandshake;
            _helloTimer = HelloRetrySeconds;
            StatusLine = "riconnessione...";
        }

        private void OnStatus(Protocol.StatusInfo status)
        {
            // Solo STATUS della configurazione aperta: dopo una riconfigurazione
            // le sequenze ripartono da 1, e mescolarle darebbe indici sbagliati.
            if (!_handshakeApplied || status.ConfigId != _activeHandshake.ConfigId) return;

            _lastStatusTime = Time.realtimeSinceStartup;

            for (int g = 0; g < _groups.Length && g < status.Groups.Length; ++g)
            {
                Protocol.GroupStatus groupStatus = status.Groups[g];
                if (!groupStatus.Valid || groupStatus.ReadyIndex >= Protocol.BuffersPerChannel) continue;

                GroupTrack track = _groups[g];
                uint ready = groupStatus.ReadyIndex;

                if (groupStatus.Sequence > track.BufferSeq[ready])
                {
                    track.BufferSeq[ready] = groupStatus.Sequence;
                    track.BufferFrameId[ready] = groupStatus.FrameId;
                }
                if (groupStatus.Sequence > track.LatestSeq)
                {
                    // Il produttore preferisce sempre il buffer diverso
                    // dall'ultimo: se pubblica di fila nello STESSO, e' perche'
                    // l'altro gli e' inacquisibile (vedi ConsumeGroup).
                    track.SameReadyStreak = (track.LatestSeq != 0 && ready == track.LatestReady)
                        ? track.SameReadyStreak + 1
                        : 0;
                    track.LatestSeq = groupStatus.Sequence;
                    track.LatestReady = ready;
                    track.LatestFrameId = groupStatus.FrameId;
                }
            }
        }

        // =====================================================================
        //  Pose
        // =====================================================================

        private void SendPoseForThisFrame()
        {
            // Il frame_id della pose E' Time.frameCount: l'eta' in frame si
            // calcola come una sottrazione, senza tabelle di corrispondenza.
            ulong frameId = (ulong)Time.frameCount;
            long qpc = ControlChannelClient.Qpc();

            int views;
            float nearM, farM;

            if (IsVr)
            {
                if (!ProduceVrPoses(frameId, out views)) return;
                nearM = PresentCamera.nearClipPlane;
                farM = PresentCamera.farClipPlane;
            }
            else
            {
                // Il driver ha gia' calcolato la pose di questo frame (gira a -200).
                _viewPoses[0] = Protocol.ViewPose.Symmetric(_driver.Position, _driver.Rotation,
                                                            _driver.FovYDeg, _driver.CurrentAspect());
                views = 1;
                nearM = _driver.NearM;
                farM = _driver.FarM;
            }

            Channel.SendPose(frameId, qpc, _viewPoses, views, nearM, farM);
            NativeBridge.GpuShare_SetFrameContext(frameId);
        }

        /// <summary>
        /// Pose degli occhi dal visore. Riempie _viewPoses (relative all'origine,
        /// per Unreal) e registra le matrici di MONDO con cui Unreal
        /// renderizzera', per poterle usare quando il frame tornera' indietro.
        /// </summary>
        private bool ProduceVrPoses(ulong frameId, out int views)
        {
            views = 0;
            if (!XrPoseSource.TryGetEyes(PresentCamera, _eyes)) return false;

            Transform origin = _driver != null ? _driver.OriginTransform : null;
            var record = new PoseRecord { FrameId = frameId, Valid = true };

            if (_activeMode == ViewMode.VrStereo)
            {
                views = 2;
                for (int eye = 0; eye < 2; ++eye)
                {
                    ViewMath.PoseFromView(_eyes.View[eye], out Vector3 worldPos, out Quaternion worldRot);
                    ViewMath.TangentsFromProjection(_eyes.Projection[eye], out float l, out float r, out float d, out float u);
                    ViewMath.ExpandTangents(ref l, ref r, ref d, ref u, VrFovMarginDeg);

                    ViewMath.ToOriginSpace(origin, worldPos, worldRot, out Vector3 pos, out Quaternion rot);
                    _viewPoses[eye] = new Protocol.ViewPose
                    {
                        Position = pos, Rotation = rot,
                        TanLeft = l, TanRight = r, TanDown = d, TanUp = u,
                    };

                    Matrix4x4 viewProjection = ViewMath.ProjectionFromTangents(l, r, d, u, 0.1f, 1000.0f)
                                             * ViewMath.ViewFromPose(worldPos, worldRot);
                    if (eye == 0) record.ViewProjection0 = viewProjection;
                    else          record.ViewProjection1 = viewProjection;
                }
            }
            else
            {
                // MONO: una sola vista dal centro della testa. Posizione a meta'
                // tra gli occhi, rotazione della testa, frustum che copre
                // entrambi gli occhi. Ricavata dagli stessi dati del visore, cosi'
                // e' coerente con cio' che il visore mostra anche senza affidarsi
                // alla transform della camera.
                views = 1;
                ViewMath.PoseFromView(_eyes.View[0], out Vector3 leftPos, out Quaternion leftRot);
                ViewMath.PoseFromView(_eyes.View[1], out Vector3 rightPos, out Quaternion rightRot);
                Vector3 headPos = (leftPos + rightPos) * 0.5f;
                Quaternion headRot = Quaternion.Slerp(leftRot, rightRot, 0.5f);

                ComputeMonoTangents(out float l, out float r, out float d, out float u);
                ViewMath.ExpandTangents(ref l, ref r, ref d, ref u, VrFovMarginDeg);

                ViewMath.ToOriginSpace(origin, headPos, headRot, out Vector3 pos, out Quaternion rot);
                _viewPoses[0] = new Protocol.ViewPose
                {
                    Position = pos, Rotation = rot,
                    TanLeft = l, TanRight = r, TanDown = d, TanUp = u,
                };

                Matrix4x4 viewProjection = ViewMath.ProjectionFromTangents(l, r, d, u, 0.1f, 1000.0f)
                                         * ViewMath.ViewFromPose(headPos, headRot);
                record.ViewProjection0 = viewProjection;
                record.ViewProjection1 = viewProjection;
            }

            _poseRing[(int)(frameId % PoseRingSize)] = record;
            return true;
        }

        /// <summary>
        /// Frustum mono: l'unione dei due occhi. Si assume che gli occhi siano
        /// allineati alla testa (vero per quasi tutti i visori; quelli con
        /// display inclinati avranno solo un po' meno margine ai bordi, perche'
        /// la riproiezione usa comunque le matrici esatte che abbiamo spedito).
        /// </summary>
        private void ComputeMonoTangents(out float left, out float right, out float down, out float up)
        {
            ViewMath.TangentsFromProjection(_eyes.Projection[0], out float l0, out float r0, out float d0, out float u0);
            ViewMath.TangentsFromProjection(_eyes.Projection[1], out float l1, out float r1, out float d1, out float u1);
            left = Mathf.Min(l0, l1);
            right = Mathf.Max(r0, r1);
            down = Mathf.Min(d0, d1);
            up = Mathf.Max(u0, u1);
        }

        // =====================================================================
        //  Consume e presentazione
        // =====================================================================

        private void ConsumeAndPresent()
        {
            if (ConsumeGroup(Protocol.GroupId.Main, out ulong frameId))
            {
                _displayedFrameId = frameId;
                Tracker.CountNewFrame((long)Time.frameCount - (long)frameId);
            }
            else
            {
                Tracker.CountRepeat();
            }

            if (_hasCubeChannel)
            {
                ConsumeGroup(Protocol.GroupId.Cube, out _);
                if (CubeAtlasTexture != null) _cubemapAssembler.Update(CubeAtlasTexture);
            }

            if (NativeBridge.GpuShare_GetLatestFrameInfo(out NativeFrameInfo frameInfo) != 0)
            {
                Tracker.TickFrameInfo(frameInfo);
            }
            if (NativeBridge.GpuShare_GetStats(out _stats) != 0)
            {
                Tracker.TickNativeStats(_stats);
            }
            if (Channel.HasStatus)
            {
                Tracker.TickUnrealFps(Channel.LastStatus.UeFrameCounter, Channel.LastStatus.Qpc, Time.unscaledDeltaTime);
            }

            if (IsVr) UpdateVrPresentation();
            else
            {
                UpdateQuad();
                UpdateMaterial();
            }
        }

        /// <summary>
        /// Consuma un gruppo SOLO se c'e' uno STATUS con una sequenza nuova.
        /// L'indice del buffer, la sequenza e la richiesta di drenaggio viaggiano
        /// dentro l'id dell'evento: vedi NativeBridge.EncodeConsume.
        /// </summary>
        private bool ConsumeGroup(Protocol.GroupId group, out ulong frameId)
        {
            frameId = 0;
            GroupTrack track = _groups[(int)group];
            if (track.LatestSeq == 0 || track.LatestSeq <= track.LastConsumedSeq) return false;

            uint ready = track.LatestReady;
            uint other = ready ^ 1u;

            // L'altro buffer contiene un frame pubblicato ma mai consumato
            // (piu' vecchio di questo): va restituito a Unreal, altrimenti
            // resterebbe bloccato nello stato "consumatore" e Unreal perderebbe
            // il timeout dell'acquire a ogni frame. Vedi NativeApi.h.
            bool staleKnown = track.BufferSeq[other] > track.LastConsumedSeq && track.BufferSeq[other] < track.LatestSeq;

            // Rete di sicurezza per quando lo STATUS che annunciava l'altro
            // buffer e' andato perso: se Unreal pubblica da 3 volte di fila nello
            // stesso buffer, l'altro gli e' inacquisibile, cioe' e' fermo nello
            // stato "consumatore" con un frame vecchio. Drenarlo e' sicuro: un
            // buffer in quello stato Unreal non puo' averlo riscritto.
            bool stuckSuspected = track.SameReadyStreak >= 3;
            if (stuckSuspected) track.SameReadyStreak = 0;

            bool drainOther = staleKnown || stuckSuspected;

            Protocol.RenderEvent eventType = group == Protocol.GroupId.Main
                ? Protocol.RenderEvent.ConsumeMain
                : Protocol.RenderEvent.ConsumeCube;

            GL.IssuePluginEvent(_renderEventFunc, NativeBridge.EncodeConsume(eventType, ready, drainOther, track.LatestSeq));

            track.LastConsumedSeq = track.LatestSeq;
            frameId = track.BufferFrameId[ready];
            return true;
        }

        private void UpdateVrPresentation()
        {
            if (_vrPresenter == null || !_vrPresenter.IsReady) return;

            bool stereo = _activeHandshake.ViewCount > 1;
            _vrPresenter.SetTextures(_colorTextures[0], stereo ? _colorTextures[1] : null,
                                     _depthTextures[0], stereo ? _depthTextures[1] : null);

            // Rotational: le matrici del frame che si sta MOSTRANDO -> l'immagine
            // resta ferma nel mondo. Off: quelle del frame corrente -> l'immagine
            // segue la testa, e la latenza si vede tutta.
            ulong recordFrame = Reprojection == ReprojectionMode.Rotational ? _displayedFrameId : (ulong)Time.frameCount;
            PoseRecord record = _poseRing[(int)(recordFrame % PoseRingSize)];
            if (record.Valid && record.FrameId == recordFrame)
            {
                _vrPresenter.SetRenderMatrices(record.ViewProjection0, record.ViewProjection1);
            }

            float depthScale = Channel.HasStatus && Channel.LastStatus.DepthScaleToMeters > 0.0f
                ? Channel.LastStatus.DepthScaleToMeters
                : 0.01f;
            _vrPresenter.SetOptions(ShouldDecodeSrgb(), DebugMode == 1 ? 1 : 0, ShowReprojectionEdges, DepthRangeM, depthScale);
        }

        // =====================================================================
        //  Texture e presentazione
        // =====================================================================

        private void CreateTextures()
        {
            // In VR si campiona in punti arbitrari (riproiezione): serve il
            // filtro bilineare. In desktop il quad e' 1:1 e il point sampling
            // rende leggibili a occhio gli 8 pixel del marker.
            FilterMode filter = IsVr ? FilterMode.Bilinear : FilterMode.Point;

            for (int view = 0; view < Protocol.MaxViews; ++view)
            {
                _colorTextures[view] = CreateExternal(Protocol.ColorChannelForView(view), TextureFormat.BGRA32, filter);
                _depthTextures[view] = CreateExternal(Protocol.DepthChannelForView(view), TextureFormat.RFloat, filter);
            }
            CubeAtlasTexture = CreateExternal(Protocol.ChannelId.Cube, TextureFormat.BGRA32, FilterMode.Bilinear);
        }

        private Texture2D CreateExternal(Protocol.ChannelId channelId, TextureFormat format, FilterMode filter)
        {
            IntPtr nativePtr = NativeBridge.GpuShare_GetUnityTexturePtr((uint)channelId);
            if (nativePtr == IntPtr.Zero) return null;

            if (NativeBridge.GpuShare_GetChannelSize((uint)channelId, out uint width, out uint height) == 0)
            {
                return null;
            }

            // linear: true -> Unity NON applica la conversione sRGB al campionamento.
            // I valori grezzi arrivano allo shader e la decodifica sRGB la
            // facciamo noi, solo se serve (vedi ShouldDecodeSrgb).
            var texture = Texture2D.CreateExternalTexture(
                (int)width, (int)height, format, /*mipChain*/ false, /*linear*/ true, nativePtr);
            texture.filterMode = filter;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private bool BuildPresentation()
        {
            if (_colorTextures[0] == null)
            {
                Notice = "Il canale COLOR non e' disponibile nell'handshake.";
                Relink();
                return false;
            }

            if (IsVr)
            {
                if (!XrRuntime.IsSinglePassInstanced && _activeMode == ViewMode.VrStereo)
                {
                    Notice = $"Stereo rendering mode = {XrRuntime.StereoModeName}: serve Single Pass Instanced " +
                             "(Project Settings > XR Plug-in Management > OpenXR > Render Mode), altrimenti entrambi gli occhi vedono il sinistro.";
                }

                Shader shader = VrPresentShader != null ? VrPresentShader : Shader.Find("GpuShare/VrPresent");
                _vrPresenter = new VrPresenter();
                if (!_vrPresenter.Create(PresentCamera, shader, out string error))
                {
                    Fail(error);
                    return false;
                }
                return true;
            }

            return BuildQuad();
        }

        private bool BuildQuad()
        {
            Shader shader = PresentShader != null ? PresentShader : Shader.Find("GpuShare/Present");
            if (shader == null)
            {
                Fail("Shader 'GpuShare/Present' non trovato. Assegnalo al campo PresentShader, "
                   + "oppure aggiungilo in Project Settings > Graphics > Always Included Shaders.");
                return false;
            }

            _material = new Material(shader);

            _quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad.name = "GpuSharePresentQuad";
            var collider = _quad.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            _quad.transform.SetParent(PresentCamera.transform, false);
            _quad.transform.localPosition = new Vector3(0.0f, 0.0f,
                Projection == PresentProjection.Perspective ? QuadDistance : 1.0f);
            _quad.transform.localRotation = Quaternion.identity;
            _quad.GetComponent<MeshRenderer>().sharedMaterial = _material;
            return true;
        }

        private void TearDownPresentation()
        {
            if (_quad != null) Destroy(_quad);
            if (_material != null) Destroy(_material);
            _quad = null;
            _material = null;

            _vrPresenter?.Dispose();
            _vrPresenter = null;

            for (int view = 0; view < Protocol.MaxViews; ++view)
            {
                if (_colorTextures[view] != null) Destroy(_colorTextures[view]);
                if (_depthTextures[view] != null) Destroy(_depthTextures[view]);
                _colorTextures[view] = null;
                _depthTextures[view] = null;
            }
            if (CubeAtlasTexture != null) Destroy(CubeAtlasTexture);
            CubeAtlasTexture = null;
        }

        private void UpdateQuad()
        {
            if (_quad == null || PresentCamera == null) return;

            // Ricalcolato ogni frame perche' la finestra puo' essere ridimensionata.
            float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16.0f / 9.0f;
            float height;

            if (Projection == PresentProjection.Perspective)
            {
                // Altezza del frustum alla distanza del quad: 2 * d * tan(fov/2).
                float halfFovRad = PresentCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                height = 2.0f * QuadDistance * Mathf.Tan(halfFovRad);
            }
            else
            {
                // Camera ortografica di size S: l'altezza visibile e' 2S.
                height = PresentCamera.orthographicSize * 2.0f;
            }

            _quad.transform.localScale = new Vector3(height * aspect, height, 1.0f);
        }

        private void UpdateMaterial()
        {
            if (_material == null) return;

            if (_colorTextures[0] != null) _material.SetTexture(MainTexId, _colorTextures[0]);
            if (_depthTextures[0] != null) _material.SetTexture(DepthTexId, _depthTextures[0]);

            _material.SetFloat(SrgbId, ShouldDecodeSrgb() ? 1.0f : 0.0f);
            _material.SetFloat(DebugId, DebugMode);
            _material.SetFloat(DepthRangeId, Mathf.Max(0.01f, DepthRangeM));
            _material.SetFloat(DepthScaleId,
                Channel.HasStatus && Channel.LastStatus.DepthScaleToMeters > 0.0f
                    ? Channel.LastStatus.DepthScaleToMeters
                    : 0.01f);
        }

        /// <summary>
        /// Il canale COLOR contiene valori codificati sRGB. Vanno decodificati solo
        /// se il progetto e' in LINEAR color space: in GAMMA il framebuffer li
        /// vuole cosi' come sono, e decodificarli scurisce l'immagine.
        /// </summary>
        private bool ShouldDecodeSrgb()
        {
            switch (SrgbDecoding)
            {
                case SrgbDecodeMode.On:  return true;
                case SrgbDecodeMode.Off: return false;
                default:                 return QualitySettings.activeColorSpace == ColorSpace.Linear;
            }
        }

        // =====================================================================
        //  Errori e chiusura
        // =====================================================================

        private void Fail(string message)
        {
            FatalError = message;
            StatusLine = "ERRORE";
            Debug.LogError("[GpuShare] " + message);
            enabled = false;
        }

        private void OnDestroy()
        {
            if (Channel != null)
            {
                Channel.HandshakeReceived -= OnHandshake;
                Channel.StatusReceived -= OnStatus;
                Channel.SendBye();
                Channel.Dispose();
                Channel = null;
            }

            TearDownPresentation();

            if (_renderEventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.Shutdown);
            }

            try { NativeBridge.GpuShare_Shutdown(); } catch (DllNotFoundException) { /* mai caricata */ }

            _cubemapAssembler?.Dispose();
        }
    }
}
