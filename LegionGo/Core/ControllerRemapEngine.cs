// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;

namespace LegionTweaks
{
    // Complete normalized snapshot. Omitted lanes are zero. Relative lanes
    // contain positive counts consumed exactly once per source sequence.
    public sealed class RemapInputFrame
    {
        public RemapMode Mode { get; set; }
        public long Sequence { get; set; }
        public long CapturedAtMilliseconds { get; set; }
        public Dictionary<RemapInput, double> Values { get; set; } = new Dictionary<RemapInput, double>();
        internal bool Valid()
        {
            return Enum.IsDefined(typeof(RemapMode), Mode) && Sequence >= 0 && CapturedAtMilliseconds >= 0
                && Values != null && Values.Count <= RemapCatalog.Inputs.Length
                && Values.All(v => Enum.IsDefined(typeof(RemapInput), v.Key) && !double.IsNaN(v.Value)
                    && !double.IsInfinity(v.Value) && v.Value >= 0 && v.Value <= (RemapCatalog.IsRelative(v.Key) ? 10000 : 1)
                    && (RemapCatalog.IsRelative(v.Key) || RemapCatalog.IsAnalog(v.Key) || v.Value == 0 || v.Value == 1));
        }
        internal double Value(RemapInput input) { double value; return Values.TryGetValue(input, out value) ? value : 0; }
    }

    // Desired OWNED output, not a claim that SendInput/XInput/game accepted it.
    // The native port must diff held states, honor shared ownership, release in
    // reverse chord order and report partial sends before acknowledging output.
    public sealed class RemapOutputFrame
    {
        public int[] Keys { get; internal set; } = new int[0];
        public RemapMouseButton[] MouseButtons { get; internal set; } = new RemapMouseButton[0];
        public GamepadFrame PlayerOne { get; internal set; }
        public GamepadFrame PlayerTwo { get; internal set; }
        public double MouseX { get; internal set; }
        public double MouseY { get; internal set; }
        public int Wheel { get; internal set; }
        public RemapFunction[] Functions { get; internal set; } = new RemapFunction[0];
    }

    // Single-worker, platform-independent action logic. No native APIs, firmware,
    // automatic activation, power settings, HID reader or third-party runtime.
    public sealed class ControllerRemapEngine
    {
        public const int FreshMilliseconds = 250, TapMilliseconds = 30;
        private sealed class State
        {
            internal bool Down, Toggled;
            internal long TapUntil;
            internal double Strength;
        }
        private RemapProfile active;
        private State[] states = new State[0];
        private bool armed, paused;
        private long lastSequence = -1, lastCapture = -1, lastNow = -1, lastMotionNow = -1, foreground;
        public bool Active => active != null;
        public bool Paused => paused;
        public bool WaitingForNeutral => Active && !armed;
        public RemapProfile ActiveProfile => active?.Copy();

        public RemapOutputFrame Activate(RemapProfile saved, RemapCapabilities capabilities)
        {
            if (saved == null || capabilities == null) throw new ArgumentNullException();
            var copy = saved.Copy(); capabilities.Require(copy);
            // Validate before touching an existing active distribution.
            active = copy; states = copy.Bindings.Select(b => new State()).ToArray(); paused = false;
            ResetInput(); return new RemapOutputFrame();
        }
        public RemapOutputFrame Stop()
        {
            active = null; states = new State[0]; paused = false;
            ResetInput(); return new RemapOutputFrame();
        }
        public RemapOutputFrame Pause()
        { if (Active) paused = true; ResetInput(); return new RemapOutputFrame(); }
        public RemapOutputFrame Resume()
        { paused = false; ResetInput(); return new RemapOutputFrame(); }
        private void ResetInput()
        {
            armed = false; lastSequence = lastCapture = lastNow = lastMotionNow = -1; foreground = 0;
            foreach (var s in states) { s.Down = s.Toggled = false; s.TapUntil = 0; s.Strength = 0; }
        }

