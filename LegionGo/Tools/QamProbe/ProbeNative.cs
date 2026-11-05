// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LegionTweaks.QamProbe
{
    internal static class ProbeNative
    {
        internal const int HotKeyId = 1;

        internal static uint WindowProcess(IntPtr window)
        {
            uint process;
            return window != IntPtr.Zero && GetWindowThreadProcessId(window, out process) != 0 ? process : 0;
        }

        internal static bool InteractiveDesktop()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero) return false;
            try {
                var name = new StringBuilder(128);
                return GetUserObjectInformationW(desktop, 2, name, name.Capacity * sizeof(char), out int _)
                    && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(desktop); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            internal int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRectangle Monitor, Work;
            internal uint Flags;
        }

        internal static Rectangle MonitorBounds(IntPtr window)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfoW(MonitorFromWindow(window, 2), ref info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo consultar el monitor del juego.");
            return Rectangle.FromLTRB(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
        }

        // EnumDisplaySettingsEx returns the current Windows mode of each active display.
        // Keep the DEVMODEW layout explicit so this probe only imports the read operation.
        [StructLayout(LayoutKind.Explicit, Size = 220)]
        private struct Mode
        {
            [FieldOffset(68)] internal ushort Size;
            [FieldOffset(72)] internal uint Fields;
            [FieldOffset(84)] internal uint Orientation;
            [FieldOffset(172)] internal uint Width;
            [FieldOffset(176)] internal uint Height;
            [FieldOffset(184)] internal uint Frequency;
        }

        internal static string DisplayModes()
        {
            return string.Join(" | ", Screen.AllScreens.OrderBy(screen => screen.DeviceName, StringComparer.Ordinal).Select(screen => {
                var mode = new Mode { Size = (ushort)Marshal.SizeOf(typeof(Mode)) };
                if (!EnumDisplaySettingsExW(screen.DeviceName, -1, ref mode, 0) || (mode.Fields & 0x00180000) != 0x00180000)
                    return screen.DeviceName + " · modo sin confirmar";
                string hz = (mode.Fields & 0x00400000) != 0 && mode.Frequency > 1 ? mode.Frequency + " Hz" : "Hz sin confirmar";
                string rotation = (mode.Fields & 0x80) != 0 && mode.Orientation <= 3 ? " · giro " + mode.Orientation * 90 + "°" : "";
                return screen.DeviceName + " · " + mode.Width + "×" + mode.Height + " · " + hz + rotation;
            }));
        }

        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, int bytes, out int needed);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool EnumDisplaySettingsExW(string device, int mode, ref Mode result, uint flags);
    }
}
