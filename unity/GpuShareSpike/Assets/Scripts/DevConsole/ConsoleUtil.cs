// ============================================================================
//  ConsoleUtil  --  il lavoro sporco dei comandi: trovare GameObject e
//  componenti per nome (anche inattivi), convertire testo in valori tipizzati,
//  e chiamare metodi / leggere e scrivere campi via reflection.
//
//  Pubblico di proposito: serve anche a chi scrive comandi propri.
//
//  NOTA SUI BUILD IL2CPP: con il Managed Stripping Level alto, il linker puo'
//  togliere metodi e campi usati SOLO via reflection. Nell'editor e in un build
//  Mono non succede; in IL2CPP marca con [UnityEngine.Scripting.Preserve] cio'
//  che vuoi raggiungere da console.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DevTools
{
    public static class ConsoleUtil
    {
        private const BindingFlags AnyDeclared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Getter che creano copie (material, mesh...) e che quindi un elenco
        // automatico non deve toccare: leggerli "per guardare" sporca la scena.
        private static readonly HashSet<string> s_unsafeGetters = new HashSet<string>
        {
            "material", "materials", "mesh", "fontMaterial", "fontMaterials",
        };

        // =====================================================================
        //  Scena
        // =====================================================================

        /// <summary>Radici di tutte le scene caricate, DontDestroyOnLoad compresa.</summary>
        public static List<GameObject> RootObjects()
        {
            var roots = new List<GameObject>();
            var seen = new HashSet<int>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && seen.Add(scene.handle))
                {
                    roots.AddRange(scene.GetRootGameObjects());
                }
            }

            // La scena DontDestroyOnLoad non compare in SceneManager: ci si
            // arriva solo da un oggetto che ci vive, e la console e' uno di quelli.
            Scene persistent = DevConsole.PersistentScene;
            if (persistent.IsValid() && seen.Add(persistent.handle))
            {
                roots.AddRange(persistent.GetRootGameObjects());
            }

            return roots;
        }

        /// <summary>Tutti i GameObject delle scene caricate, inattivi compresi, in ordine di gerarchia.</summary>
        public static IEnumerable<GameObject> AllGameObjects()
        {
            var stack = new Stack<Transform>();
            List<GameObject> roots = RootObjects();
            for (int i = roots.Count - 1; i >= 0; i--)
            {
                stack.Push(roots[i].transform);
            }

            while (stack.Count > 0)
            {
                Transform t = stack.Pop();
                if (t == null) continue;   // distrutto mentre iteravamo
                yield return t.gameObject;
                for (int c = t.childCount - 1; c >= 0; c--)
                {
                    stack.Push(t.GetChild(c));
                }
            }
        }

        public static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                sb.Insert(0, '/').Insert(0, p.name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Trova un GameObject, anche inattivo. Accetta un nome ("Player") o un
        /// percorso nella gerarchia ("Level/Doors/Door 2"). Con piu' oggetti
        /// dello stesso nome usa il primo e lo dice.
        /// </summary>
        public static GameObject FindGameObject(string query)
        {
            if (TryFindGameObject(query, out GameObject go, out string error, out int matches))
            {
                if (matches > 1)
                {
                    DevConsole.LogWarning($"{matches} GameObject si chiamano '{query}': uso {PathOf(go.transform)}. "
                                        + "Per un altro usa il percorso completo (find " + query + ").");
                }
                return go;
            }
            throw new ConsoleException(error);
        }

        public static bool TryFindGameObject(string query, out GameObject result)
        {
            return TryFindGameObject(query, out result, out _, out _);
        }

        private static bool TryFindGameObject(string query, out GameObject result, out string error, out int matches)
        {
            result = null;
            matches = 0;
            error = null;

            string q = query?.Trim();
            if (string.IsNullOrEmpty(q))
            {
                error = "manca il nome del GameObject";
                return false;
            }

            // Percorso: la prima parte e' una radice, il resto lo risolve
            // Transform.Find, che a differenza di GameObject.Find trova anche
            // gli oggetti inattivi.
            if (q.IndexOf('/') >= 0)
            {
                string path = q.TrimStart('/');
                int slash = path.IndexOf('/');
                string rootName = slash < 0 ? path : path.Substring(0, slash);
                string rest = slash < 0 ? null : path.Substring(slash + 1);

                foreach (GameObject root in RootObjects())
                {
                    if (root.name != rootName) continue;
                    Transform found = string.IsNullOrEmpty(rest) ? root.transform : root.transform.Find(rest);
                    if (found != null)
                    {
                        result = found.gameObject;
                        matches = 1;
                        return true;
                    }
                }
                // Non e' un percorso valido: magari e' un nome che contiene '/'.
            }

            GameObject exact = null;
            GameObject ignoreCase = null;
            int exactCount = 0;
            int ignoreCaseCount = 0;
            foreach (GameObject go in AllGameObjects())
            {
                if (go.name == q)
                {
                    if (exact == null) exact = go;
                    exactCount++;
                }
                else if (string.Equals(go.name, q, StringComparison.OrdinalIgnoreCase))
                {
                    if (ignoreCase == null) ignoreCase = go;
                    ignoreCaseCount++;
                }
            }

            result = exact != null ? exact : ignoreCase;
            matches = exact != null ? exactCount : ignoreCaseCount;
            if (result == null)
            {
                error = $"nessun GameObject chiamato '{q}' (prova: find {q})";
                return false;
            }
            return true;
        }

        /// <summary>Componenti di tipo <paramref name="type"/> su tutti i GameObject attivi.</summary>
        public static List<Component> FindActiveComponents(Type type)
        {
            var result = new List<Component>();
            foreach (GameObject go in AllGameObjects())
            {
                if (go.activeInHierarchy)
                {
                    result.AddRange(go.GetComponents(type));
                }
            }
            return result;
        }

        // =====================================================================
        //  Componenti
        // =====================================================================

        /// <summary>
        /// Trova un componente per nome di tipo: "Light", "BoxCollider", anche
        /// un tipo base ("Collider", "Renderer"). Nessun errore se manca.
        /// </summary>
        public static bool TryFindComponent(GameObject go, string typeName, out Component component)
        {
            component = null;
            if (go == null || string.IsNullOrEmpty(typeName)) return false;

            Component[] components = go.GetComponents<Component>();

            foreach (Component c in components)
            {
                if (c != null && c.GetType().Name == typeName) { component = c; return true; }
            }
            foreach (Component c in components)
            {
                if (c == null) continue;   // script mancante
                Type t = c.GetType();
                if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    component = c;
                    return true;
                }
            }
            foreach (Component c in components)
            {
                if (c == null) continue;
                for (Type t = c.GetType().BaseType; t != null && t != typeof(Component); t = t.BaseType)
                {
                    if (string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        component = c;
                        return true;
                    }
                }
            }
            return false;
        }

        public static Component FindComponent(GameObject go, string typeName)
        {
            if (TryFindComponent(go, typeName, out Component c)) return c;
            throw new ConsoleException($"'{go.name}' non ha un componente '{typeName}'. Ha: {string.Join(", ", ComponentNames(go))}");
        }

        /// <summary>
        /// Come FindComponent, ma "GameObject" indica l'oggetto stesso: cosi'
        /// get/set/call raggiungono anche layer, tag, SetActive...
        /// </summary>
        public static object ResolveTarget(GameObject go, string typeName)
        {
            if (string.Equals(typeName, "GameObject", StringComparison.OrdinalIgnoreCase)) return go;
            return FindComponent(go, typeName);
        }

        public static List<string> ComponentNames(GameObject go)
        {
            var names = new List<string>();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c != null && !names.Contains(c.GetType().Name)) names.Add(c.GetType().Name);
            }
            return names;
        }

        /// <summary>Abilita/disabilita un componente. null = inverte. Restituisce il nuovo stato.</summary>
        public static bool SetEnabled(Component c, bool? value)
        {
            switch (c)
            {
                case Behaviour b:
                    b.enabled = value ?? !b.enabled;
                    return b.enabled;
                case Renderer r:
                    r.enabled = value ?? !r.enabled;
                    return r.enabled;
                case Collider col:
                    col.enabled = value ?? !col.enabled;
                    return col.enabled;
            }

            // Cloth, LODGroup e simili: hanno "enabled" ma nessuna base comune.
            PropertyInfo p = c.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.PropertyType == typeof(bool) && p.CanRead && p.CanWrite)
            {
                bool next = value ?? !(bool)p.GetValue(c);
                p.SetValue(c, next);
                return next;
            }

            throw new ConsoleException($"{c.GetType().Name} non si abilita/disabilita (non ha 'enabled'). Spegni il GameObject.");
        }

        /// <summary>" [off]" se il componente e' disabilitato, "" altrimenti.</summary>
        public static string EnabledSuffix(Component c)
        {
            switch (c)
            {
                case Behaviour b: return b.enabled ? "" : "  [off]";
                case Renderer r: return r.enabled ? "" : "  [off]";
                case Collider col: return col.enabled ? "" : "  [off]";
                default: return "";
            }
        }

        // =====================================================================
        //  Membri (campi e proprieta')
        // =====================================================================

        public static MemberInfo FindMember(Type type, string name)
        {
            foreach (StringComparison cmp in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                for (Type t = type; t != null; t = t.BaseType)
                {
                    foreach (FieldInfo f in t.GetFields(AnyDeclared))
                    {
                        if (string.Equals(f.Name, name, cmp)) return f;
                    }
                    foreach (PropertyInfo p in t.GetProperties(AnyDeclared))
                    {
                        if (p.GetIndexParameters().Length == 0 && string.Equals(p.Name, name, cmp)) return p;
                    }
                }
            }
            return null;
        }

        public static MemberInfo RequireMember(Type type, string name)
        {
            MemberInfo m = FindMember(type, name);
            if (m == null)
            {
                throw new ConsoleException($"{type.Name} non ha un campo o una proprieta' '{name}'. Prova: get <go> {type.Name}");
            }
            return m;
        }

        public static Type MemberType(MemberInfo m)
        {
            return m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
        }

        public static object GetValue(object target, MemberInfo m)
        {
            if (m is FieldInfo f) return f.GetValue(f.IsStatic ? null : target);

            var p = (PropertyInfo)m;
            MethodInfo getter = p.GetGetMethod(true);
            if (getter == null) throw new ConsoleException($"{p.Name} e' in sola scrittura");
            return getter.Invoke(getter.IsStatic ? null : target, null);
        }

        /// <summary>Converte <paramref name="text"/> nel tipo del membro, lo scrive e restituisce il valore riletto.</summary>
        public static object SetValue(object target, MemberInfo m, string text)
        {
            Type type = MemberType(m);
            if (!TryConvert(text, type, out object value, out string error))
            {
                throw new ConsoleException($"{m.Name}: {error}");
            }

            if (m is FieldInfo f)
            {
                if (f.IsLiteral || f.IsInitOnly) throw new ConsoleException($"{f.Name} e' in sola lettura");
                f.SetValue(f.IsStatic ? null : target, value);
            }
            else
            {
                var p = (PropertyInfo)m;
                MethodInfo setter = p.GetSetMethod(true);
                if (setter == null) throw new ConsoleException($"{p.Name} e' in sola lettura");
                setter.Invoke(setter.IsStatic ? null : target, new[] { value });
            }

            try { return GetValue(target, m); }
            catch (Exception) { return value; }
        }

        /// <summary>
        /// Nome e valore dei membri "interessanti": per gli script, i campi
        /// pubblici o [SerializeField] e le proprieta' pubbliche; per i
        /// componenti di Unity, le proprieta' pubbliche del tipo.
        /// </summary>
        public static List<KeyValuePair<string, string>> ListMembers(object target, Type type)
        {
            var result = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>();

            foreach (Type t in OwnTypeChain(type))
            {
                bool engine = IsEngineType(t);

                foreach (FieldInfo f in t.GetFields(AnyDeclared))
                {
                    if (f.IsStatic || f.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)) continue;
                    bool visible = f.IsPublic || (!engine && f.IsDefined(typeof(SerializeField), false));
                    if (!visible || !seen.Add(f.Name)) continue;
                    result.Add(new KeyValuePair<string, string>(f.Name, SafeFormat(() => f.GetValue(target))));
                }

                foreach (PropertyInfo p in t.GetProperties(AnyDeclared))
                {
                    MethodInfo getter = p.GetGetMethod(false);
                    if (getter == null || getter.IsStatic || p.GetIndexParameters().Length > 0) continue;
                    if (p.IsDefined(typeof(ObsoleteAttribute), true) || s_unsafeGetters.Contains(p.Name)) continue;
                    if (!seen.Add(p.Name)) continue;
                    result.Add(new KeyValuePair<string, string>(p.Name, SafeFormat(() => p.GetValue(target))));
                }
            }

            return result;
        }

        // =====================================================================
        //  Metodi
        // =====================================================================

        /// <summary>
        /// Overload di <paramref name="name"/> dal tipo piu' derivato in su.
        /// <paramref name="stopAt"/> esclude quel tipo e le sue basi (null = fino a object).
        /// </summary>
        public static List<MethodInfo> FindMethods(Type type, string name, bool ignoreCase, Type stopAt = null)
        {
            var result = new List<MethodInfo>();
            var overridden = new HashSet<MethodInfo>();
            StringComparison cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            for (Type t = type; t != null && t != stopAt; t = t.BaseType)
            {
                foreach (MethodInfo m in t.GetMethods(AnyDeclared))
                {
                    if (!string.Equals(m.Name, name, cmp) || m.ContainsGenericParameters) continue;
                    // Un override compare sia nel derivato che nella base: si tiene il primo.
                    if (!overridden.Add(m.GetBaseDefinition())) continue;
                    result.Add(m);
                }
            }
            return result;
        }

        public static bool HasMethod(Type type, string name)
        {
            return FindMethods(type, name, true).Count > 0;
        }

        /// <summary>
        /// Chiama il metodo <paramref name="name"/> sul bersaglio scegliendo il
        /// primo overload a cui gli argomenti si convertono.
        /// </summary>
        public static object InvokeMethod(object target, string name, string[] args, out MethodInfo used)
        {
            Type type = target.GetType();
            List<MethodInfo> candidates = FindMethods(type, name, false);
            if (candidates.Count == 0) candidates = FindMethods(type, name, true);
            if (candidates.Count == 0)
            {
                throw new ConsoleException($"{type.Name} non ha un metodo '{name}'");
            }

            // Prima gli overload che consumano esattamente gli argomenti dati,
            // poi quelli con meno stringhe (cosi' "5" va a int prima che a string).
            IEnumerable<MethodInfo> ordered = candidates
                .OrderBy(m => m.GetParameters().Length == args.Length ? 0 : 1)
                .ThenBy(m => m.GetParameters().Count(p => p.ParameterType == typeof(string)))
                .ThenBy(m => m.GetParameters().Length);

            string firstError = null;
            foreach (MethodInfo m in ordered)
            {
                if (TryBind(m.GetParameters(), args, out object[] values, out string error))
                {
                    used = m;
                    return m.Invoke(m.IsStatic ? null : target, values);
                }
                if (firstError == null) firstError = error;
            }

            throw new ConsoleException($"{type.Name}.{name}: {firstError}\n  firme: {string.Join("  |  ", candidates.Select(Signature))}");
        }

        /// <summary>
        /// Converte gli argomenti testuali nei parametri di un metodo.
        /// Parametri con default = opzionali; un unico string[] = argomenti grezzi;
        /// se l'ultimo parametro e' string, raccoglie il testo avanzato.
        /// </summary>
        public static bool TryBind(ParameterInfo[] ps, string[] args, out object[] values, out string error)
        {
            values = null;
            error = null;

            if (ps.Length == 1 && ps[0].ParameterType == typeof(string[]))
            {
                values = new object[] { args };
                return true;
            }

            if (args.Length > ps.Length)
            {
                if (ps.Length > 0 && ps[ps.Length - 1].ParameterType == typeof(string))
                {
                    var merged = new string[ps.Length];
                    Array.Copy(args, merged, ps.Length - 1);
                    merged[ps.Length - 1] = string.Join(" ", args, ps.Length - 1, args.Length - ps.Length + 1);
                    args = merged;
                }
                else
                {
                    error = ps.Length == 0 ? "non vuole argomenti" : $"troppi argomenti (al massimo {ps.Length})";
                    return false;
                }
            }

            values = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                ParameterInfo p = ps[i];
                if (p.ParameterType.IsByRef)
                {
                    error = $"parametro ref/out '{p.Name}' non supportato";
                    return false;
                }

                if (i < args.Length)
                {
                    if (!TryConvert(args[i], p.ParameterType, out values[i], out string convError))
                    {
                        error = $"argomento {i + 1} ({p.Name}): {convError}";
                        return false;
                    }
                }
                else if (p.IsOptional)
                {
                    values[i] = DefaultFor(p);
                }
                else
                {
                    error = $"manca l'argomento {i + 1} ({TypeName(p.ParameterType)} {p.Name})";
                    return false;
                }
            }
            return true;
        }

        private static object DefaultFor(ParameterInfo p)
        {
            Type type = p.ParameterType;
            object value = p.HasDefaultValue ? p.DefaultValue : null;

            if (value == null || value is DBNull || value == Type.Missing)
            {
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            // Per gli enum alcuni runtime restituiscono il valore come intero.
            if (type.IsEnum && value.GetType() != type) return Enum.ToObject(type, value);
            return value;
        }

        /// <summary>
        /// Chiama un metodo su ogni script che lo ha, come GameObject.SendMessage
        /// ma convertendo gli argomenti nei tipi dei parametri (e con piu' di uno).
        /// Restituisce quanti script lo hanno ricevuto.
        /// </summary>
        public static int Deliver(IEnumerable<MonoBehaviour> targets, string message, string[] args, out string firstError)
        {
            int delivered = 0;
            firstError = null;

            foreach (MonoBehaviour mb in targets)
            {
                if (mb == null) continue;

                // Come SendMessage: nome case-sensitive, metodi anche privati,
                // solo quelli dello script e non di MonoBehaviour.
                foreach (MethodInfo m in FindMethods(mb.GetType(), message, false, typeof(MonoBehaviour)))
                {
                    if (m.IsStatic) continue;
                    if (!TryBind(m.GetParameters(), args, out object[] values, out string error))
                    {
                        if (firstError == null) firstError = $"{mb.GetType().Name}.{m.Name}: {error}";
                        continue;
                    }

                    delivered++;
                    try
                    {
                        if (m.Invoke(mb, values) is IEnumerator routine) mb.StartCoroutine(routine);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        DevConsole.LogError($"{mb.gameObject.name}.{mb.GetType().Name}.{m.Name}: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                    }
                    break;   // un solo overload per script, come SendMessage
                }
            }
            return delivered;
        }

        public static string Signature(MethodInfo m)
        {
            IEnumerable<string> ps = m.GetParameters().Select(p =>
                TypeName(p.ParameterType) + " " + p.Name + (p.HasDefaultValue ? "=" + Format(p.DefaultValue) : ""));
            return $"{m.Name}({string.Join(", ", ps)})";
        }

        // =====================================================================
        //  Nomi per l'autocompletamento
        // =====================================================================

        public static List<string> GameObjectNames()
        {
            var names = new HashSet<string>();
            foreach (GameObject go in AllGameObjects()) names.Add(go.name);
            return names.ToList();
        }

        /// <summary>
        /// Metodi da proporre col Tab: quelli del tipo e delle sue basi, senza
        /// quelli di MonoBehaviour/Component (Invoke, GetComponent...), che
        /// restano chiamabili ma affollerebbero l'elenco.
        /// </summary>
        public static List<string> MethodNames(Type type)
        {
            var names = new HashSet<string>();
            foreach (Type t in OwnTypeChain(type))
            {
                bool engine = IsEngineType(t);
                foreach (MethodInfo m in t.GetMethods(AnyDeclared))
                {
                    if (m.IsSpecialName || m.ContainsGenericParameters || m.Name.IndexOf('<') >= 0) continue;
                    if (engine && !m.IsPublic) continue;
                    names.Add(m.Name);
                }
            }
            return names.ToList();
        }

        /// <summary>Campi e proprieta' da proporre col Tab, con lo stesso criterio di MethodNames.</summary>
        public static List<string> MemberNames(Type type)
        {
            var names = new HashSet<string>();
            if (typeof(Behaviour).IsAssignableFrom(type)) names.Add("enabled");
            foreach (Type t in OwnTypeChain(type))
            {
                bool engine = IsEngineType(t);
                foreach (FieldInfo f in t.GetFields(AnyDeclared))
                {
                    if (f.Name.IndexOf('<') >= 0) continue;   // backing field generati
                    if (f.IsPublic || (!engine && f.IsDefined(typeof(SerializeField), false))) names.Add(f.Name);
                }
                foreach (PropertyInfo p in t.GetProperties(AnyDeclared))
                {
                    if (p.GetIndexParameters().Length == 0 && p.GetGetMethod(false) != null &&
                        !p.IsDefined(typeof(ObsoleteAttribute), true))
                    {
                        names.Add(p.Name);
                    }
                }
            }
            return names.ToList();
        }

        /// <summary>Dal tipo in su, escluse le basi di Unity (MonoBehaviour, Behaviour, Component, Object).</summary>
        private static IEnumerable<Type> OwnTypeChain(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t == typeof(MonoBehaviour) || t == typeof(Behaviour) || t == typeof(Component) ||
                    t == typeof(UnityEngine.Object) || t == typeof(object))
                {
                    yield break;
                }
                yield return t;
            }
        }

        private static bool IsEngineType(Type t)
        {
            return t.Namespace != null && t.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal);
        }

        // =====================================================================
        //  Conversione testo -> valore
        // =====================================================================

        public static bool TryConvert(string text, Type type, out object value, out string error)
        {
            value = null;
            error = null;

            if (type == typeof(string) || type == typeof(object))
            {
                value = text;
                return true;
            }

            text = text?.Trim() ?? "";

            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
            {
                if (IsNullWord(text)) return true;
                type = underlying;
            }
            else if (!type.IsValueType && IsNullWord(text))
            {
                return true;
            }

            if (type == typeof(bool))
            {
                if (TryParseBool(text, out bool b)) { value = b; return true; }
                error = $"'{text}' non e' un bool (true/false, 1/0, on/off)";
                return false;
            }

            if (type.IsEnum)
            {
                try
                {
                    value = Enum.Parse(type, text, true);
                    return true;
                }
                catch (Exception)
                {
                    error = $"'{text}' non e' un {type.Name}. Valori: {string.Join(", ", Enum.GetNames(type))}";
                    return false;
                }
            }

            if (type.IsPrimitive || type == typeof(decimal))
            {
                try
                {
                    value = Convert.ChangeType(NormalizeNumber(text), type, Inv);
                    return true;
                }
                catch (Exception)
                {
                    error = $"'{text}' non e' un {TypeName(type)} valido";
                    return false;
                }
            }

            if (TryConvertUnityValue(text, type, out value))
            {
                return true;
            }

            if (type == typeof(GameObject))
            {
                if (TryFindGameObject(text, out GameObject go, out error, out _)) { value = go; return true; }
                return false;
            }

            if (typeof(Component).IsAssignableFrom(type))
            {
                if (!TryFindGameObject(text, out GameObject go, out error, out _)) return false;
                Component c = go.GetComponent(type);
                if (c == null)
                {
                    error = $"'{go.name}' non ha un componente {type.Name}";
                    return false;
                }
                value = c;
                return true;
            }

            if (IsUnityValueType(type))
            {
                error = $"'{text}' non e' un {type.Name} valido (es. 1,2,3 oppure \"1 2 3\")";
                return false;
            }

            error = $"tipo {TypeName(type)} non supportato da console";
            return false;
        }

        private static bool IsUnityValueType(Type t)
        {
            return t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) ||
                   t == typeof(Vector2Int) || t == typeof(Vector3Int) || t == typeof(Quaternion) ||
                   t == typeof(Color) || t == typeof(Color32) || t == typeof(Rect);
        }

        private static bool TryConvertUnityValue(string text, Type type, out object value)
        {
            value = null;
            if (!IsUnityValueType(type)) return false;

            if (type == typeof(Color) || type == typeof(Color32))
            {
                // "#ff8800", "#ff8800cc", "red", "cyan"... oppure 3/4 numeri.
                if (ColorUtility.TryParseHtmlString(text, out Color html))
                {
                    value = type == typeof(Color32) ? (object)(Color32)html : html;
                    return true;
                }
            }

            if (!TryParseFloats(text, out float[] f)) return false;
            int n = f.Length;

            if (type == typeof(Vector2) && n <= 2)
            {
                value = n == 1 ? new Vector2(f[0], f[0]) : new Vector2(f[0], f[1]);
            }
            else if (type == typeof(Vector3) && n <= 3)
            {
                value = n == 1 ? Vector3.one * f[0] : new Vector3(f[0], f[1], n > 2 ? f[2] : 0.0f);
            }
            else if (type == typeof(Vector4) && (n == 1 || n >= 3) && n <= 4)
            {
                value = n == 1 ? Vector4.one * f[0] : new Vector4(f[0], f[1], f[2], n > 3 ? f[3] : 0.0f);
            }
            else if (type == typeof(Vector2Int) && n <= 2)
            {
                int x = Mathf.RoundToInt(f[0]);
                value = new Vector2Int(x, n > 1 ? Mathf.RoundToInt(f[1]) : x);
            }
            else if (type == typeof(Vector3Int) && n <= 3)
            {
                int x = Mathf.RoundToInt(f[0]);
                value = n == 1 ? new Vector3Int(x, x, x)
                               : new Vector3Int(x, Mathf.RoundToInt(f[1]), n > 2 ? Mathf.RoundToInt(f[2]) : 0);
            }
            else if (type == typeof(Quaternion) && (n == 3 || n == 4))
            {
                // 3 numeri = angoli di Eulero in gradi, 4 = x y z w.
                value = n == 3 ? Quaternion.Euler(f[0], f[1], f[2]) : new Quaternion(f[0], f[1], f[2], f[3]);
            }
            else if (type == typeof(Rect) && n == 4)
            {
                value = new Rect(f[0], f[1], f[2], f[3]);
            }
            else if ((type == typeof(Color) || type == typeof(Color32)) && (n == 3 || n == 4))
            {
                // Se c'e' un valore > 1 i numeri sono 0..255.
                float scale = f.Any(x => x > 1.0f) ? 1.0f / 255.0f : 1.0f;
                var c = new Color(f[0] * scale, f[1] * scale, f[2] * scale, n > 3 ? f[3] * scale : 1.0f);
                value = type == typeof(Color32) ? (object)(Color32)c : c;
            }

            return value != null;
        }

        private static bool TryParseFloats(string text, out float[] values)
        {
            string s = text.Trim().TrimStart('(', '[').TrimEnd(')', ']');
            string[] parts = s.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, Inv, out values[i])) return false;
            }
            return parts.Length > 0;
        }

        /// <summary>"0,5" -> "0.5": su una tastiera italiana la virgola scappa.</summary>
        private static string NormalizeNumber(string text)
        {
            if (text.IndexOf(',') >= 0 && text.IndexOf('.') < 0 && text.Count(ch => ch == ',') == 1)
            {
                return text.Replace(',', '.');
            }
            return text;
        }

        private static bool TryParseBool(string text, out bool value)
        {
            switch (text.ToLowerInvariant())
            {
                case "true": case "1": case "on": case "yes": case "si": case "s\u00ec":
                    value = true;
                    return true;
                case "false": case "0": case "off": case "no":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static bool IsNullWord(string text)
        {
            return string.Equals(text, "null", StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        //  Valore -> testo
        // =====================================================================

        public static string Format(object value)
        {
            return Format(value, 0);
        }

        private static string Format(object value, int depth)
        {
            switch (value)
            {
                case null:
                    return "null";
                case UnityEngine.Object uo:
                    // L'operatore == di Unity: un oggetto distrutto "e'" null.
                    if (uo == null) return "null (distrutto)";
                    if (uo is Component c) return $"{c.gameObject.name} ({c.GetType().Name})";
                    return $"{uo.name} ({uo.GetType().Name})";
                case string s:
                    return s;
                case bool b:
                    return b ? "true" : "false";
                case float f:
                    return f.ToString("0.#####", Inv);
                case double d:
                    return d.ToString("0.########", Inv);
                case Vector2 v2:
                    return v2.ToString("0.###");
                case Vector3 v3:
                    return v3.ToString("0.###");
                case Vector4 v4:
                    return v4.ToString("0.###");
                case Quaternion q:
                    return q.eulerAngles.ToString("0.##") + " (euler)";
                case Color col:
                    return "#" + ColorUtility.ToHtmlStringRGBA(col);
                case Color32 col32:
                    return "#" + ColorUtility.ToHtmlStringRGBA(col32);
                case IEnumerable seq when depth < 2:
                    var items = new List<string>();
                    int count = 0;
                    foreach (object item in seq)
                    {
                        if (count++ < 16) items.Add(Format(item, depth + 1));
                    }
                    return $"[{string.Join(", ", items)}{(count > 16 ? $", ... ({count} elementi)" : "")}]";
                case IFormattable formattable:
                    return Flatten(formattable.ToString(null, Inv));
                default:
                    return Flatten(value.ToString());
            }
        }

        private static string Flatten(string s)
        {
            return s.Replace("\r", "").Replace("\n", " | ").Replace("\t", " ");
        }

        private static string SafeFormat(Func<object> read)
        {
            try { return Format(read()); }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                return $"<{inner.GetType().Name}>";
            }
        }

        public static string TypeName(Type t)
        {
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(double)) return "double";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            if (t == typeof(long)) return "long";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(ulong)) return "ulong";
            if (t == typeof(short)) return "short";
            if (t == typeof(ushort)) return "ushort";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(sbyte)) return "sbyte";
            if (t == typeof(char)) return "char";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(object)) return "object";
            if (t == typeof(void)) return "void";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[]";
            Type underlying = Nullable.GetUnderlyingType(t);
            if (underlying != null) return TypeName(underlying) + "?";
            int tick = t.Name.IndexOf('`');
            return tick < 0 ? t.Name : t.Name.Substring(0, tick);
        }
    }
}
