// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LegionTweaks
{
    internal struct MouseDesktop
    {
        internal bool Available;
        internal long Foreground;
    }

    internal interface IMouseOutput
    {
        MouseDesktop Inspect();
        bool Move(int x, int y, MouseDesktop expected);
    }

    // Only relative cursor motion. No buttons, wheel, foreground changes, desktop
    // switching, hooks or device readers. The helper targets Windows x64 only.
    internal sealed class MouseOutput : IMouseOutput
    {
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        internal struct NativeInput
        {
            [FieldOffset(0)] internal uint Type;
            [FieldOffset(8)] internal int X;
            [FieldOffset(12)] internal int Y;
            [FieldOffset(16)] internal uint MouseData;
            [FieldOffset(20)] internal uint Flags;
            [FieldOffset(24)] internal uint Time;
            [FieldOffset(32)] internal UIntPtr Extra;
        }

        internal static NativeInput CreateMove(int x, int y) => new NativeInput { Type = 0, X = x, Y = y, Flags = 0x0001 };

        public MouseDesktop Inspect()
        {
            IntPtr desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
            if (desktop == IntPtr.Zero) return new MouseDesktop();
            try
            {
                var name = new StringBuilder(128); int needed;
                if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed)
                    || !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                    return new MouseDesktop();
                IntPtr foreground = GetForegroundWindow();
                return new MouseDesktop { Available = foreground != IntPtr.Zero, Foreground = foreground.ToInt64() };
            }
            finally { CloseDesktop(desktop); }
        }

        public bool Move(int x, int y, MouseDesktop expected)
        {
            if (x == 0 && y == 0) return true;
            // Check again just before SendInput: a previously valid observation is
            // not authority to send movement to another window or secure desktop.
            var check = Stopwatch.StartNew();
            var current = Inspect();
            if (!expected.Available || !current.Available || current.Foreground != expected.Foreground
                || check.ElapsedMilliseconds > StickMouse.MaximumSampleGapMilliseconds) return false;
            var inputs = new[] { CreateMove(x, y) };
            if (check.ElapsedMilliseconds > StickMouse.MaximumSampleGapMilliseconds) return false;
            if (SendInput(1, inputs, Marshal.SizeOf(typeof(NativeInput))) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows no aceptó el movimiento del mouse; el modo se desactivó.");
            return true;
        }

        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out int needed);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool CloseDesktop(IntPtr desktop);
    }
}
