// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LegionTweaks
{
    internal interface IFpsKeyOutput
    {
        MouseDesktop Inspect();
        bool Pulse(int key, MouseDesktop expected);
        bool ReleaseOwned();
    }

    // Paired pulses from an explicit allowlist. No user-key releases.
    internal sealed class FpsKeyOutput : IFpsKeyOutput
    {
        private readonly Func<MouseDesktop> inspect;
        private readonly Func<int, bool> isDown;
        private readonly Func<GameBarShortcut.Input[], uint> send;
        private readonly Func<int, bool> allowed;
        private int owned;

        internal FpsKeyOutput() : this(new MouseOutput().Inspect,
            key => (GetAsyncKeyState(key) & 0x8000) != 0,
            keys => SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(GameBarShortcut.Input)))) { }
        internal FpsKeyOutput(Func<int, bool> allowed) : this(new MouseOutput().Inspect,
            key => (GetAsyncKeyState(key) & 0x8000) != 0,
            keys => SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(GameBarShortcut.Input))), allowed) { }
        internal FpsKeyOutput(Func<MouseDesktop> inspect, Func<int, bool> isDown, Func<GameBarShortcut.Input[], uint> send)
            : this(inspect, isDown, send, key => key == 0x2d || key == 0x1b) { }
        internal FpsKeyOutput(Func<MouseDesktop> inspect, Func<int, bool> isDown, Func<GameBarShortcut.Input[], uint> send, Func<int, bool> allowed)
        { this.inspect = inspect; this.isDown = isDown; this.send = send; this.allowed = allowed ?? throw new ArgumentNullException(nameof(allowed)); }

        public MouseDesktop Inspect()
        {
            var value = inspect();
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c })
                if (isDown(key)) value.Available = false;
            return value;
        }
        public bool Pulse(int key, MouseDesktop expected)
        {
            if (!allowed(key)) throw new ArgumentOutOfRangeException(nameof(key));
            bool result = false;
            GameBarShortcut.WithKeyboardGate(() => {
                if (!ReleaseOwned()) throw new InvalidOperationException("Queda una tecla FPS pendiente de liberar.");
                var clock = Stopwatch.StartNew();
                var current = Inspect();
                if (!expected.Available || expected.Foreground == 0 || !current.Available || current.Foreground != expected.Foreground
                    || isDown(key) || clock.ElapsedMilliseconds > 100) return;
                uint accepted = send(new[] { Key(key, false), Key(key, true) });
                if (accepted == 2) { result = true; return; }
                if (accepted == 1) { owned = key; ReleaseOwned(); }
                throw new Win32Exception("Windows no confirmó la pulsación FPS completa."
                    + (owned == 0 ? "" : " Queda pendiente liberar la tecla propia " + owned + "."));
            });
            return result;
        }
        public bool ReleaseOwned()
        {
            if (owned == 0) return true;
            for (int i = 0; i < 3; i++)
                if (send(new[] { Key(owned, true) }) == 1) { owned = 0; return true; }
            return false;
        }
        private static GameBarShortcut.Input Key(int key, bool up) =>
            new GameBarShortcut.Input { Type = 1, Key = (ushort)key, Flags = (up ? 2u : 0u) | (key == 0x2d ? 1u : 0u) };
        [DllImport("user32.dll", ExactSpelling = true)] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern uint SendInput(uint count, GameBarShortcut.Input[] inputs, int size);
    }
}
