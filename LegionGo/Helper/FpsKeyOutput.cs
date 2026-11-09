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
    internal interface IFpsGuardedKeyOutput : IFpsKeyOutput
    {
        bool Pulse(int key, MouseDesktop expected, Func<bool> canSend);
        string LastPulseDecision { get; }
        int LastAcceptedEvents { get; }
    }

    // Paired pulses from an explicit allowlist. No user-key releases.
    internal sealed class FpsKeyOutput : IFpsGuardedKeyOutput
    {
        private readonly Func<MouseDesktop> inspect;
        private readonly Func<int, bool> isDown;
        private readonly Func<GameBarShortcut.Input[], uint> send;
        private readonly Func<int, bool> allowed;
        private int owned;
        public string LastPulseDecision { get; private set; }
        public int LastAcceptedEvents { get; private set; } = -1;

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
        public bool Pulse(int key, MouseDesktop expected) => Pulse(key, expected, null);
        public bool Pulse(int key, MouseDesktop expected, Func<bool> canSend)
        {
            if (!allowed(key)) throw new ArgumentOutOfRangeException(nameof(key));
            LastPulseDecision = "not_sent"; LastAcceptedEvents = -1;
            bool result = false;
            GameBarShortcut.WithKeyboardGate(() => {
                if (!ReleaseOwned()) throw new InvalidOperationException("Queda una tecla FPS pendiente de liberar.");
                var clock = Stopwatch.StartNew();
                var current = Inspect();
                if (!expected.Available || expected.Foreground == 0 || !current.Available)
                { LastPulseDecision = "desktop_or_modifier"; return; }
                if (current.Foreground != expected.Foreground) { LastPulseDecision = "focus_changed"; return; }
                if (isDown(key)) { LastPulseDecision = "key_already_down"; return; }
                if (clock.ElapsedMilliseconds > 100) { LastPulseDecision = "output_check_timeout"; return; }
                // Recheck the original edge immediately before SendInput, after
                // waiting for the keyboard gate and checking the foreground.
                if (canSend != null && !canSend()) { LastPulseDecision = "edge_expired"; return; }
                uint accepted = send(new[] { Key(key, false), Key(key, true) });
                LastAcceptedEvents = (int)accepted;
                if (accepted == 2) { LastPulseDecision = "pair_accepted"; result = true; return; }
                LastPulseDecision = accepted == 1 ? "partial_send" : "send_rejected";
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