        // routeAvailable attests BOTH an ordinary input desktop and the continued
        // ownership/readiness of the native substitution/output route.
        public RemapOutputFrame Observe(RemapInputFrame frame, long now, bool routeAvailable, long window)
        {
            if (!Active || paused) return new RemapOutputFrame();
            if (frame != null && frame.Mode != active.Mode) return Stop();
            if (!routeAvailable || window == 0 || frame == null || !frame.Valid() || now < 0 || now > long.MaxValue - FreshMilliseconds
                || frame.CapturedAtMilliseconds > now || now - frame.CapturedAtMilliseconds > FreshMilliseconds)
            { ResetInput(); return new RemapOutputFrame(); }
            if (lastNow >= 0 && (now < lastNow || now - lastNow > FreshMilliseconds || foreground != window)) ResetInput();
            if (lastSequence >= 0 && (frame.Sequence < lastSequence || frame.CapturedAtMilliseconds < lastCapture
                || frame.Sequence == lastSequence && frame.CapturedAtMilliseconds != lastCapture))
            { ResetInput(); return new RemapOutputFrame(); }
            bool fresh = frame.Sequence != lastSequence;
            lastNow = now; foreground = window;
            if (!fresh) return Render(now, 0, false, new RemapFunction[0], 0);
            double elapsed = lastMotionNow < 0 ? 0 : Math.Min(50, now - lastMotionNow) / 1000.0;
            lastMotionNow = now;
            lastSequence = frame.Sequence; lastCapture = frame.CapturedAtMilliseconds;
            if (!armed)
            {
                if (frame.Values.All(v => v.Value <= 0.05)) armed = true;
                return new RemapOutputFrame();
            }
            var functions = new HashSet<RemapFunction>(); int wheel = 0;
            for (int i = 0; i < active.Bindings.Length; i++)
            {
                var b = active.Bindings[i]; var a = b.Action; var s = states[i]; double value = frame.Value(b.Input);
                bool continuous = a.Kind == RemapActionKind.MouseMove || a.Kind == RemapActionKind.GamepadAxis;
                bool down = RemapCatalog.IsAnalog(b.Input) && !continuous ? value >= b.Threshold / 100.0 : value > 0;
                bool rising = down && (!s.Down || RemapCatalog.IsRelative(b.Input));
                s.Down = down;
                if (b.Behavior == RemapBehavior.Toggle) { if (rising) { s.Toggled = !s.Toggled; s.Strength = 1; } }
                else s.Strength = value;
                if (rising && b.Behavior == RemapBehavior.Tap) s.TapUntil = now + TapMilliseconds;
                if (!rising) continue;
                if (a.Kind == RemapActionKind.Function) functions.Add(a.Function);
                if (a.Kind == RemapActionKind.MouseWheel)
                    wheel += a.Y * (RemapCatalog.IsRelative(b.Input) ? Math.Max(1, (int)Math.Round(value)) : 1);
            }
            if (functions.Contains(RemapFunction.ReturnOriginal))
            { var stopped = Stop(); stopped.Functions = functions.OrderBy(x => x).ToArray(); return stopped; }
            if (functions.Contains(RemapFunction.PauseRemap))
            { var stopped = Pause(); stopped.Functions = functions.OrderBy(x => x).ToArray(); return stopped; }
            return Render(now, elapsed, true, functions.OrderBy(x => x).ToArray(), wheel);
        }

        private RemapOutputFrame Render(long now, double elapsed, bool fresh, RemapFunction[] functions, int wheel)
        {
            var keys = new HashSet<int>(); var mouse = new HashSet<RemapMouseButton>();
            var buttons = new ushort[2]; var axes = new double[2, 6]; double mx = 0, my = 0;
            for (int i = 0; i < active.Bindings.Length; i++)
            {
                var b = active.Bindings[i]; var a = b.Action; var s = states[i];
                bool on = b.Behavior == RemapBehavior.Hold ? s.Down : b.Behavior == RemapBehavior.Toggle ? s.Toggled : now < s.TapUntil;
                if (!on) continue;
                double strength = b.Behavior == RemapBehavior.Hold ? Math.Min(1, s.Strength) : 1;
                double amount = b.Amount / 100.0;
                switch (a.Kind)
                {
                    case RemapActionKind.Keyboard: foreach (int key in a.Keys) keys.Add(RemapCatalog.CanonicalKey(key)); break;
                    case RemapActionKind.MouseButton: mouse.Add(a.MouseButton); break;
                    case RemapActionKind.MouseMove:
                        // Relative counts are displacement; analog/button lanes
                        // are velocity, independent of polling frequency.
                        if (fresh)
                        {
                            double movement = amount * (RemapCatalog.IsRelative(b.Input) ? s.Strength : strength * 1000 * elapsed);
                            mx += a.X * movement; my += a.Y * movement;
                        }
                        break;
                    case RemapActionKind.GamepadButton: buttons[a.Player - 1] |= (ushort)a.Button; break;
                    case RemapActionKind.GamepadAxis: axes[a.Player - 1, (int)a.Axis - 1] += a.Direction * amount * strength; break;
                }
            }
            return new RemapOutputFrame {
                Keys = keys.OrderBy(k => RemapCatalog.IsModifier(k) ? 0 : 1).ThenBy(k => k).ToArray(),
                MouseButtons = mouse.OrderBy(k => k).ToArray(), MouseX = Clamp(mx, -10000, 10000), MouseY = Clamp(my, -10000, 10000),
                PlayerOne = Gamepad(buttons[0], axes, 0), PlayerTwo = Gamepad(buttons[1], axes, 1),
                Wheel = Math.Max(-10000, Math.Min(10000, wheel)), Functions = functions
            };
        }
        private static GamepadFrame Gamepad(ushort buttons, double[,] axes, int player)
        {
            if ((buttons & 3) == 3) buttons &= 0xfffc;
            if ((buttons & 12) == 12) buttons &= 0xfff3;
            return new GamepadFrame { Buttons = buttons, LX = Stick(axes[player, 0]), LY = Stick(axes[player, 1]),
                RX = Stick(axes[player, 2]), RY = Stick(axes[player, 3]),
                LT = (byte)Math.Round(Clamp(axes[player, 4], 0, 1) * 255), RT = (byte)Math.Round(Clamp(axes[player, 5], 0, 1) * 255) };
        }
        private static short Stick(double value) => (short)Math.Round(Clamp(value, -1, 1) * (value < 0 ? 32768 : 32767));
        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
    }
}
