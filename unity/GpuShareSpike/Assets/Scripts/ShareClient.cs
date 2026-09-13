// ============================================================================
//  ShareClient  --  orchestratore lato Unity.
//
//  Sequenza di avvio:
//    1. self test del protocollo e della ABI nativa (fallisce subito e forte)
//    2. apre il canale UDP e manda HELLO (ripetuto finche' non arriva risposta)
//    3. all'HANDSHAKE: GpuShare_Configure + evento GS_EVENT_CONFIGURE
//    4. quando il plugin ha aperto le shared texture: Texture2D.CreateExternalTexture
//    5. regime: pose -> IssuePluginEvent(consume) -> metriche
//
//  ORDINE DENTRO IL FRAME (conta):
//  GL.IssuePluginEvent inserisce la callback NELLO STREAM di comandi di render
//  nel punto in cui viene chiamata. Chiamandola da Update, il consume avviene
//  PRIMA che la camera disegni il quad, quindi il quad mostra il frame appena
//  acquisito e non quello precedente.
// ============================================================================

using System;
using UnityEngine;

namespace GpuShareSpike
{
    [DefaultExecutionOrder(-100)]
    public sealed class ShareClient : MonoBehaviour
    {
        [Header("Connessione")]
        public string UnrealHost = "127.0.0.1";
        public int UnrealPort = Protocol.DefaultUePort;
        [Tooltip("Secondi tra un HELLO e il successivo finche' Unreal non risponde.")]
        public float HelloRetrySeconds = 0.5f;

        [Header("Canali richiesti")]
        public bool WantDepth = true;
        public bool WantCube = false;

        [Header("Presentazione")]
        [Tooltip("La camera che E' il punto di vista. Il quad viene creato come sua figlia.")]
        public Camera PresentCamera;

        public enum PresentProjection
        {
            /// <summary>Il quad riempie lo schermo a prescindere dal FOV. Robusto, disaccoppiato.</summary>
            Orthographic,
            /// <summary>Camera prospettica: il quad riempie esattamente il frustum. Il FOV della camera e' quello spedito a Unreal, quindi la corrispondenza e' 1:1.</summary>
            Perspective,
        }

        [Tooltip("Orthographic e' il default robusto. Perspective serve se vuoi UNA sola camera vera, con il suo FOV, che comanda anche l'inquadratura di Unreal.")]
        public PresentProjection Projection = PresentProjection.Orthographic;

        [Tooltip("Distanza del quad dalla camera, in metri. Rilevante solo in Perspective.")]
        public float QuadDistance = 1.0f;
        [Tooltip("Assegna GpuSharePresent.shader. Se lasci vuoto si prova Shader.Find, che in un Player funziona solo se lo shader e' negli Always Included Shaders.")]
        public Shader PresentShader;
        [Tooltip("0 = colore, 1 = depth, 2 = zoom sui pixel del marker")]
        [Range(0, 2)] public int DebugMode = 0;
        [Tooltip("Il canale COLOR arriva codificato sRGB. Tienilo acceso in un progetto Linear.")]
        public bool SrgbDecode = true;
        [Tooltip("Metri: fondo scala della visualizzazione del depth.")]
        public float DepthRangeM = 50.0f;

        [Header("Trasporto")]
        [Tooltip("Timeout dell'AcquireSync in ms. 0 e' il valore giusto per MISURARE: aspettare mascherebbe le ripetizioni che vogliamo contare.")]
        public uint ConsumeTimeoutMs = 0;

        // --- stato ---------------------------------------------------------
        public ControlChannelClient Channel { get; private set; }
        public VirtualCameraDriver Driver => _driver;
        public LatencyTracker Tracker { get; } = new LatencyTracker();
        public string FatalError { get; private set; }
        public string StatusLine { get; private set; } = "avvio...";
        public bool AdapterMismatch { get; private set; }
        public uint UnityLuidLow { get; private set; }
        public int UnityLuidHigh { get; private set; }

        public Texture2D ColorTexture { get; private set; }
        public Texture2D DepthTexture { get; private set; }
        public Texture2D CubeAtlasTexture { get; private set; }

        private VirtualCameraDriver _driver;
        private CubemapAssembler _cubemapAssembler;
        private GameObject _quad;
        private Material _material;
        private IntPtr _renderEventFunc = IntPtr.Zero;
        private float _helloTimer;
        private bool _configureIssued;
        private bool _texturesCreated;
        private bool _hasCubeChannel;
        private NativeStats _stats;

