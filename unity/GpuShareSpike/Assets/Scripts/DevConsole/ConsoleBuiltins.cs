// ============================================================================
//  Comandi predefiniti della DevConsole.
//
//  Gli argomenti <go> accettano un nome ("Player") o un percorso nella
//  gerarchia ("Level/Doors/Door 2"), anche di oggetti inattivi. Al posto del
//  nome di un componente si puo' scrivere "GameObject" per l'oggetto stesso:
//      set Cube GameObject layer 8
//      call Cube GameObject SetActive false
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DevTools
{
    internal static class ConsoleBuiltins
    {
        public static void RegisterAll()
        {
            DevConsole.Register("help", "help [comando|testo]", "elenca i comandi, o mostra come si usa uno", Help, ConsoleArg.Command);
            DevConsole.Register("clear", "clear", "pulisce la console", _ => DevConsole.Clear());
            DevConsole.RegisterAlias("cls", "clear");
            DevConsole.Register("echo", "echo <testo>", "stampa il testo", args => DevConsole.Log(string.Join(" ", args)));

            DevConsole.Register("find", "find [testo]", "cerca GameObject per nome, anche inattivi; senza testo elenca le radici delle scene",
                                Find, ConsoleArg.GameObject);
            DevConsole.Register("inspect", "inspect <go>", "stato, componenti e figli di un GameObject", Inspect, ConsoleArg.GameObject);
            DevConsole.RegisterAlias("ls", "inspect");

            DevConsole.Register("enable", "enable <go> [Componente]", "attiva un GameObject, o abilita un suo componente",
                                args => SetState(args, true, "enable"), ConsoleArg.GameObject, ConsoleArg.Component);
            DevConsole.Register("disable", "disable <go> [Componente]", "disattiva un GameObject, o disabilita un suo componente",
                                args => SetState(args, false, "disable"), ConsoleArg.GameObject, ConsoleArg.Component);
            DevConsole.Register("toggle", "toggle <go> [Componente]", "inverte lo stato di un GameObject o di un suo componente",
                                args => SetState(args, null, "toggle"), ConsoleArg.GameObject, ConsoleArg.Component);

            DevConsole.Register("call", "call <go> [Componente] <metodo> [arg...]", "chiama un metodo, anche privato; gli argomenti sono convertiti nei tipi dei parametri",
                                Call, ConsoleArg.GameObject, ConsoleArg.Component, ConsoleArg.Method);
            DevConsole.Register("send", "send <go|*> <messaggio> [arg...]", "come GameObject.SendMessage: chiama il metodo su ogni script del GameObject (* = tutti gli oggetti attivi)",
                                args => Send(args, false), ConsoleArg.GameObject, ConsoleArg.Message);
            DevConsole.Register("broadcast", "broadcast <go> <messaggio> [arg...]", "come BroadcastMessage: anche sugli script dei figli attivi",
                                args => Send(args, true), ConsoleArg.GameObject, ConsoleArg.Message);

            DevConsole.Register("get", "get <go> <Componente> [campo]", "legge un campo o una proprieta'; senza campo li elenca",
                                Get, ConsoleArg.GameObject, ConsoleArg.Component, ConsoleArg.Member);
            DevConsole.Register("set", "set <go> <Componente> <campo> <valore>", "scrive un campo o una proprieta', anche privati (Vector3: 1,2,3 oppure 1 2 3)",
                                Set, ConsoleArg.GameObject, ConsoleArg.Component, ConsoleArg.Member);

            DevConsole.Register("timescale", "timescale [valore]", "legge o imposta Time.timeScale (0 = pausa)", TimeScale);
            DevConsole.RegisterAlias("slomo", "timescale");
            DevConsole.Register("scene", "scene <nome|indice>", "carica una scena delle Build Settings", LoadScene, ConsoleArg.Scene);
            DevConsole.Register("reload", "reload", "ricarica la scena attiva", _ => ReloadScene());
            DevConsole.Register("quit", "quit", "esce dal gioco (nell'editor ferma il Play)", _ => Quit());
        }

        // =====================================================================

        private static void Require(string[] args, int count, string usage)
        {
            if (args.Length < count) throw new ConsoleException("uso: " + usage);
        }

        private static string Describe(GameObject go)
        {
            string state = go.activeInHierarchy ? "" : go.activeSelf ? "  [inattivo: genitore spento]" : "  [inattivo]";
            return ConsoleUtil.PathOf(go.transform) + state;
        }

        private static string Name(object target)
        {
            return target is Component c ? $"{c.gameObject.name}.{c.GetType().Name}" : ((GameObject)target).name;
        }

        // =====================================================================

        private static void Help(string[] args)
        {
            if (args.Length > 0 && DevConsole.TryGetCommandInfo(args[0], out string usage, out string help, out IList<string> aliases))
            {
                DevConsole.Log(usage);
                if (help.Length > 0) DevConsole.Log("  " + help);
                if (aliases.Count > 0) DevConsole.Log("  alias: " + string.Join(", ", aliases));
                return;
            }

            string filter = args.Length > 0 ? string.Join(" ", args) : null;
            List<KeyValuePair<string, string>> commands = DevConsole.CommandList()
                .Where(c => filter == null ||
                            c.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (commands.Count == 0)
            {
                DevConsole.LogWarning($"nessun comando contiene '{filter}'");
                return;
            }

            int width = Mathf.Min(48, commands.Max(c => c.Key.Length));
            foreach (KeyValuePair<string, string> c in commands)
            {
                DevConsole.Log((c.Key.PadRight(width) + "   " + c.Value).TrimEnd());
            }

            if (filter == null)
            {
                DevConsole.Log("Tab completa  |  su/giu' cronologia  |  \"virgolette\" per i nomi con spazi  |  ';' separa piu' comandi", DimColor);
            }
        }

        private static readonly Color DimColor = new Color(0.55f, 0.58f, 0.63f);

        private static void Find(string[] args)
        {
            if (args.Length == 0)
            {
                foreach (IGrouping<string, GameObject> scene in ConsoleUtil.RootObjects().GroupBy(go => go.scene.name))
                {
                    DevConsole.Log($"scena '{scene.Key}':", DimColor);
                    foreach (GameObject root in scene) DevConsole.Log("  " + Describe(root));
                }
                return;
            }

            const int limit = 100;
            string text = string.Join(" ", args);
            int found = 0;
            foreach (GameObject go in ConsoleUtil.AllGameObjects())
            {
                if (go.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (found < limit) DevConsole.Log(Describe(go));
                found++;
            }

            if (found == 0) DevConsole.LogWarning($"nessun GameObject contiene '{text}'");
            else DevConsole.Log($"{found} trovati" + (found > limit ? $" (mostrati i primi {limit})" : ""), DimColor);
        }

        private static void Inspect(string[] args)
        {
            Require(args, 1, "inspect <go>");
            GameObject go = ConsoleUtil.FindGameObject(args[0]);
            Transform t = go.transform;

            DevConsole.Log(Describe(go));
            DevConsole.Log($"  attivo {go.activeSelf}   layer {LayerMask.LayerToName(go.layer)} ({go.layer})   tag {go.tag}   scena {go.scene.name}", DimColor);
            DevConsole.Log($"  posizione {ConsoleUtil.Format(t.position)}   rotazione {ConsoleUtil.Format(t.eulerAngles)}   scala {ConsoleUtil.Format(t.localScale)}", DimColor);

            DevConsole.Log("componenti:");
            foreach (Component c in go.GetComponents<Component>())
            {
                DevConsole.Log(c == null ? "  (script mancante)" : "  " + c.GetType().Name + ConsoleUtil.EnabledSuffix(c));
            }

            if (t.childCount > 0)
            {
                DevConsole.Log($"figli ({t.childCount}):");
                foreach (Transform child in t)
                {
                    DevConsole.Log("  " + child.name + (child.gameObject.activeSelf ? "" : "  [inattivo]"));
                }
            }
        }

        private static void SetState(string[] args, bool? value, string command)
        {
            Require(args, 1, command + " <go> [Componente]");
            GameObject go = ConsoleUtil.FindGameObject(args[0]);

            if (args.Length >= 2)
            {
                Component c = ConsoleUtil.FindComponent(go, args[1]);
                if (c == DevConsole.Instance && value != true)
                {
                    throw new ConsoleException("questo spegnerebbe la console stessa: non si potrebbe piu' riaprire");
                }
                bool enabled = ConsoleUtil.SetEnabled(c, value);
                DevConsole.Log($"{go.name}.{c.GetType().Name} -> {(enabled ? "abilitato" : "disabilitato")}");
                return;
            }

            if (DevConsole.Instance != null && DevConsole.Instance.transform.IsChildOf(go.transform) && value != true)
            {
                throw new ConsoleException("questo spegnerebbe la console stessa: non si potrebbe piu' riaprire");
            }

            bool active = value ?? !go.activeSelf;
            go.SetActive(active);
            string note = active && !go.activeInHierarchy ? "  (ma un genitore e' inattivo, quindi resta spento)" : "";
            DevConsole.Log($"{ConsoleUtil.PathOf(go.transform)} -> {(active ? "attivo" : "inattivo")}{note}");
        }

        private static void Call(string[] args)
        {
            const string usage = "call <go> [Componente] <metodo> [arg...]";
            Require(args, 2, usage);
            GameObject go = ConsoleUtil.FindGameObject(args[0]);

            // Con il componente esplicito...
            if (args.Length >= 3)
            {
                object target = null;
                if (string.Equals(args[1], "GameObject", StringComparison.OrdinalIgnoreCase)) target = go;
                else if (ConsoleUtil.TryFindComponent(go, args[1], out Component c)) target = c;

                if (target != null)
                {
                    Invoke(target, args[2], args.Skip(3).ToArray());
                    return;
                }
            }

            // ...oppure il primo componente del GameObject che ha quel metodo.
            string method = args[1];
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c != null && ConsoleUtil.HasMethod(c.GetType(), method))
                {
                    Invoke(c, method, args.Skip(2).ToArray());
                    return;
                }
            }
            if (ConsoleUtil.HasMethod(typeof(GameObject), method))
            {
                Invoke(go, method, args.Skip(2).ToArray());
                return;
            }

            throw new ConsoleException($"nessun componente di '{go.name}' ha un metodo '{method}'. Componenti: {string.Join(", ", ConsoleUtil.ComponentNames(go))}");
        }

        private static void Invoke(object target, string methodName, string[] args)
        {
            object result = ConsoleUtil.InvokeMethod(target, methodName, args, out System.Reflection.MethodInfo used);
            string label = $"{Name(target)}.{used.Name}()";

            if (result is System.Collections.IEnumerator && target is MonoBehaviour)
            {
                DevConsole.ReportResult(used, target, result, "");
                return;
            }
            DevConsole.Log(used.ReturnType == typeof(void) ? label + " eseguito" : $"{label} = {ConsoleUtil.Format(result)}");
        }

        private static void Send(string[] args, bool includeChildren)
        {
            string usage = includeChildren ? "broadcast <go> <messaggio> [arg...]" : "send <go|*> <messaggio> [arg...]";
            Require(args, 2, usage);

            string message = args[1];
            string[] rest = args.Skip(2).ToArray();
            IEnumerable<MonoBehaviour> targets;
            string where;

            if (args[0] == "*")
            {
                targets = ConsoleUtil.AllGameObjects()
                                     .Where(go => go.activeInHierarchy)
                                     .SelectMany(go => go.GetComponents<MonoBehaviour>())
                                     .ToList();
                where = "tutti gli oggetti attivi";
            }
            else
            {
                GameObject go = ConsoleUtil.FindGameObject(args[0]);
                targets = includeChildren
                    ? go.GetComponentsInChildren<MonoBehaviour>(true).Where(mb => mb != null && (mb.gameObject == go || mb.gameObject.activeInHierarchy)).ToList()
                    : go.GetComponents<MonoBehaviour>().ToList();
                where = $"'{go.name}'" + (includeChildren ? " e figli" : "");
            }

            int delivered = ConsoleUtil.Deliver(targets, message, rest, out string firstError);
            if (delivered == 0)
            {
                throw new ConsoleException(firstError ?? $"nessuno script di {where} ha un metodo '{message}'");
            }
            DevConsole.Log($"'{message}' consegnato a {delivered} script ({where})");
        }

        private static void Get(string[] args)
        {
            Require(args, 2, "get <go> <Componente> [campo]");
            GameObject go = ConsoleUtil.FindGameObject(args[0]);
            object target = ConsoleUtil.ResolveTarget(go, args[1]);
            Type type = target.GetType();

            if (args.Length == 2)
            {
                DevConsole.Log(Name(target) + ":");
                foreach (KeyValuePair<string, string> member in ConsoleUtil.ListMembers(target, type))
                {
                    DevConsole.Log($"  {member.Key} = {member.Value}");
                }
                return;
            }

            System.Reflection.MemberInfo m = ConsoleUtil.RequireMember(type, args[2]);
            DevConsole.Log($"{Name(target)}.{m.Name} = {ConsoleUtil.Format(ConsoleUtil.GetValue(target, m))}");
        }

        private static void Set(string[] args)
        {
            Require(args, 4, "set <go> <Componente> <campo> <valore>");
            GameObject go = ConsoleUtil.FindGameObject(args[0]);
            object target = ConsoleUtil.ResolveTarget(go, args[1]);
            System.Reflection.MemberInfo m = ConsoleUtil.RequireMember(target.GetType(), args[2]);

            // Il valore e' tutto il resto: "set Cube Transform position 1 2 3" funziona.
            object now = ConsoleUtil.SetValue(target, m, string.Join(" ", args, 3, args.Length - 3));
            DevConsole.Log($"{Name(target)}.{m.Name} = {ConsoleUtil.Format(now)}");
        }

        private static void TimeScale(string[] args)
        {
            if (args.Length > 0)
            {
                if (!ConsoleUtil.TryConvert(args[0], typeof(float), out object value, out string error)) throw new ConsoleException(error);
                Time.timeScale = Mathf.Clamp((float)value, 0.0f, 100.0f);
            }
            DevConsole.Log($"Time.timeScale = {ConsoleUtil.Format(Time.timeScale)}");
        }

        private static void LoadScene(string[] args)
        {
            Require(args, 1, "scene <nome|indice>");
            int count = SceneManager.sceneCountInBuildSettings;

            if (int.TryParse(args[0], out int index))
            {
                if (index < 0 || index >= count) throw new ConsoleException($"indice fuori dalle Build Settings (0..{count - 1})");
                DevConsole.Log($"carico la scena {index}");
                SceneManager.LoadScene(index);
                return;
            }

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                names.Add(name);
                if (string.Equals(name, args[0], StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, args[0], StringComparison.OrdinalIgnoreCase))
                {
                    DevConsole.Log($"carico '{name}'");
                    SceneManager.LoadScene(i);
                    return;
                }
            }

            throw new ConsoleException(count == 0
                ? "nessuna scena nelle Build Settings (File > Build Settings > Add Open Scenes)"
                : $"nessuna scena '{args[0]}' nelle Build Settings. Ci sono: {string.Join(", ", names)}");
        }

        private static void ReloadScene()
        {
            Scene active = SceneManager.GetActiveScene();
            DevConsole.Log($"ricarico '{active.name}'");

            if (active.buildIndex >= 0)
            {
                SceneManager.LoadScene(active.buildIndex);
                return;
            }

#if UNITY_EDITOR
            // Nell'editor una scena che non e' nelle Build Settings si carica per percorso.
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(active.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            throw new ConsoleException("la scena attiva non e' nelle Build Settings");
#endif
        }

        private static void Quit()
        {
            DevConsole.Log("esco");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
