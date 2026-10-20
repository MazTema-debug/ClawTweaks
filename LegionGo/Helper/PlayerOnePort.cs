// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace LegionTweaks
{
    // Inspection and virtual-output reservation only. No PNP/HID/firmware setters.
    internal sealed class PlayerOnePort : IPlayerOnePort
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly ManualResetEvent stop;
        private readonly Action connect;
        private readonly Func<int?> readSlot;
        private readonly Action checkWatchdog;
        private readonly bool automatic;
        private readonly bool modeBased;
        internal PlayerOnePort(ManualResetEvent stop, Action connect, Func<int?> readSlot, Action checkWatchdog, bool automatic = false, bool modeBased = false)
        { this.stop = stop; this.connect = connect; this.readSlot = readSlot; this.checkWatchdog = checkWatchdog; this.automatic = automatic; this.modeBased = modeBased; }
        public long ElapsedMilliseconds => clock.ElapsedMilliseconds;
        public bool Canceled => stop.WaitOne(0);
        internal static bool IsPhysicalEndpoint(string instance) => instance != null
            && Regex.IsMatch(instance, @"^USB\\VID_17EF&PID_(6182|61EB)&MI_00\\[^\\]+$", RegexOptions.IgnoreCase);
        internal static void EnsureApplicationsClosed()
        {
            foreach (string name in new[] { "Diablo IV", "DiabloIV", "steam" })
            {
                var processes = Process.GetProcessesByName(name);
                bool running = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (running) throw new InvalidOperationException("Cierra Diablo IV y sal de Steam antes de preparar el jugador 1.");
            }
        }
        public PlayerOneEnvironment Inspect()
        {
            CheckBudget();
            CheckApplications(); checkWatchdog();
            var devices = ControllerDevices.XusbInstances();
            int physical = devices.Count(IsPhysicalEndpoint);
            var connected = XInputSlotProbe.Read(stop, CheckBudget);
            return new PlayerOneEnvironment { ConnectedSlots = connected, PhysicalDevices = physical, OtherDevices = devices.Length - physical };
        }
        public void ConnectVirtual()
        {
            CheckBudget();
            CheckApplications(); checkWatchdog();
            if (Canceled) throw new OperationCanceledException(automatic ? "Preparación cancelada. Se recuperará el mando físico." : "Preparación cancelada. Devuelve FPS a OFF.");
            connect();
            CheckBudget();
        }
        public int? ReadVirtualSlot() { CheckBudget(); return readSlot(); }
        private void CheckApplications() { if (modeBased) PlayerOneApplications.EnsureModeAllowed(); else EnsureApplicationsClosed(); }
        internal void CheckBudget()
        {
            if (Canceled) throw new OperationCanceledException(automatic ? "Preparación cancelada. Se recuperará el mando físico." : "Preparación cancelada. Devuelve FPS a OFF.");
            if (clock.ElapsedMilliseconds >= (automatic ? 45000 : 90000)) throw new TimeoutException(automatic
                ? "Se agotó el plazo de reconexión. Se recuperará el mando físico."
                : "Se agotaron los 90 segundos de preparación. Devuelve FPS a OFF.");
        }
        public void Wait(int milliseconds) { stop.WaitOne(milliseconds); }
    }
}
