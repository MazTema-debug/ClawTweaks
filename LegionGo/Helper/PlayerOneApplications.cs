// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;

namespace LegionTweaks
{
    internal sealed class ControllerApplication
    {
        internal string Name;
        internal string Executable;
    }

    // Steam's client is allowed by the mode route; a running game is not.
    // Executable paths identify Steam-library processes, not every possible game.
    // Slot/device/identity checks remain mandatory independently of this guard.
    internal static class PlayerOneApplications
    {
        internal static void EnsureModeAllowed() => Check(Snapshot());
        internal static void Check(IEnumerable<ControllerApplication> applications)
        {
            if (applications == null) throw new InvalidOperationException("No se pudieron consultar las aplicaciones antes de preparar el mando.");
            foreach (var application in applications)
            {
                if (application == null) throw new InvalidOperationException("La consulta de aplicaciones está incompleta.");
                string name = application.Name ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
                bool knownGame = string.Equals(name, "Diablo IV", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "DiabloIV", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Dungeons", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Minecraft.Windows", StringComparison.OrdinalIgnoreCase);
                string path = (application.Executable ?? "").Replace('/', '\\');
                // A library can also contain desktop utilities. Recognize the
                // observed Lossless Scaling executable by both name and folder;
                // a matching process name alone must not exempt another title.
                bool scalingUtility = string.Equals(name, "LosslessScaling", StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(@"\steamapps\common\Lossless Scaling\LosslessScaling.exe", StringComparison.OrdinalIgnoreCase);
                bool steamLibrary = path.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase) >= 0 && !scalingUtility;
                if (knownGame || steamLibrary)
                    throw new InvalidOperationException(knownGame
                        ? "Cierra el juego antes de preparar el jugador 1. Steam puede permanecer abierto con el método automático."
                        : "Cierra el juego o la aplicación de la biblioteca de Steam antes de preparar el jugador 1. El cliente Steam puede permanecer abierto.");
            }
        }

        private static IEnumerable<ControllerApplication> Snapshot()
        {
            // Called only during bounded preparation, never by the frame loop,
            // telemetry/status polling or a background retry timer.
            var result = new List<ControllerApplication>();
            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    string name;
                    try { name = process.ProcessName; }
                    catch (InvalidOperationException) { continue; } // Exited during enumeration.
                    string path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Win32Exception) { } // Protected processes can deny image inspection.
                    catch (InvalidOperationException) { }
                    result.Add(new ControllerApplication { Name = name, Executable = path });
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
            return result;
        }
    }
}
