// ============================================================================
//  Overlay diagnostico. IMGUI (OnGUI) di proposito: zero setup di scena, zero
//  Canvas, zero prefab da committare. Non e' UI di prodotto, e' uno strumento.
//
//  In VR l'overlay NON compare nel visore: OnGUI disegna sulla finestra di
//  Unity sul monitor (il "mirror"). E' voluto: e' uno strumento per chi misura,
//  non per chi indossa il visore.
// ============================================================================

using UnityEngine;

namespace GpuShareSpike
{
    [RequireComponent(typeof(ShareClient))]
    public sealed class HudOverlay : MonoBehaviour
    {
        public bool Visible = true;
        public KeyCode ToggleKey = KeyCode.F1;
        public int FontSize = 14;

        private ShareClient _client;
        private GUIStyle _boxStyle;
        private GUIStyle _labelStyle;
        private readonly System.Text.StringBuilder _text = new System.Text.StringBuilder(2048);

        private void Awake()
        {
            _client = GetComponent<ShareClient>();
        }

        private void Update()
        {
            if (SafeInput.GetKeyDown(ToggleKey)) Visible = !Visible;
        }

        private void OnGUI()
        {
            if (!Visible || _client == null) return;

            EnsureStyles();

            _text.Clear();

            if (_client.FatalError != null)
            {
                _text.AppendLine("<color=#ff6b6b><b>ERRORE FATALE</b></color>");
                _text.AppendLine(_client.FatalError);
                Draw(600.0f);
                return;
            }

            var tracker = _client.Tracker;
            var channel = _client.Channel;

            _text.AppendLine("<b>GPU Share Spike</b>   (F1 nasconde, F2 cambia modalita')");
            _text.AppendLine($"stato: {_client.StatusLine}");

            if (!string.IsNullOrEmpty(_client.Notice))
            {
                _text.AppendLine($"<color=#ffd166>{_client.Notice}</color>");
            }
            if (!SafeInput.Available)
            {
                _text.AppendLine($"<color=#ffd166>{SafeInput.Hint}</color>");
            }
            if (_client.AdapterMismatch)
            {
                _text.AppendLine("<color=#ff6b6b><b>MISMATCH DI ADAPTER: i due processi sono su GPU diverse</b></color>");
            }

            // --- Modalita' ------------------------------------------------------
            _text.AppendLine();
            _text.AppendLine("<b>MODALITA'</b>");
            int views = _client.ActiveViewCount;
            Vector2Int size = _client.RequestedViewSize;
            string sizeText = size.x > 0 ? $"{size.x}x{size.y}" : "default di Unreal";
            _text.AppendLine($"  attiva         {_client.ActiveMode}   ({(views > 0 ? views.ToString() : "-")} vist{(views == 1 ? "a" : "e")}, richiesta {sizeText} per vista)");
            _text.AppendLine($"  XR             {(XrRuntime.IsActive ? "attivo" : "spento")}" +
                             $"{(XrRuntime.CanControl ? "" : "   (XR Plug-in Management non trovato)")}");
            if (_client.IsVr)
            {
                _text.AppendLine($"  render mode    {XrRuntime.StereoModeName}" +
                                 (XrRuntime.IsSinglePassInstanced ? "" : "   <color=#ffd166>serve Single Pass Instanced</color>"));
                string source = _client.EyeSourceIsFallback
                    ? $"<color=#ff6b6b>{_client.EyeSource}</color>"
                    : _client.EyeSource;
                _text.AppendLine($"  pose occhi da  {source}");
                _text.AppendLine($"  riproiezione   {_client.Reprojection}   margine {_client.VrFovMarginDeg:F0} gradi");
            }
            _text.AppendLine($"  colore         decodifica sRGB {(_client.ColorDecodeActive ? "SI" : "no")}  (color space {QualitySettings.activeColorSpace})");

            // --- Latenza --------------------------------------------------------
            _text.AppendLine();
            _text.AppendLine("<b>ANELLO POSE -> PIXEL</b>");
            if (tracker.HasSample)
            {
                _text.AppendLine($"  latenza        {tracker.LatencyMs,7:F2} ms   (media {tracker.LatencyMsSmoothed:F2})");
                _text.AppendLine($"  min / max      {tracker.LatencyMsMin,7:F2} / {tracker.LatencyMsMax:F2} ms");
                _text.AppendLine($"  eta' del frame {tracker.AgeFrames,7} frame Unity");
                _text.AppendLine($"  di cui Unreal  {tracker.UnrealSubmitMs,7:F2} ms fino alla submit");
                _text.AppendLine($"  frame_id       {tracker.LastFrameId}");
            }
            else
            {
                _text.AppendLine("  in attesa del primo marker valido...");
            }

            if (!tracker.MarkerValid && tracker.MarkerReads > 0)
            {
                _text.AppendLine("  <color=#ffd166>marker non valido: magic/checksum non tornano</color>");
                _text.AppendLine("  <color=#ffd166>(i bit dei pixel vengono alterati lungo la catena)</color>");
            }

            // --- Frequenze ------------------------------------------------------
            _text.AppendLine();
            _text.AppendLine("<b>FREQUENZE</b>");
            _text.AppendLine($"  Unity          {tracker.UnityFps,7:F1} fps");
            _text.AppendLine($"  Unreal         {tracker.UnrealFps,7:F1} fps");

            // --- Frame ----------------------------------------------------------
            _text.AppendLine();
            _text.AppendLine("<b>FRAME</b>");
            _text.AppendLine($"  nuovi          {tracker.NewFrames,7}");
            _text.AppendLine($"  ripresentati   {tracker.RepeatedFrames,7}  su {tracker.FramesRunning} ({tracker.RepeatRatio * 100.0:F1}%)");
            _text.AppendLine($"  buffer drenati {tracker.StaleDrained,7}  (Unity piu' lento di Unreal)");
            if (tracker.ConsumeFailures > 0)
            {
                _text.AppendLine($"  <color=#ffd166>consume falliti {tracker.ConsumeFailures,6}  (dovrebbe restare a 0)</color>");
            }
            _text.AppendLine($"  ritardo lettura{tracker.ReadbackLagEvents,7} eventi (non entra nella misura)");

            // --- Rete -----------------------------------------------------------
            _text.AppendLine();
            _text.AppendLine("<b>CANALE DI CONTROLLO</b>");
            if (channel != null)
            {
                _text.AppendLine($"  pose inviate   {channel.PosePacketsSent,7}");
                _text.AppendLine($"  status ricevuti{channel.StatusPacketsReceived,7}");
                _text.AppendLine($"  handshake      {channel.HandshakesReceived,7}");
                _text.AppendLine($"  scartati       {channel.BadPacketsReceived,7}");
            }

            // --- Pose spedita ---------------------------------------------------
            // Risponde alla domanda "da che lato sta il problema?". Se questi
            // numeri cambiano ma l'immagine e' nera, il problema e' a valle
            // (Unreal, o il trasporto). Se NON cambiano, e' qui.
            var driver = _client.Driver;
            if (driver != null && !_client.IsVr)
            {
                _text.AppendLine();
                _text.AppendLine("<b>POSE SPEDITA</b>  (spazio Unity, relativa all'origine)");
                Vector3 euler = driver.Rotation.eulerAngles;
                _text.AppendLine($"  modo           {driver.Mode}");
                _text.AppendLine($"  posizione      ({driver.Position.x,7:F2},{driver.Position.y,7:F2},{driver.Position.z,7:F2}) m");
                _text.AppendLine($"  rotazione      ({euler.x,7:F1},{euler.y,7:F1},{euler.z,7:F1}) gradi");
                _text.AppendLine($"  fov verticale  {driver.FovYDeg,7:F1} gradi   near {driver.NearM:F2} m");
                if (driver.OriginTransform != null)
                {
                    _text.AppendLine($"  origine        {driver.OriginTransform.name} (pose relativa a questo)");
                }
            }

            // --- Texture --------------------------------------------------------
            _text.AppendLine();
            _text.AppendLine($"<b>VISTA</b>  (0=colore 1=depth 2=marker)  attuale: {_client.DebugMode}");
            if (_client.ColorTexture != null)
            {
                _text.AppendLine($"  colore  {_client.ColorTexture.width}x{_client.ColorTexture.height}" +
                                 (_client.ColorTextureRight != null ? "  x2 (occhio sinistro + destro)" : ""));
            }
            if (_client.DepthTexture != null)
            {
                float depthScale = (channel != null && channel.HasStatus) ? channel.LastStatus.DepthScaleToMeters : 0.01f;
                _text.AppendLine($"  depth   {_client.DepthTexture.width}x{_client.DepthTexture.height}  (lineare; x{depthScale} = metri)");
            }
            if (_client.CubeAtlasTexture != null)
            {
                _text.AppendLine($"  cube    atlas {_client.CubeAtlasTexture.width}x{_client.CubeAtlasTexture.height}");
            }

            Draw(600.0f);
        }

        private void Draw(float width)
        {
            const float margin = 10.0f;
            float height = _labelStyle.CalcHeight(new GUIContent(_text.ToString()), width - 20.0f) + 16.0f;

            GUI.Box(new Rect(margin, margin, width, height), GUIContent.none, _boxStyle);
            GUI.Label(new Rect(margin + 10.0f, margin + 8.0f, width - 20.0f, height), _text.ToString(), _labelStyle);
        }

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = FontSize,
                richText = true,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft,
                font = Font.CreateDynamicFontFromOSFont("Consolas", FontSize),
            };
            _labelStyle.normal.textColor = new Color(0.92f, 0.94f, 0.96f);

            var background = new Texture2D(1, 1);
            background.SetPixel(0, 0, new Color(0.0f, 0.0f, 0.0f, 0.72f));
            background.Apply();

            _boxStyle = new GUIStyle(GUI.skin.box);
            _boxStyle.normal.background = background;
        }
    }
}
