// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    internal static class GameBarShortcut
    {
        private static readonly object KeyboardGate = new object();
        // INPUT is 40 bytes on Windows x64 (the only helper target).
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        internal struct Input
        {
            [FieldOffset(0)] internal uint Type;
            [FieldOffset(8)] internal ushort Key;
            [FieldOffset(10)] internal ushort Scan;
            [FieldOffset(12)] internal uint Flags;
            [FieldOffset(16)] internal uint Time;
            [FieldOffset(24)] internal IntPtr Extra;
        }
        internal static void Toggle()
        {
            WithKeyboardGate(ToggleCore);
        }
        internal static void WithKeyboardGate(Action action)
        {
            // The Legion reader calls Toggle while holding its own state lock.
            // Never make that input reader wait behind the OptiScaler operation.
            if (!Monitor.TryEnter(KeyboardGate))
                throw new InvalidOperationException("Hay un atajo de teclado en curso. Suelta el botón y vuelve a intentarlo.");
            try { action(); }
            finally { Monitor.Exit(KeyboardGate); }
        }
        private static void ToggleCore()
        {
            // Do not deliver keys on the lock/UAC desktop or release keys the user holds.
            IntPtr desktop = OpenInputDesktop(0, false, 1);
            if (desktop == IntPtr.Zero) throw new InvalidOperationException("Game Bar no está disponible en este escritorio.");
            try
            {
                var name = new StringBuilder(128); int needed;
                if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed)
                    || !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Desbloquea Windows antes de abrir Game Bar.");
            }
            finally { CloseDesktop(desktop); }
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x47 })
                if ((GetAsyncKeyState(key) & 0x8000) != 0)
                    throw new InvalidOperationException("Suelta las teclas modificadoras y vuelve a pulsar el botón Legion.");
            var inputs = new[] { Key(0x5b, false), Key(0x47, false), Key(0x47, true), Key(0x5b, true) };
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input)));
            if (sent != inputs.Length)
            {
                int error = Marshal.GetLastWin32Error();
                if (sent > 0) SendInput(2, new[] { Key(0x47, true), Key(0x5b, true) }, Marshal.SizeOf(typeof(Input)));
                throw new Win32Exception(error, "Windows no aceptó el atajo Win+G.");
            }
        }
        private static Input Key(ushort key, bool up) => new Input { Type = 1, Key = key, Flags = up ? 2u : 0u };
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out int needed);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    }
}
