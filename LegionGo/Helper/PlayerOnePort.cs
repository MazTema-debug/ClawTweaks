// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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
        internal PlayerOnePort(ManualResetEvent stop, Action connect, Func<int?> readSlot, Action checkWatchdog)
        { this.stop = stop; this.connect = connect; this.readSlot = readSlot; this.checkWatchdog = checkWatchdog; }
        public long ElapsedMilliseconds => clock.ElapsedMilliseconds;
        public bool Canceled => stop.WaitOne(0);
        internal static bool IsPhysicalEndpoint(string instance) => instance != null
            && Regex.IsMatch(instance, @"^USB\\VID_17EF&PID_(6182|61EB)&MI_00\\[^\\]+$", RegexOptions.IgnoreCase);
        internal static void EnsureApplicationsClosed()
        {
            foreach (string name in new[] { "Diablo IV", "steam" })
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
            EnsureApplicationsClosed(); checkWatchdog();
            var devices = ControllerDevices.XusbInstances();
            int physical = devices.Count(IsPhysicalEndpoint);
            var connected = new bool[4];
            for (uint i = 0; i < 4; i++)
            {
                NativeState state; uint result = XInputGetState(i, out state);
                if (result != 0 && result != 1167) throw new Win32Exception((int)result);
                connected[i] = result == 0;
            }
            return new PlayerOneEnvironment { ConnectedSlots = connected, PhysicalDevices = physical, OtherDevices = devices.Length - physical };
        }
        public void ConnectVirtual()
        {
            CheckBudget();
            EnsureApplicationsClosed(); checkWatchdog();
            if (Canceled) throw new OperationCanceledException("Preparación cancelada. Devuelve FPS a OFF.");
            connect();
            CheckBudget();
        }
        public int? ReadVirtualSlot() { CheckBudget(); return readSlot(); }
        internal void CheckBudget()
        {
            if (Canceled) throw new OperationCanceledException("Preparación cancelada. Devuelve FPS a OFF.");
            if (clock.ElapsedMilliseconds >= 90000) throw new TimeoutException("Se agotaron los 90 segundos de preparación. Devuelve FPS a OFF.");
        }
        public void Wait(int milliseconds) { stop.WaitOne(milliseconds); }
        [StructLayout(LayoutKind.Sequential)] private struct Pad
        { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeState { public uint Packet; public Pad Pad; }
        [DllImport("xinput1_4.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint XInputGetState(uint index, out NativeState state);
    }
}
