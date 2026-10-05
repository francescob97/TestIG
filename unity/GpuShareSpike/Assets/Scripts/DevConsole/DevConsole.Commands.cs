// ============================================================================
//  DevConsole, meta' "motore": registro dei comandi, parsing della riga,
//  esecuzione, autocompletamento. Tutto statico: un comando si puo'
//  registrare ed eseguire anche prima che la console esista.
//
//  SINTASSI DELLA RIGA
//    comando arg1 arg2 ...        separatori: spazi
//    "Main Camera"                virgolette per gli argomenti con spazi
//    cmd1 ; cmd2                  piu' comandi sulla stessa riga
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DevTools
{
    public sealed partial class DevConsole
    {
        private sealed class Command
        {
            public string Name;
            public string Usage;
            public string Help;
            public Action<string[]> Handler;
            public ConsoleArg[] Args;
            public Type[] ArgTypes;   // solo per i comandi da attributo: serve a completare gli enum
            public readonly List<string> Aliases = new List<string>();
        }

        private struct Token
        {
            public string Text;
            public int Start;
        }

        private static readonly Dictionary<string, Command> s_commands = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);
        private static bool s_initialized;

        // =====================================================================
        //  Registrazione
        // =====================================================================

        /// <summary>Registra un comando. Il gestore riceve gli argomenti gia' separati.</summary>
        public static void Register(string name, string help, Action<string[]> handler)
        {
            Register(name, null, help, handler);
        }

        /// <param name="usage">Riga d'uso mostrata da help, es. "spawn &lt;nome&gt; [quanti]".</param>
        /// <param name="argKinds">Cosa si aspetta ogni argomento, per il completamento con Tab.</param>
        public static void Register(string name, string usage, string help, Action<string[]> handler, params ConsoleArg[] argKinds)
        {
            RegisterCore(name, usage, help, handler, argKinds, null);
        }

        /// <summary>Un secondo nome per un comando esistente.</summary>
        public static void RegisterAlias(string alias, string commandName)
        {
            EnsureInitialized();
            if (!s_commands.TryGetValue(commandName, out Command cmd))
            {
                Debug.LogWarning($"[DevConsole] alias '{alias}': il comando '{commandName}' non esiste");
                return;
            }
            s_commands[alias] = cmd;
            if (!cmd.Aliases.Contains(alias)) cmd.Aliases.Add(alias);
        }

        public static void Unregister(string name)
        {
            EnsureInitialized();
            if (s_commands.TryGetValue(name, out Command cmd))
            {
                s_commands.Remove(name);
                cmd.Aliases.Remove(name);
            }
        }

        private static void RegisterCore(string name, string usage, string help, Action<string[]> handler, ConsoleArg[] args, Type[] argTypes)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.IndexOf(';') >= 0)
            {
                Debug.LogWarning($"[DevConsole] nome di comando non valido: '{name}'");
                return;
            }
            if (handler == null) throw new ArgumentNullException(nameof(handler));

            s_commands[name] = new Command
            {
                Name = name,
                Usage = string.IsNullOrEmpty(usage) ? name : usage,
                Help = help ?? "",
                Handler = handler,
                Args = args ?? Array.Empty<ConsoleArg>(),
                ArgTypes = argTypes,
            };
        }

        private static void EnsureInitialized()
        {
            if (s_initialized) return;
            s_initialized = true;   // prima di registrare: Register richiama questo metodo

            ConsoleBuiltins.RegisterAll();
            RegisterAttributeCommands();
        }

        // =====================================================================
        //  [ConsoleCommand]
        // =====================================================================

        private static void RegisterAttributeCommands()
        {
            Assembly own = typeof(ConsoleCommandAttribute).Assembly;
            string ownName = own.GetName().Name;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // Solo gli assembly che possono usare l'attributo: evita di
                // scandire System, UnityEngine e tutto il resto.
                if (asm.IsDynamic || (asm != own && !References(asm, ownName))) continue;

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (Type type in types)
                {
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                                                  BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (!method.IsDefined(typeof(ConsoleCommandAttribute), false)) continue;
                        foreach (ConsoleCommandAttribute attr in method.GetCustomAttributes(typeof(ConsoleCommandAttribute), false))
                        {
                            RegisterMethod(method, attr);
                        }
                    }
                }
            }
        }

        private static bool References(Assembly asm, string name)
        {
            try
            {
                foreach (AssemblyName reference in asm.GetReferencedAssemblies())
                {
                    if (reference.Name == name) return true;
                }
            }
            catch (Exception)
            {
                // Alcuni assembly non lo permettono: semplicemente non li scandiamo.
            }
            return false;
        }

        private static void RegisterMethod(MethodInfo method, ConsoleCommandAttribute attr)
        {
            string where = $"{method.DeclaringType?.Name}.{method.Name}";
            string name = string.IsNullOrWhiteSpace(attr.Name) ? method.Name.ToLowerInvariant() : attr.Name.Trim();

            if (method.ContainsGenericParameters)
            {
                Debug.LogWarning($"[DevConsole] [ConsoleCommand] su {where}: i metodi generici non sono supportati");
                return;
            }
            if (!method.IsStatic && !typeof(Component).IsAssignableFrom(method.DeclaringType))
            {
                Debug.LogWarning($"[DevConsole] [ConsoleCommand] su {where}: un metodo d'istanza deve stare in un componente (MonoBehaviour), altrimenti rendilo static");
                return;
            }

            ParameterInfo[] ps = method.GetParameters();
            if (ps.Any(p => p.ParameterType.IsByRef))
            {
                Debug.LogWarning($"[DevConsole] [ConsoleCommand] su {where}: parametri ref/out non supportati");
                return;
            }

            bool raw = ps.Length == 1 && ps[0].ParameterType == typeof(string[]);
            ConsoleArg[] kinds = raw ? Array.Empty<ConsoleArg>() : ps.Select(p => ArgKindFor(p.ParameterType)).ToArray();
            Type[] types = raw ? null : ps.Select(p => p.ParameterType).ToArray();
            string usage = raw ? name + " [arg...]" : BuildUsage(name, ps);

            RegisterCore(name, usage, attr.Help, args => InvokeAttributeCommand(method, ps, usage, args), kinds, types);
        }

        private static ConsoleArg ArgKindFor(Type t)
        {
            if (t == typeof(GameObject) || typeof(Component).IsAssignableFrom(t)) return ConsoleArg.GameObject;
            if (t == typeof(bool)) return ConsoleArg.Bool;
            if (t.IsEnum) return ConsoleArg.Enum;
            return ConsoleArg.Text;
        }

        private static string BuildUsage(string name, ParameterInfo[] ps)
        {
            var sb = new StringBuilder(name);
            foreach (ParameterInfo p in ps)
            {
                string type = ConsoleUtil.TypeName(p.ParameterType);
                if (p.IsOptional)
                {
                    string def = p.HasDefaultValue ? "=" + ConsoleUtil.Format(p.DefaultValue) : "";
                    sb.Append($" [{type} {p.Name}{def}]");
                }
                else
                {
                    sb.Append($" <{type} {p.Name}>");
                }
            }
            return sb.ToString();
        }

        private static void InvokeAttributeCommand(MethodInfo method, ParameterInfo[] ps, string usage, string[] args)
        {
            if (!ConsoleUtil.TryBind(ps, args, out object[] values, out string error))
            {
                throw new ConsoleException($"{error}\n  uso: {usage}");
            }

            if (method.IsStatic)
            {
                ReportResult(method, null, method.Invoke(null, values), null);
                return;
            }

            List<Component> targets = ConsoleUtil.FindActiveComponents(method.DeclaringType);
            if (targets.Count == 0)
            {
                throw new ConsoleException($"nessuna istanza attiva di {method.DeclaringType.Name} nelle scene caricate");
            }
            foreach (Component target in targets)
            {
                ReportResult(method, target, method.Invoke(target, values), target.gameObject.name + ": ");
            }
        }

        /// <summary>Stampa il valore restituito; se e' un IEnumerator lo avvia come coroutine.</summary>
        internal static void ReportResult(MethodInfo method, object target, object result, string prefix)
        {
            if (result is IEnumerator routine)
            {
                MonoBehaviour host = target as MonoBehaviour;
                if (host == null) host = Instance;
                if (host == null)
                {
                    LogWarning($"{prefix}{method.Name}: restituisce una coroutine ma non c'e' un MonoBehaviour su cui avviarla");
                    return;
                }
                host.StartCoroutine(routine);
                Log($"{prefix}{method.Name}: coroutine avviata");
                return;
            }
            if (method.ReturnType != typeof(void))
            {
                Log(prefix + ConsoleUtil.Format(result));
            }
        }

        // =====================================================================
        //  Esecuzione
        // =====================================================================

        /// <summary>
        /// Esegue una riga di comandi come se fosse stata scritta in console.
        /// Utilizzabile anche da codice, es. DevConsole.Execute("timescale 0.5").
        /// </summary>
        public static void Execute(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return;
            EnsureInitialized();

            AddLine("> " + commandLine.Trim(), EchoColor);

            foreach (string statement in SplitStatements(commandLine))
            {
                List<Token> tokens = Tokenize(statement, out _);
                if (tokens.Count == 0) continue;

                string name = tokens[0].Text;
                var args = new string[tokens.Count - 1];
                for (int i = 1; i < tokens.Count; i++) args[i - 1] = tokens[i].Text;

                if (!s_commands.TryGetValue(name, out Command cmd))
                {
                    List<string> similar = MatchNames(CommandNames(), name).Take(5).ToList();
                    LogError($"comando sconosciuto '{name}'." + (similar.Count > 0 ? $" Forse: {string.Join(", ", similar)}" : " Scrivi 'help'."));
                    continue;
                }

                Run(cmd, args);
            }
        }

        private static void Run(Command cmd, string[] args)
        {
            try
            {
                cmd.Handler(args);
            }
            catch (ConsoleException ex)
            {
                LogError(ex.Message);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                // Eccezione lanciata dal metodo chiamato via reflection.
                if (ex.InnerException is ConsoleException inner) LogError(inner.Message);
                else ReportException(cmd.Name, ex.InnerException);
            }
            catch (Exception ex)
            {
                ReportException(cmd.Name, ex);
            }
        }

        private static void ReportException(string commandName, Exception ex)
        {
            LogError($"{commandName}: {ex.GetType().Name}: {ex.Message}");
            AddLine("  (stack trace completo nella Console di Unity)", DimColor);

            // Nella Console di Unity si', ma non anche rispecchiato qui una seconda volta.
            s_suppressMirror = true;
            try { Debug.LogException(ex); }
            finally { s_suppressMirror = false; }
        }

        internal static bool TryGetCommandInfo(string name, out string usage, out string help, out IList<string> aliases)
        {
            EnsureInitialized();
            if (s_commands.TryGetValue(name, out Command cmd))
            {
                usage = cmd.Usage;
                help = cmd.Help;
                aliases = cmd.Aliases;
                return true;
            }
            usage = help = null;
            aliases = null;
            return false;
        }

        /// <summary>Un elemento per comando (gli alias non si ripetono), in ordine alfabetico.</summary>
        internal static List<KeyValuePair<string, string>> CommandList()
        {
            EnsureInitialized();
            return s_commands.Values.Distinct()
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new KeyValuePair<string, string>(
                    c.Usage + (c.Aliases.Count > 0 ? $"  ({string.Join(", ", c.Aliases)})" : ""), c.Help))
                .ToList();
        }

        private static IEnumerable<string> CommandNames()
        {
            return s_commands.Keys;
        }

        // =====================================================================
        //  Parsing
        // =====================================================================

        /// <summary>Spezza la riga sui ';' fuori dalle virgolette.</summary>
        private static List<string> SplitStatements(string line)
        {
            var result = new List<string>();
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == ';' && !quoted)
                {
                    result.Add(line.Substring(start, i - start));
                    start = i + 1;
                }
            }
            result.Add(line.Substring(start));
            return result;
        }

        private static int LastStatementStart(string line)
        {
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == ';' && !quoted) start = i + 1;
            }
            return start;
        }

        private static List<Token> Tokenize(string s, out bool openQuote)
        {
            var tokens = new List<Token>();
            var sb = new StringBuilder();
            openQuote = false;
            int i = 0;

            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;

                int start = i;
                sb.Clear();
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    if (s[i] != '"')
                    {
                        sb.Append(s[i++]);
                        continue;
                    }

                    int close = s.IndexOf('"', i + 1);
                    if (close < 0)
                    {
                        sb.Append(s, i + 1, s.Length - i - 1);
                        i = s.Length;
                        openQuote = true;
                        break;
                    }
                    sb.Append(s, i + 1, close - i - 1);
                    i = close + 1;
                }
                tokens.Add(new Token { Text = sb.ToString(), Start = start });
            }
            return tokens;
        }

        private static string QuoteIfNeeded(string s)
        {
            if (s.Length == 0 || s.Any(ch => char.IsWhiteSpace(ch) || ch == ';')) return "\"" + s + "\"";
            return s;
        }

        // =====================================================================
        //  Autocompletamento
        // =====================================================================

        /// <summary>
        /// Candidati per l'ultima parola di <paramref name="input"/>.
        /// <paramref name="prefix"/> e' il testo da tenere davanti al candidato scelto.
        /// </summary>
        private static List<string> GetCompletions(string input, out string prefix, out bool completingCommand)
        {
            EnsureInitialized();

            int statementStart = LastStatementStart(input);
            string statement = input.Substring(statementStart);
            List<Token> tokens = Tokenize(statement, out bool openQuote);

            bool startsNewToken = !openQuote && (statement.Length == 0 || char.IsWhiteSpace(statement[statement.Length - 1]));
            int argIndex = startsNewToken ? tokens.Count : tokens.Count - 1;
            string partial = startsNewToken ? "" : tokens[argIndex].Text;
            int partialStart = startsNewToken ? statement.Length : tokens[argIndex].Start;

            prefix = input.Substring(0, statementStart + partialStart);
            completingCommand = argIndex == 0;

            IEnumerable<string> pool;
            if (argIndex == 0)
            {
                pool = CommandNames();
            }
            else
            {
                var typed = new string[argIndex - 1];
                for (int i = 1; i < argIndex; i++) typed[i - 1] = tokens[i].Text;
                pool = ArgumentCandidates(tokens[0].Text, typed, argIndex - 1);
            }

            return MatchNames(pool, partial).Take(200).ToList();
        }

        /// <summary>Prima chi inizia con <paramref name="partial"/>, poi chi lo contiene.</summary>
        private static IEnumerable<string> MatchNames(IEnumerable<string> pool, string partial)
        {
            List<string> all = pool.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (string.IsNullOrEmpty(partial)) return all;

            IEnumerable<string> starts = all.Where(n => n.StartsWith(partial, StringComparison.OrdinalIgnoreCase));
            IEnumerable<string> contains = all.Where(n => !n.StartsWith(partial, StringComparison.OrdinalIgnoreCase) &&
                                                          n.IndexOf(partial, StringComparison.OrdinalIgnoreCase) >= 0);
            return starts.Concat(contains);
        }

        private static IEnumerable<string> ArgumentCandidates(string commandName, string[] typed, int position)
        {
            if (!s_commands.TryGetValue(commandName, out Command cmd) || position >= cmd.Args.Length)
            {
                return Array.Empty<string>();
            }

            try
            {
                switch (cmd.Args[position])
                {
                    case ConsoleArg.Command:
                        return CommandNames();

                    case ConsoleArg.GameObject:
                        return ConsoleUtil.GameObjectNames();

                    case ConsoleArg.Component:
                    {
                        GameObject go = PreviousGameObject(cmd, typed, position);
                        if (go == null) return Array.Empty<string>();
                        List<string> names = ConsoleUtil.ComponentNames(go);
                        names.Insert(0, "GameObject");
                        return names;
                    }

                    case ConsoleArg.Method:
                    {
                        object target = PreviousTarget(cmd, typed, position);
                        return target == null ? Array.Empty<string>() : (IEnumerable<string>)ConsoleUtil.MethodNames(target.GetType());
                    }

                    case ConsoleArg.Member:
                    {
                        object target = PreviousTarget(cmd, typed, position);
                        return target == null ? Array.Empty<string>() : (IEnumerable<string>)ConsoleUtil.MemberNames(target.GetType());
                    }

                    case ConsoleArg.Message:
                    {
                        GameObject go = PreviousGameObject(cmd, typed, position);
                        if (go == null) return Array.Empty<string>();
                        return go.GetComponents<MonoBehaviour>()
                                 .Where(mb => mb != null)
                                 .SelectMany(mb => ConsoleUtil.MethodNames(mb.GetType()));
                    }

                    case ConsoleArg.Scene:
                    {
                        var names = new List<string>();
                        for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                        {
                            names.Add(System.IO.Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i)));
                        }
                        return names;
                    }

                    case ConsoleArg.Bool:
                        return new[] { "true", "false" };

                    case ConsoleArg.Enum:
                        Type enumType = cmd.ArgTypes != null && position < cmd.ArgTypes.Length ? cmd.ArgTypes[position] : null;
                        return enumType != null && enumType.IsEnum ? Enum.GetNames(enumType) : Array.Empty<string>();

                    default:
                        return Array.Empty<string>();
                }
            }
            catch (Exception)
            {
                // Il completamento non deve mai rompere la console.
                return Array.Empty<string>();
            }
        }

        private static GameObject PreviousGameObject(Command cmd, string[] typed, int position)
        {
            for (int i = Mathf.Min(position, typed.Length) - 1; i >= 0; i--)
            {
                if (i < cmd.Args.Length && cmd.Args[i] == ConsoleArg.GameObject)
                {
                    return ConsoleUtil.TryFindGameObject(typed[i], out GameObject go) ? go : null;
                }
            }
            return null;
        }

        private static object PreviousTarget(Command cmd, string[] typed, int position)
        {
            GameObject go = PreviousGameObject(cmd, typed, position);
            if (go == null) return null;

            for (int i = Mathf.Min(position, typed.Length) - 1; i >= 0; i--)
            {
                if (i < cmd.Args.Length && cmd.Args[i] == ConsoleArg.Component)
                {
                    if (string.Equals(typed[i], "GameObject", StringComparison.OrdinalIgnoreCase)) return go;
                    return ConsoleUtil.TryFindComponent(go, typed[i], out Component c) ? c : null;
                }
            }
            return null;
        }
    }
}
