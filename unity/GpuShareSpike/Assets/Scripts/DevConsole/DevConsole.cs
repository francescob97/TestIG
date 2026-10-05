// ============================================================================
//  DevConsole  --  console in gioco stile Unreal.
//
//  Si apre con  \  (il tasto a sinistra dell'1 sulla tastiera italiana),
//  con  `  o  ~  su tastiera US, oppure con F2. Si scrive un comando, Invio.
//
//    help                          elenca i comandi
//    find porta                    cerca GameObject per nome (anche inattivi)
//    disable Enemy                 spegne un GameObject
//    disable Player PlayerMove     disabilita un componente
//    call Player Jump 5            chiama un metodo (anche privato)
//    send Door Open                come GameObject.SendMessage
//    set "Main Camera" Camera fieldOfView 40
//
//  Tab completa (comandi, nomi di GameObject, componenti, metodi, campi),
//  frecce su/giu' scorrono la cronologia, PagSu/PagGiu' o la rotella il log.
//
//  ZERO SETUP: nell'editor e nei Development Build la console si crea da sola
//  al primo caricamento di scena (definisci DEVCONSOLE_NO_AUTOCREATE per
//  impedirlo). In un build di release non c'e', come in Shipping di Unreal,
//  a meno che non aggiungi tu il componente a un GameObject.
//
//  IMGUI di proposito, come HudOverlay: niente Canvas, niente prefab, e
//  funziona sia con il vecchio Input Manager sia con il nuovo Input System,
//  perche' la tastiera arriva dagli eventi di OnGUI e non dalla classe Input.
//
//  Il vecchio Input Manager pero' continua a vedere i tasti anche mentre
//  scrivi: gli script di gioco che non devono reagire controllano
//  DevConsole.IsOpen (vedi VirtualCameraDriver).
//
//  Comandi propri: [ConsoleCommand] su un metodo, oppure DevConsole.Register.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DevTools
{
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed partial class DevConsole : MonoBehaviour
    {
        [Header("Apertura")]
        [Tooltip("Tasti che aprono e chiudono la console. Backslash e' il tasto a sinistra dell'1 sulla tastiera italiana.")]
        public KeyCode[] ToggleKeys = { KeyCode.Backslash, KeyCode.F2 };

        [Tooltip("Caratteri che aprono e chiudono la console, qualunque tasto li produca. Copre i layout in cui il tasto non ha un KeyCode (` e ~ su tastiera US).")]
        public string ToggleCharacters = "\\`~";

        [Tooltip("Chiude la console dopo ogni comando, come la riga singola di Unreal.")]
        public bool CloseOnSubmit = false;

        [Tooltip("Sblocca e mostra il cursore mentre la console e' aperta, e lo rimette com'era alla chiusura.")]
        public bool ReleaseCursor = true;

        [Tooltip("La console sopravvive ai cambi di scena (DontDestroyOnLoad). Vale solo se e' su un GameObject radice.")]
        public bool PersistAcrossScenes = true;

        [Header("Aspetto")]
        [Range(0.15f, 1.0f)] public float HeightFraction = 0.4f;
        [Range(8, 40)] public int FontSize = 14;
        [Tooltip("Scala dell'intera console, per schermi ad alta densita'.")]
        [Range(0.5f, 3.0f)] public float UiScale = 1.0f;
        public Color BackgroundColor = new Color(0.04f, 0.05f, 0.07f, 0.92f);

        [Header("Log")]
        [Tooltip("Mostra in console anche i messaggi di Debug.Log, i warning e gli errori di Unity.")]
        public bool MirrorUnityLog = true;
        [Tooltip("Per errori ed eccezioni di Unity mostra anche lo stack trace.")]
        public bool ShowStackTraces = false;

        [Header("Avvio")]
        [Tooltip("Comandi eseguiti all'avvio, uno per riga. Le righe che iniziano con // sono ignorate.")]
        [TextArea(2, 6)] public string StartupCommands = "";

        /// <summary>La console attiva, se c'e'.</summary>
        public static DevConsole Instance { get; private set; }

        /// <summary>True mentre la console e' aperta: la tastiera serve a scrivere, non a giocare.</summary>
        public static bool IsOpen => Instance != null && Instance._open;

        internal static Scene PersistentScene => Instance != null ? Instance.gameObject.scene : default;

        // --- colori ---------------------------------------------------------
        private static readonly Color TextColor = new Color(0.90f, 0.92f, 0.95f);
        private static readonly Color EchoColor = new Color(0.55f, 0.78f, 1.00f);
        private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.63f);
        private static readonly Color WarningColor = new Color(1.00f, 0.82f, 0.40f);
        private static readonly Color ErrorColor = new Color(1.00f, 0.45f, 0.45f);
        private static readonly Color UnityLogColor = new Color(0.72f, 0.74f, 0.78f);
        private static readonly Color AccentColor = new Color(0.45f, 0.80f, 1.00f);
        private static readonly Color InputRowColor = new Color(1.00f, 1.00f, 1.00f, 0.06f);
        private static readonly Color PopupColor = new Color(0.08f, 0.09f, 0.12f, 0.97f);
        private static readonly Color HighlightColor = new Color(0.45f, 0.80f, 1.00f, 0.22f);

        // --- righe del log (statiche: si scrive anche senza console) -------
        private struct Line
        {
            public string Text;
            public Color Color;
        }

        private const int MaxLines = 2000;
        private static readonly List<Line> s_lines = new List<Line>(MaxLines);
        private static readonly object s_linesLock = new object();   // i log di Unity arrivano anche da altri thread
        private static long s_linesAdded;
        [ThreadStatic] private static bool s_suppressMirror;

        // --- stato della finestra ------------------------------------------
        private const string InputControlName = "DevConsoleInput";
        private const int MaxPopupRows = 12;

        private bool _open;
        private string _input = "";
        private int _scroll;                // righe dal fondo; 0 = segue l'ultima
        private long _seenLines;
        private int _visibleLines = 10;
        private int _swallowCharFrame = -1;
        private bool _moveCursorToEnd;
        private readonly List<string> _queued = new List<string>();

        private bool _cursorSaved;
        private CursorLockMode _savedLockState;
        private bool _savedCursorVisible;

        // --- cronologia -----------------------------------------------------
        private const string HistoryPrefsKey = "DevTools.DevConsole.History";
        private const int MaxHistory = 100;
        private readonly List<string> _history = new List<string>();   // la piu' recente in fondo
        private int _historyIndex = -1;                                  // -1 = riga nuova
        private string _draft = "";

        // --- suggerimenti e completamento ----------------------------------
        private readonly List<KeyValuePair<string, string>> _suggestions = new List<KeyValuePair<string, string>>();
        private readonly List<KeyValuePair<string, string>> _hintRow = new List<KeyValuePair<string, string>>(1);
        private List<string> _cycle;        // candidati del giro di Tab in corso
        private List<KeyValuePair<string, string>> _cycleRows;
        private int _cycleIndex;
        private string _cyclePrefix;
        private string _cycleResult;        // input dopo l'ultimo Tab: se e' ancora quello, il Tab continua il giro

        // --- stili ----------------------------------------------------------
        private static readonly string[] MonoFonts = { "Consolas", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Courier New" };
        private GUIStyle _lineStyle;
        private GUIStyle _inputStyle;
        private int _styleFontSize;
        private float _lineHeight;
        private float _charWidth;
        private float _promptWidth;

        // =====================================================================
        //  Log
        // =====================================================================

        public static void Log(string message) => AddLine(message, TextColor);
        public static void Log(string message, Color color) => AddLine(message, color);
        public static void LogWarning(string message) => AddLine(message, WarningColor);
        public static void LogError(string message) => AddLine(message, ErrorColor);

        public static void Clear()
        {
            lock (s_linesLock) s_lines.Clear();
        }

        private static void AddLine(string text, Color color)
        {
            text = (text ?? "null").TrimEnd('\r', '\n').Replace("\t", "    ");

            lock (s_linesLock)
            {
                int start = 0;
                for (int i = 0; i <= text.Length; i++)
                {
                    if (i < text.Length && text[i] != '\n') continue;
                    int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                    s_lines.Add(new Line { Text = text.Substring(start, end - start), Color = color });
                    s_linesAdded++;
                    start = i + 1;
                }

                if (s_lines.Count > MaxLines + 200) s_lines.RemoveRange(0, s_lines.Count - MaxLines);
            }
        }

        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (s_suppressMirror || !MirrorUnityLog) return;

            Color color = type == LogType.Log ? UnityLogColor : type == LogType.Warning ? WarningColor : ErrorColor;
            string text = condition;
            if (ShowStackTraces && type != LogType.Log && type != LogType.Warning && !string.IsNullOrEmpty(stackTrace))
            {
                text += "\n" + stackTrace.TrimEnd();
            }
            AddLine(text, color);
        }

        // =====================================================================
        //  Ciclo di vita
        // =====================================================================

#if !DEVCONSOLE_NO_AUTOCREATE
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            // Debug.isDebugBuild e' true nell'editor e nei Development Build.
            if (Instance != null || !Debug.isDebugBuild) return;
            new GameObject("[DevConsole]").AddComponent<DevConsole>();
        }
