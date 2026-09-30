// ============================================================================
//  Stato e controllo del runtime XR, SENZA dipendenza di compilazione da
//  XR Plug-in Management.
//
//  PERCHE' LA RIFLESSIONE:
//  il pacchetto com.unity.xr.management potrebbe non essere installato (nel
//  progetto di partenza non c'e'). Se questi script lo referenziassero
//  direttamente, senza il pacchetto NON COMPILEREBBE NIENTE, modalita' desktop
//  compresa, e Unity non entrerebbe nemmeno in Play. L'alternativa "pulita"
//  (un assembly separato con define condizionali) richiede di spostare tutti
//  gli script in assembly definition: piu' parti mobili per ottenere la
//  stessa cosa. Qui la riflessione tocca quattro metodi pubblici e stabili, e
//  se il pacchetto manca il risultato e' un messaggio chiaro in HUD, non un
//  errore di compilazione.
//
//  Cosa serve dal pacchetto (tutto pubblico e documentato):
//    XRGeneralSettings.Instance.Manager  -> XRManagerSettings
//    XRManagerSettings.activeLoader
//    XRManagerSettings.InitializeLoader()   (coroutine)
//    XRManagerSettings.StartSubsystems() / StopSubsystems() / DeinitializeLoader()
//
//  Rilevare se l'XR e' attivo invece NON richiede il pacchetto: i sottosistemi
//  di display sono nel modulo XR del motore, sempre presente.
// ============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace GpuShareSpike
{
    public static class XrRuntime
    {
        private static readonly List<XRDisplaySubsystem> s_displays = new List<XRDisplaySubsystem>();
        private static Type s_generalSettingsType;
        private static bool s_typeResolved;

        /// <summary>Un display XR (visore) sta renderizzando.</summary>
        public static bool IsActive => ActiveDisplay != null;

        public static XRDisplaySubsystem ActiveDisplay
        {
            get
            {
                SubsystemManager.GetSubsystems(s_displays);
                for (int i = 0; i < s_displays.Count; ++i)
                {
                    if (s_displays[i] != null && s_displays[i].running) return s_displays[i];
                }
                return null;
            }
        }

        /// <summary>XR Plug-in Management e' installato e configurato: possiamo avviare/fermare l'XR da codice.</summary>
        public static bool CanControl => GetManager() != null;

        public static string StereoModeName
        {
            get
            {
                try { return XRSettings.stereoRenderingMode.ToString(); }
                catch { return "sconosciuto"; }
            }
        }

        /// <summary>
        /// Single Pass Instanced (o multiview): una draw call disegna entrambi gli
        /// occhi e lo shader sa per quale occhio sta lavorando. E' quello che
        /// serve al presenter VR per scegliere la texture dell'occhio giusto.
        /// </summary>
        public static bool IsSinglePassInstanced
        {
            get
            {
                try
                {
                    var mode = XRSettings.stereoRenderingMode;
                    return mode == XRSettings.StereoRenderingMode.SinglePassInstanced
                        || mode == XRSettings.StereoRenderingMode.SinglePassMultiview;
                }
                catch { return false; }
            }
        }

        /// <summary>Coroutine: inizializza il loader XR (se serve) e avvia i sottosistemi.</summary>
        public static IEnumerator StartRoutine(Action<string> onError)
        {
            object manager = GetManager();
            if (manager == null)
            {
                onError?.Invoke("XR Plug-in Management non e' installato o non e' configurato " +
                                "(Project Settings > XR Plug-in Management > spunta OpenXR). Vedi docs/06-vr.md.");
                yield break;
            }

            Type managerType = manager.GetType();
            object loader = managerType.GetProperty("activeLoader")?.GetValue(manager);

            if (loader == null)
            {
                MethodInfo initialize = managerType.GetMethod("InitializeLoader", Type.EmptyTypes);
                if (initialize == null)
                {
                    onError?.Invoke("XRManagerSettings.InitializeLoader non trovato: versione di XR Plug-in Management inattesa.");
                    yield break;
                }

                // E' una coroutine: la eseguiamo annidata dentro la nostra.
                if (initialize.Invoke(manager, null) is IEnumerator routine)
                {
                    yield return routine;
                }

                loader = managerType.GetProperty("activeLoader")?.GetValue(manager);
            }

            if (loader == null)
            {
                onError?.Invoke("Nessun loader XR si e' inizializzato. Il visore e' collegato e il runtime OpenXR " +
                                "(Oculus/Meta, SteamVR, WMR) e' attivo e impostato come predefinito?");
                yield break;
            }

            managerType.GetMethod("StartSubsystems", Type.EmptyTypes)?.Invoke(manager, null);
        }

        /// <summary>Ferma i sottosistemi e deinizializza il loader XR.</summary>
        public static void Stop()
        {
            object manager = GetManager();
            if (manager == null) return;

            Type managerType = manager.GetType();
            if (managerType.GetProperty("activeLoader")?.GetValue(manager) == null) return;

            managerType.GetMethod("StopSubsystems", Type.EmptyTypes)?.Invoke(manager, null);
            managerType.GetMethod("DeinitializeLoader", Type.EmptyTypes)?.Invoke(manager, null);
        }

        private static object GetManager()
        {
            if (!s_typeResolved)
            {
                s_typeResolved = true;
                s_generalSettingsType = FindType("UnityEngine.XR.Management.XRGeneralSettings");
            }
            if (s_generalSettingsType == null) return null;

            object settings = s_generalSettingsType
                .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?
                .GetValue(null);
            if (settings == null) return null;

            return s_generalSettingsType.GetProperty("Manager")?.GetValue(settings);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }
    }

    /// <summary>
    /// Input "vecchio" (Input Manager) che non fa esplodere tutto se il progetto
    /// e' passato al solo Input System.
    ///
    /// Installando OpenXR arriva anche il pacchetto Input System, e Unity chiede
    /// quale backend di input usare. Se si sceglie solo "Input System Package
    /// (New)", ogni chiamata a UnityEngine.Input lancia un'eccezione a OGNI
    /// frame. La scelta giusta e' "Both"; se non e' stata fatta, qui ce ne
    /// accorgiamo una volta sola, disattiviamo le scorciatoie da tastiera e lo
    /// diciamo in HUD invece di riempire la console.
    /// </summary>
    public static class SafeInput
    {
        public static bool Available { get; private set; } = true;
        public const string Hint = "Input Manager disattivato: Project Settings > Player > Active Input Handling = Both.";

        public static bool GetKeyDown(KeyCode key)
        {
            if (!Available) return false;
            try { return Input.GetKeyDown(key); }
            catch (InvalidOperationException) { Available = false; Debug.LogWarning("[GpuShare] " + Hint); return false; }
        }

        public static float GetAxis(string axis)
        {
            if (!Available) return 0.0f;
            try { return Input.GetAxis(axis); }
            catch (InvalidOperationException) { Available = false; Debug.LogWarning("[GpuShare] " + Hint); return 0.0f; }
            catch (ArgumentException) { return 0.0f; }   // asse non definito nell'Input Manager
        }
    }
}
