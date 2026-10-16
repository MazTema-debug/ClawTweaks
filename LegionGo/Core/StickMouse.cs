// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    public interface IStickMouseControllerService
    {
        void SetMouse(bool enabled, int sensitivity, int deadzone);
        StickMouseStatus GetMouseStatus();
    }

    [DataContract]
    public sealed class StickMouseStatus
    {
        [DataMember] public bool Supports { get; set; }
        [DataMember] public bool Available { get; set; }
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public bool WaitingForNeutral { get; set; }
        [DataMember] public int Sensitivity { get; set; }
        [DataMember] public int Deadzone { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
    }

    public struct StickMouseStep
    {
        public GamepadFrame ControllerInput;
        public int DeltaX, DeltaY;
    }

    // Converts fresh physical frames into relative movement, without reading devices
    // or sending Windows input. The original right stick is consumed before layouts
    // can swap/invert it. Enabling and context changes require a neutral observation.
    public sealed class StickMouse
    {
        public const int MinimumSensitivity = 1, MaximumSensitivity = 20, DefaultSensitivity = 8;
        public const int MinimumDeadzone = 5, MaximumDeadzone = 30, DefaultDeadzone = 15;
        public const int MaximumSampleGapMilliseconds = 100;
        public bool Enabled { get; private set; }
        public bool Active { get; private set; }
        public bool WaitingForNeutral => Enabled && !Active;
        public int Sensitivity { get; private set; } = DefaultSensitivity;
        public int Deadzone { get; private set; } = DefaultDeadzone;
        private long? previousMilliseconds;
        private long foreground;
        private double residualX, residualY;

        public void Configure(bool enabled, int sensitivity, int deadzone)
        {
            if (sensitivity < MinimumSensitivity || sensitivity > MaximumSensitivity
                || deadzone < MinimumDeadzone || deadzone > MaximumDeadzone)
                throw new ArgumentException("Sensibilidad del mouse: 1–20. Zona muerta: 5–30 %. ");
            Sensitivity = sensitivity; Deadzone = deadzone; Enabled = enabled;
            RequireNeutral();
        }

        public void RequireNeutral()
        {
            Active = false; previousMilliseconds = null; foreground = 0;
            residualX = residualY = 0;
        }

        public StickMouseStep Process(GamepadFrame input, long elapsedMilliseconds, bool desktopAvailable, long foregroundWindow)
        {
            var step = new StickMouseStep { ControllerInput = input };
            if (!Enabled) return step;
            step.ControllerInput.RX = step.ControllerInput.RY = 0;
            if (!desktopAvailable || foregroundWindow == 0 || elapsedMilliseconds < 0)
            { RequireNeutral(); return step; }

            double x = input.RX / (input.RX < 0 ? 32768.0 : 32767.0);
            double y = input.RY / (input.RY < 0 ? 32768.0 : 32767.0);
            double magnitude = Math.Sqrt(x * x + y * y), limit = Deadzone / 100.0;
            bool neutral = magnitude <= limit;
            bool contextChanged = foreground != foregroundWindow || !previousMilliseconds.HasValue
                || elapsedMilliseconds < previousMilliseconds.Value
                || elapsedMilliseconds - previousMilliseconds.Value > MaximumSampleGapMilliseconds;
            if (contextChanged)
            {
                RequireNeutral(); foreground = foregroundWindow; previousMilliseconds = elapsedMilliseconds;
                if (neutral) Active = true;
                return step;
            }
            long delta = elapsedMilliseconds - previousMilliseconds.Value;
            previousMilliseconds = elapsedMilliseconds;
            if (!Active)
            {
                if (neutral) Active = true;
                return step;
            }
            if (neutral)
            { residualX = residualY = 0; return step; }
            if (delta == 0) return step;

            double amount = (Math.Min(1, magnitude) - limit) / (1 - limit);
            // A quadratic response keeps fine movement near the deadzone. The bound
            // is 100..2000 relative units/s before Windows pointer speed/acceleration.
            double distance = amount * amount * Sensitivity * 100.0 * delta / 1000;
            residualX += x / magnitude * distance;
            residualY -= y / magnitude * distance; // Gamepad +Y is up; Windows +Y is down.
            step.DeltaX = (int)residualX; step.DeltaY = (int)residualY;
            residualX -= step.DeltaX; residualY -= step.DeltaY;
            return step;
        }
    }
}