#endif

        // Con "Enter Play Mode Options" senza domain reload gli statici
        // sopravvivono tra un Play e l'altro: si riparte puliti.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_commands.Clear();
            s_initialized = false;
            Clear();
            Instance = null;
        }

        public static void Open() { if (Instance != null) Instance.SetOpen(true); }
        public static void Close() { if (Instance != null) Instance.SetOpen(false); }
        public static void Toggle() { if (Instance != null) Instance.SetOpen(!Instance._open); }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning($"[DevConsole] ce n'e' gia' una su '{Instance.name}': quella su '{name}' viene rimossa.");
                Destroy(this);
                return;
            }

            Instance = this;
            useGUILayout = false;   // solo GUI.*, niente GUILayout: si risparmia l'evento Layout

            if (PersistAcrossScenes && transform.parent == null)
            {
                DontDestroyOnLoad(gameObject);
            }

            LoadHistory();
            EnsureInitialized();
        }

        private void OnEnable()
        {
            if (Instance != this) return;
            Application.logMessageReceivedThreaded += OnUnityLog;
        }

        private void OnDisable()
        {
            Application.logMessageReceivedThreaded -= OnUnityLog;
            SetOpen(false);
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            SaveHistory();
            try { PlayerPrefs.Save(); }
            catch (Exception) { }
            Instance = null;
        }

        private void Start()
        {
            if (string.IsNullOrWhiteSpace(StartupCommands)) return;

            foreach (string raw in StartupCommands.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal)) Execute(line);
            }
        }

        private void Update()
        {
            // I comandi scritti girano qui e non dentro OnGUI: cosi' vedono il
            // frame come lo vede il resto del gioco.
            if (_queued.Count == 0) return;

            string[] batch = _queued.ToArray();
            _queued.Clear();
            foreach (string line in batch) Execute(line);
        }

        private void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;

            if (open)
            {
                _scroll = 0;
                _moveCursorToEnd = true;
                RefreshSuggestions();

                if (ReleaseCursor)
                {
                    _savedLockState = Cursor.lockState;
                    _savedCursorVisible = Cursor.visible;
                    _cursorSaved = true;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            else if (_cursorSaved)
            {
                Cursor.lockState = _savedLockState;
                Cursor.visible = _savedCursorVisible;
                _cursorSaved = false;
            }
        }

        // =====================================================================
        //  Tastiera
        // =====================================================================

        private void HandleKeyDown(Event e)
        {
            char ch = e.character;

            // Su Windows ogni tasto arriva come DUE eventi: prima quello con il
            // keyCode, poi quello con il carattere, che e' quello che il
            // TextField usa per scrivere. Il carattere di un tasto che abbiamo
            // gia' gestito (apertura, Invio, Tab) va mangiato, se no finisce
            // nella riga o richiude subito la console.
            if (e.keyCode == KeyCode.None)
            {
                if (ch == '\0') return;

                if (_swallowCharFrame == Time.frameCount)
                {
                    _swallowCharFrame = -1;
                    e.Use();
                    return;
                }
                if (IsToggleCharacter(ch))
                {
                    SetOpen(!_open);
                    e.Use();
                    return;
                }
                if (_open && (ch == '\t' || ch == '\n' || ch == '\r')) e.Use();
                return;
            }

            _swallowCharFrame = -1;

            if (IsToggleKey(e.keyCode) || IsToggleCharacter(ch))
            {
                SetOpen(!_open);
                SwallowFollowingCharacter(e);
                e.Use();
                return;
            }

            if (!_open) return;

            switch (e.keyCode)
            {
                case KeyCode.Escape:
                    SetOpen(false);
                    e.Use();
                    break;

                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit();
                    SwallowFollowingCharacter(e);
                    e.Use();
                    break;

                case KeyCode.Tab:
                    Complete(e.shift ? -1 : 1);
                    SwallowFollowingCharacter(e);
                    e.Use();
                    break;

                case KeyCode.UpArrow:
                    HistoryStep(+1);
                    e.Use();
                    break;

                case KeyCode.DownArrow:
                    HistoryStep(-1);
                    e.Use();
                    break;

                case KeyCode.PageUp:
                    _scroll += Mathf.Max(1, _visibleLines - 1);
                    e.Use();
                    break;

                case KeyCode.PageDown:
                    _scroll = Mathf.Max(0, _scroll - Mathf.Max(1, _visibleLines - 1));
                    e.Use();
                    break;
            }
        }

        private void SwallowFollowingCharacter(Event e)
        {
            // Solo se il carattere arriva a parte (Windows); limitato a questo
            // frame, cosi' un tasto senza carattere (F2) non mangia la lettera dopo.
            if (e.character == '\0') _swallowCharFrame = Time.frameCount;
        }

        private bool IsToggleKey(KeyCode key)
        {
            if (ToggleKeys == null) return false;
            foreach (KeyCode k in ToggleKeys)
            {
                if (k == key && k != KeyCode.None) return true;
            }
            return false;
        }

        private bool IsToggleCharacter(char ch)
        {
            return ch != '\0' && !string.IsNullOrEmpty(ToggleCharacters) && ToggleCharacters.IndexOf(ch) >= 0;
        }

        private void Submit()
        {
            string line = _input.Trim();
            _input = "";
            _cycle = null;
            _historyIndex = -1;
            _scroll = 0;
            RefreshSuggestions();

            if (line.Length == 0) return;

            AddToHistory(line);
            _queued.Add(line);
            if (CloseOnSubmit) SetOpen(false);
        }

        // =====================================================================
        //  Cronologia
        // =====================================================================

        /// <param name="direction">+1 verso i comandi piu' vecchi, -1 verso i piu' recenti.</param>
        private void HistoryStep(int direction)
        {
            if (_history.Count == 0) return;
            if (_historyIndex < 0)
            {
                if (direction < 0) return;
                _draft = _input;
            }

            _historyIndex = Mathf.Clamp(_historyIndex + direction, -1, _history.Count - 1);
            _input = _historyIndex < 0 ? _draft : _history[_history.Count - 1 - _historyIndex];
            _moveCursorToEnd = true;
            _cycle = null;
            RefreshSuggestions();
        }

        private void AddToHistory(string line)
        {
            _history.Remove(line);
            _history.Add(line);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
            SaveHistory();
        }

        private void LoadHistory()
        {
            try
            {
                _history.Clear();
                foreach (string line in PlayerPrefs.GetString(HistoryPrefsKey, "").Split('\n'))
                {
                    if (line.Length > 0) _history.Add(line);
                }
            }
            catch (Exception)
            {
                // Cronologia illeggibile: si riparte vuoti, non e' un problema.
            }
        }

        private void SaveHistory()
        {
            try { PlayerPrefs.SetString(HistoryPrefsKey, string.Join("\n", _history)); }
            catch (Exception) { }
        }

        // =====================================================================
        //  Suggerimenti e Tab
        // =====================================================================

        private void OnInputEdited()
        {
            _cycle = null;
            RefreshSuggestions();
        }

        /// <summary>
        /// Mentre scrivi il nome: i comandi che corrispondono. Dopo il nome:
        /// la riga d'uso del comando, come i suggerimenti della console di Unreal.
        /// </summary>
        private void RefreshSuggestions()
        {
            _suggestions.Clear();
            _hintRow.Clear();

            string statement = _input.Substring(LastStatementStart(_input));
            List<Token> tokens = Tokenize(statement, out bool openQuote);
            if (tokens.Count == 0) return;

            bool typingName = tokens.Count == 1 && !openQuote && !char.IsWhiteSpace(statement[statement.Length - 1]);
            if (typingName)
            {
                foreach (string name in MatchNames(CommandNames(), tokens[0].Text))
                {
                    Command cmd = s_commands[name];
                    string label = string.Equals(name, cmd.Name, StringComparison.OrdinalIgnoreCase) ? cmd.Usage : $"{name}  (= {cmd.Name})";
                    _suggestions.Add(new KeyValuePair<string, string>(label, cmd.Help));
                    if (_suggestions.Count >= 30) break;
                }
                return;
            }

            if (s_commands.TryGetValue(tokens[0].Text, out Command current))
            {
                _hintRow.Add(new KeyValuePair<string, string>(current.Usage, current.Help));
            }
        }

        private void Complete(int direction)
        {
            if (_cycle != null && _input == _cycleResult)
            {
                _cycleIndex = (_cycleIndex + direction + _cycle.Count) % _cycle.Count;
            }
            else
            {
                List<string> candidates = GetCompletions(_input, out string prefix, out bool completingCommand);
                if (candidates.Count == 0)
                {
                    _cycle = null;
                    return;
                }

                _cycle = candidates;
                _cyclePrefix = prefix;
                _cycleIndex = direction > 0 ? 0 : candidates.Count - 1;
                _cycleRows = candidates.Select(c => new KeyValuePair<string, string>(
                    c, completingCommand && s_commands.TryGetValue(c, out Command cmd) ? cmd.Help : "")).ToList();
            }

            _input = _cyclePrefix + QuoteIfNeeded(_cycle[_cycleIndex]) + " ";
            _moveCursorToEnd = true;
            _suggestions.Clear();
            _hintRow.Clear();

            if (_cycle.Count == 1)
            {
                // Unico candidato: il prossimo Tab passa all'argomento successivo.
                _cycle = null;
                RefreshSuggestions();
            }
            else
            {
                _cycleResult = _input;
            }
        }

        // =====================================================================
        //  Disegno
        // =====================================================================

        private void OnGUI()
        {
            GUI.depth = -1000;   // sopra a tutto il resto, HUD compreso

            Event e = Event.current;
            if (e.type == EventType.KeyDown) HandleKeyDown(e);
            if (!_open) return;

            EnsureStyles();

            float scale = Mathf.Clamp(UiScale, 0.5f, 3.0f);
            Matrix4x4 previous = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));
            try
            {
                DrawConsole(e, Screen.width / scale, Screen.height / scale);
            }
            finally
            {
                GUI.matrix = previous;
            }
        }

        private void DrawConsole(Event e, float width, float height)
        {
            const float pad = 6.0f;
            float lineH = _lineHeight;
            float panelH = Mathf.Clamp(Mathf.Round(height * HeightFraction), lineH * 4.0f + pad * 4.0f, height);

            var panel = new Rect(0.0f, 0.0f, width, panelH);
            var inputRow = new Rect(0.0f, panelH - lineH - pad * 2.0f, width, lineH + pad * 2.0f);
            var logArea = new Rect(pad, pad, width - pad * 2.0f, inputRow.y - pad * 2.0f);
            _visibleLines = Mathf.Max(1, Mathf.FloorToInt(logArea.height / lineH));

            if (e.type == EventType.ScrollWheel && panel.Contains(e.mousePosition))
            {
                _scroll = Mathf.Max(0, _scroll - (e.delta.y > 0.0f ? 3 : -3));
                e.Use();
            }

            if (e.type == EventType.Repaint)
            {
                DrawRect(panel, BackgroundColor);
                DrawRect(inputRow, InputRowColor);
                DrawRect(new Rect(0.0f, panelH, width, 1.0f), AccentColor);
                DrawLog(logArea, lineH);

                _lineStyle.normal.textColor = AccentColor;
                GUI.Label(new Rect(pad, inputRow.y + pad, _promptWidth, lineH), ">", _lineStyle);
            }

            var fieldRect = new Rect(pad + _promptWidth, inputRow.y + pad, width - pad * 2.0f - _promptWidth, lineH);
            GUI.SetNextControlName(InputControlName);
            string edited = GUI.TextField(fieldRect, _input, _inputStyle);
            if (edited != _input)
            {
                _input = edited;
                OnInputEdited();
            }

            // La riga di input tiene sempre il fuoco: si apre e si scrive.
            // Riprendendo il fuoco IMGUI seleziona tutto il testo: si rimette
            // il cursore in fondo, se no il primo tasto dopo un clic cancella la riga.
            if (GUI.GetNameOfFocusedControl() != InputControlName)
            {
                GUI.FocusControl(InputControlName);
                _moveCursorToEnd = true;
            }
            else if (_moveCursorToEnd)
            {
                var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
                editor.MoveTextEnd();
                _moveCursorToEnd = false;
            }

            if (e.type == EventType.Repaint)
            {
                DrawPopup(fieldRect.x, panelH + 2.0f, width - fieldRect.x - pad, lineH);
            }
        }

        private void DrawLog(Rect area, float lineH)
        {
            lock (s_linesLock)
            {
                int total = s_lines.Count;

                // Se stai leggendo piu' in alto, le righe nuove non ti spostano il testo.
                if (_scroll > 0) _scroll += (int)(s_linesAdded - _seenLines);
                _seenLines = s_linesAdded;
                _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, total - _visibleLines));

                int first = Mathf.Max(0, total - _visibleLines - _scroll);
                int last = Mathf.Min(total, first + _visibleLines);

                // Le righe si appoggiano al fondo, vicino a dove scrivi.
                float y = area.yMax - (last - first) * lineH;
                for (int i = first; i < last; i++, y += lineH)
                {
                    _lineStyle.normal.textColor = s_lines[i].Color;
                    GUI.Label(new Rect(area.x, y, area.width, lineH), s_lines[i].Text, _lineStyle);
                }
            }

            if (_scroll > 0)
            {
                string marker = $"-- {_scroll} righe sotto (PagGiu') --";
                float w = (marker.Length + 2) * _charWidth;
                var r = new Rect(area.xMax - w, area.y, w, lineH);
                DrawRect(r, PopupColor);
                _lineStyle.normal.textColor = AccentColor;
                GUI.Label(r, marker, _lineStyle);
            }
        }

        private void DrawPopup(float x, float y, float maxWidth, float lineH)
        {
            List<KeyValuePair<string, string>> rows;
            int highlight = -1;

            if (_cycle != null && _input == _cycleResult)
            {
                rows = _cycleRows;
                highlight = _cycleIndex;
            }
            else if (_suggestions.Count > 0)
            {
                rows = _suggestions;
            }
            else if (_hintRow.Count > 0)
            {
                rows = _hintRow;
            }
            else
            {
                return;
            }

            const float pad = 6.0f;
            int count = rows.Count;
            int shown = Mathf.Min(count, MaxPopupRows);
            int first = highlight >= shown ? highlight - shown + 1 : 0;

            int lenKey = 0;
            int lenValue = 0;
            for (int i = first; i < first + shown; i++)
            {
                lenKey = Mathf.Max(lenKey, rows[i].Key.Length);
                lenValue = Mathf.Max(lenValue, rows[i].Value.Length);
            }

            float gap = 3.0f * _charWidth;
            float keyW = Mathf.Min(lenKey + 1, 60) * _charWidth;
            float valueW = lenValue > 0 ? Mathf.Min(lenValue + 1, 90) * _charWidth : 0.0f;
            bool more = count > shown;
            float w = Mathf.Min(maxWidth, Mathf.Max(keyW + (valueW > 0.0f ? gap + valueW : 0.0f), more ? 30.0f * _charWidth : 0.0f) + pad * 2.0f);
            float h = (shown + (more ? 1 : 0)) * lineH + pad * 2.0f;

            DrawRect(new Rect(x, y, w, h), PopupColor);

            float rowY = y + pad;
            for (int i = first; i < first + shown; i++, rowY += lineH)
            {
                if (i == highlight) DrawRect(new Rect(x, rowY, w, lineH), HighlightColor);

                _lineStyle.normal.textColor = AccentColor;
                GUI.Label(new Rect(x + pad, rowY, Mathf.Min(keyW, w - pad * 2.0f), lineH), rows[i].Key, _lineStyle);

                if (valueW > 0.0f && rows[i].Value.Length > 0)
                {
                    float vx = x + pad + keyW + gap;
                    _lineStyle.normal.textColor = DimColor;
                    GUI.Label(new Rect(vx, rowY, Mathf.Max(0.0f, x + w - pad - vx), lineH), rows[i].Value, _lineStyle);
                }
            }

            if (more)
            {
                _lineStyle.normal.textColor = DimColor;
                GUI.Label(new Rect(x + pad, rowY, w - pad * 2.0f, lineH), $"... altri {count - shown} (Tab per scorrere)", _lineStyle);
            }
        }

        private void EnsureStyles()
        {
            if (_lineStyle != null && _styleFontSize == FontSize) return;
            _styleFontSize = FontSize;

            Font font = Font.CreateDynamicFontFromOSFont(MonoFonts, FontSize);

            _lineStyle = new GUIStyle(GUI.skin.label)
            {
                font = font,
                fontSize = FontSize,
                richText = false,   // il testo dei log non deve essere interpretato come markup
                wordWrap = false,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                font = font,
                fontSize = FontSize,
                richText = false,
                wordWrap = false,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            foreach (GUIStyleState state in new[] { _inputStyle.normal, _inputStyle.hover, _inputStyle.focused, _inputStyle.active })
            {
                state.background = null;
                state.textColor = Color.white;
            }

            _lineHeight = Mathf.Ceil(Mathf.Max(_lineStyle.lineHeight, FontSize * 1.2f)) + 2.0f;
            _charWidth = _lineStyle.CalcSize(new GUIContent("M")).x;
            if (_charWidth <= 0.0f) _charWidth = FontSize * 0.6f;
            _promptWidth = _charWidth * 2.0f;
        }

        private static void DrawRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