        private static readonly int MainTexId   = Shader.PropertyToID("_MainTex");
        private static readonly int DepthTexId  = Shader.PropertyToID("_DepthTex");
        private static readonly int SrgbId      = Shader.PropertyToID("_SrgbDecode");
        private static readonly int DebugId     = Shader.PropertyToID("_DebugMode");
        private static readonly int DepthRangeId = Shader.PropertyToID("_DepthRangeM");
        private static readonly int DepthScaleId = Shader.PropertyToID("_DepthScaleToMeters");

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
                    Fail($"UnityGpuShare.dll espone la ABI v{apiVersion}, il C# si aspetta v{NativeBridge.ExpectedApiVersion}. Ricompila il plugin nativo.");
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
                return;
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

            // Per default IL PUNTO DI VISTA E' LA PRESENT CAMERA STESSA.
            // Il quad le viene creato figlio: e' uno schermo incollato davanti
            // all'occhio, quindi muovendo la camera si muovono entrambi e il
            // quad resta a riempire lo schermo. Quello che cambia e' il
            // contenuto della texture, perche' la pose va a Unreal.
            // Assegna un SourceTransform diverso solo se il punto di vista e'
            // un altro oggetto (un rig, un character controller, un XR rig).
            if (_driver.SourceTransform == null)
            {
                _driver.SourceTransform = PresentCamera.transform;
            }
            if (_driver.SourceCamera == null && Projection == PresentProjection.Perspective)
            {
                _driver.SourceCamera = PresentCamera;
            }

            PresentCamera.clearFlags = CameraClearFlags.SolidColor;
            PresentCamera.backgroundColor = Color.black;

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
                // il quad riempie esattamente il frustum a QuadDistance. La
                // corrispondenza tra cio' che Unreal renderizza e cio' che vedi
                // e' 1:1, che e' la cosa giusta quando arriverai al VR.
                PresentCamera.orthographic = false;
                PresentCamera.nearClipPlane = Mathf.Min(PresentCamera.nearClipPlane, QuadDistance * 0.5f);
                PresentCamera.farClipPlane = Mathf.Max(PresentCamera.farClipPlane, QuadDistance * 2.0f);
            }

            Channel = new ControlChannelClient();
            if (!Channel.Open(UnrealHost, UnrealPort, out string networkError))
            {
                Fail("Socket UDP non aperto: " + networkError);
                return;
            }

