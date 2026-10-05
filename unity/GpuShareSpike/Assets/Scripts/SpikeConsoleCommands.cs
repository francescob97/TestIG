// ============================================================================
//  Comandi di console dello spike. Servono a due cose: comodita' durante le
//  misure, ed esempio di come si aggiunge un comando con [ConsoleCommand].
//
//  Gli stessi effetti si ottengono anche con i comandi generici, es.
//      set GpuShareClient ShareClient DebugMode 1
//  ma un comando dedicato si ricorda meglio e si completa con Tab.
// ============================================================================

using DevTools;
using UnityEngine;

namespace GpuShareSpike
{
    public static class SpikeConsoleCommands
    {
        [ConsoleCommand("gs.hud", "mostra/nasconde l'HUD delle metriche (come F1)")]
        private static void ToggleHud()
        {
            foreach (HudOverlay hud in Find<HudOverlay>())
            {
                hud.Visible = !hud.Visible;
                DevConsole.Log($"HUD {(hud.Visible ? "visibile" : "nascosto")}");
            }
        }

        [ConsoleCommand("gs.view", "vista: 0 colore, 1 depth, 2 zoom sul marker. Senza argomento dice quella attuale")]
        private static void View(int mode = -1)
        {
            foreach (ShareClient client in Find<ShareClient>())
            {
                if (mode >= 0) client.DebugMode = Mathf.Clamp(mode, 0, 2);
                DevConsole.Log($"vista = {client.DebugMode}");
            }
        }

        // Parametro enum: Tab completa i valori possibili.
        [ConsoleCommand("gs.cam", "modalita' del driver della camera")]
        private static void CameraMode(VirtualCameraDriver.DriveMode mode)
        {
            foreach (VirtualCameraDriver driver in Find<VirtualCameraDriver>())
            {
                driver.Mode = mode;
                DevConsole.Log($"camera: {driver.Mode}");
            }
        }

        private static T[] Find<T>() where T : Component
        {
            var found = ConsoleUtil.FindActiveComponents(typeof(T)).ConvertAll(c => (T)c).ToArray();
            if (found.Length == 0) throw new ConsoleException($"nessun {typeof(T).Name} attivo in scena");
            return found;
        }
    }
}
