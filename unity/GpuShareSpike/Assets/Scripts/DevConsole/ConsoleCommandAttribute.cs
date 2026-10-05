// ============================================================================
//  [ConsoleCommand]: espone un metodo alla DevConsole, come una funzione "exec"
//  di Unreal. Non serve registrare niente a mano: all'avvio la console cerca
//  questi attributi in tutti gli assembly che la referenziano.
//
//      [ConsoleCommand("spawn", "crea N nemici attorno al giocatore")]
//      private static void Spawn(int count, float radius = 5.0f) { ... }
//
//      > spawn 10
//      > spawn 10 2.5
//
//  REGOLE
//    - Metodo statico: viene chiamato direttamente.
//    - Metodo d'istanza di un componente (MonoBehaviour): viene chiamato su
//      TUTTE le istanze attive di quel componente nelle scene caricate.
//    - Parametri: convertiti dal testo. int, float, bool, string, enum,
//      Vector2/3/4, Quaternion, Color, e GameObject/Transform/componenti
//      passati per nome. Quelli con un valore di default sono opzionali.
//      Un unico parametro string[] riceve gli argomenti grezzi.
//    - Se il metodo restituisce un valore, viene stampato. Se restituisce un
//      IEnumerator, parte come coroutine.
//    - Nome omesso: si usa il nome del metodo in minuscolo.
//    - Per un errore "pulito" (senza stack trace) lancia ConsoleException.
// ============================================================================

using System;

namespace DevTools
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
    public sealed class ConsoleCommandAttribute : Attribute
    {
        public string Name { get; }
        public string Help { get; }

        public ConsoleCommandAttribute(string name = null, string help = "")
        {
            Name = name;
            Help = help ?? "";
        }
    }

    /// <summary>
    /// Errore previsto di un comando (oggetto non trovato, argomento sbagliato...):
    /// la console lo stampa in rosso senza stack trace.
    /// </summary>
    public sealed class ConsoleException : Exception
    {
        public ConsoleException(string message) : base(message) { }
    }

    /// <summary>
    /// Cosa si aspetta un argomento: serve solo all'autocompletamento con Tab.
    /// </summary>
    public enum ConsoleArg
    {
        Text,
        GameObject,
        /// <summary>Un componente del GameObject indicato dall'argomento GameObject precedente.</summary>
        Component,
        /// <summary>Un metodo del componente indicato dall'argomento Component precedente.</summary>
        Method,
        /// <summary>Un campo o una proprieta' del componente indicato dall'argomento Component precedente.</summary>
        Member,
        /// <summary>Un metodo di uno qualunque degli script del GameObject precedente (per send/broadcast).</summary>
        Message,
        Command,
        Scene,
        Bool,
        Enum,
    }
}