            SendHello();
            StatusLine = "in attesa dell'handshake di Unreal...";
        }

        private void Update()
        {
            if (FatalError != null) return;

            Tracker.TickUnityFps(Time.unscaledDeltaTime);

            Channel.Poll();

            if (!Channel.HandshakeReceived)
            {
                _helloTimer += Time.unscaledDeltaTime;
                if (_helloTimer >= HelloRetrySeconds)
                {
                    _helloTimer = 0.0f;
                    SendHello();
                }
                return;
            }

            if (!_configureIssued)
            {
                ApplyHandshake();
                return;
            }

            if (!_texturesCreated)
            {
                TryCreateTextures();
                if (!_texturesCreated)
                {
                    // Il render thread non ha ancora eseguito GS_EVENT_CONFIGURE.
                    GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.Configure);
                    return;
                }
            }

            // --- regime ------------------------------------------------------

            // Il frame_id della pose E' Time.frameCount: cosi' l'eta' in frame si
            // calcola come una sottrazione, senza tenere tabelle di corrispondenza.
            ulong frameId = (ulong)Time.frameCount;
            long qpc = ControlChannelClient.Qpc();

            Channel.SendPose(
                _driver.Position, _driver.Rotation, frameId, qpc,
                _driver.FovYDeg, _driver.CurrentAspect(), _driver.NearM, _driver.FarM);

            NativeBridge.GpuShare_SetFrameContext(frameId);

            if (Channel.HasStatus)
            {
                var status = Channel.LastStatus;
                for (int i = 0; i < status.Groups.Length; ++i)
                {
                    if (status.Groups[i].Valid)
                    {
                        NativeBridge.GpuShare_SetGroupReady(
                            status.Groups[i].GroupId, status.Groups[i].ReadyIndex, status.Groups[i].Sequence);
                    }
                }
                Tracker.TickUnrealFps(status.UeFrameCounter, status.Qpc, Time.unscaledDeltaTime);
            }

            GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.ConsumeMain);
            if (_hasCubeChannel)
            {
                GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.ConsumeCube);
            }

            if (NativeBridge.GpuShare_GetLatestFrameInfo(out NativeFrameInfo frameInfo) != 0)
            {
                Tracker.TickFrameInfo(frameInfo);
            }
            if (NativeBridge.GpuShare_GetStats(out _stats) != 0)
            {
                Tracker.TickNativeStats(_stats);
            }

            if (_hasCubeChannel && CubeAtlasTexture != null)
            {
                _cubemapAssembler.Update(CubeAtlasTexture);
            }

            UpdateQuad();
            UpdateMaterial();

            StatusLine = "in esecuzione";
        }

        private void SendHello()
        {
            var flags = Protocol.HelloFlags.None;
            if (WantDepth) flags |= Protocol.HelloFlags.WantDepth;
            if (WantCube) flags |= Protocol.HelloFlags.WantCube;
            Channel.SendHello(UnityLuidLow, UnityLuidHigh, flags);
        }

        private void ApplyHandshake()
        {
            var handshake = Channel.Handshake;

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
                Fail("Handshake senza canali: Unreal non e' riuscito a creare le superfici condivise (controlla il suo log).");
                return;
            }

            foreach (var channel in handshake.Channels)
            {
                if (channel.ChannelId == (uint)Protocol.ChannelId.Cube) _hasCubeChannel = true;
            }

            if (NativeBridge.GpuShare_Configure(
                    handshake.Channels, handshake.Channels.Length,
                    (uint)handshake.Mode, handshake.NamePrefix) == 0)
            {
                Fail("GpuShare_Configure rifiutata: " + NativeBridge.GetLastError());
                return;
            }

            GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.Configure);
            _configureIssued = true;
            StatusLine = "apertura delle texture condivise...";
        }

        private void TryCreateTextures()
        {
            if (NativeBridge.GpuShare_IsConfigured() == 0)
            {
                string error = NativeBridge.GetLastError();
                if (!string.IsNullOrEmpty(error))
                {
                    Fail("Apertura delle texture condivise fallita: " + error);
                }
                return;
            }

            ColorTexture = CreateExternal(Protocol.ChannelId.Color, TextureFormat.BGRA32);
            if (ColorTexture == null)
            {
                Fail("Il canale COLOR non e' disponibile.");
                return;
            }

            DepthTexture     = CreateExternal(Protocol.ChannelId.Depth, TextureFormat.RFloat);
            CubeAtlasTexture = CreateExternal(Protocol.ChannelId.Cube,  TextureFormat.BGRA32);

            BuildQuad();
            _texturesCreated = true;
            StatusLine = "canali aperti";
        }

        private Texture2D CreateExternal(Protocol.ChannelId channelId, TextureFormat format)
        {
            IntPtr nativePtr = NativeBridge.GpuShare_GetUnityTexturePtr((uint)channelId);
            if (nativePtr == IntPtr.Zero) return null;

            if (NativeBridge.GpuShare_GetChannelSize((uint)channelId, out uint width, out uint height) == 0)
            {
                return null;
            }

            // linear: true -> Unity NON applica la conversione sRGB al campionamento.
            // I valori grezzi arrivano allo shader e la decodifica sRGB la
            // facciamo noi, con un interruttore. Cosi' il colore e' sotto
            // controllo invece di dipendere da come Unity interpreta il flag.
            var texture = Texture2D.CreateExternalTexture(
                (int)width, (int)height, format, /*mipChain*/ false, /*linear*/ true, nativePtr);

            // Point: nessun filtraggio. Su un quad 1:1 e' anche piu' nitido, e
            // rende leggibili a occhio gli 8 pixel del marker in alto a sinistra.
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            return texture;
        }

        private void BuildQuad()
        {
            if (_quad != null) return;

            Shader shader = PresentShader != null ? PresentShader : Shader.Find("GpuShare/Present");
            if (shader == null)
            {
                Fail("Shader 'GpuShare/Present' non trovato. Assegnalo al campo PresentShader, "
                   + "oppure aggiungilo in Project Settings > Graphics > Always Included Shaders.");
                return;
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

            if (ColorTexture != null) _material.SetTexture(MainTexId, ColorTexture);
            if (DepthTexture != null) _material.SetTexture(DepthTexId, DepthTexture);

            _material.SetFloat(SrgbId, SrgbDecode ? 1.0f : 0.0f);
            _material.SetFloat(DebugId, DebugMode);
            _material.SetFloat(DepthRangeId, Mathf.Max(0.01f, DepthRangeM));
            _material.SetFloat(DepthScaleId,
                Channel.HasStatus && Channel.LastStatus.DepthScaleToMeters > 0.0f
                    ? Channel.LastStatus.DepthScaleToMeters
                    : 0.01f);
        }

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
                Channel.SendBye();
                Channel.Dispose();
                Channel = null;
            }

            if (_renderEventFunc != IntPtr.Zero)
            {
                GL.IssuePluginEvent(_renderEventFunc, (int)Protocol.RenderEvent.Shutdown);
            }

            try { NativeBridge.GpuShare_Shutdown(); } catch (DllNotFoundException) { /* mai caricata */ }

            _cubemapAssembler?.Dispose();
        }
    }
}
