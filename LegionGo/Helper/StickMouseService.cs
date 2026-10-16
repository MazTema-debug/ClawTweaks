// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;

namespace LegionTweaks
{
    // Called by the existing controller worker, never opens a physical reader.
    internal sealed class StickMouseService
    {
        private readonly object gate = new object();
        private readonly StickMouse motion = new StickMouse();
        private readonly IMouseOutput output;
        private readonly Func<long> milliseconds;
        private bool available;
        private long? lastSample;
        private string message = "Mouse con stick derecho desactivado.", error;

        internal StickMouseService() : this(new MouseOutput(), Clock()) { }
        internal StickMouseService(IMouseOutput output, Func<long> milliseconds)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.milliseconds = milliseconds ?? throw new ArgumentNullException(nameof(milliseconds));
        }
        private static Func<long> Clock()
        {
            var clock = Stopwatch.StartNew(); return () => clock.ElapsedMilliseconds;
        }

        internal void Configure(bool enabled, int sensitivity, int deadzone)
        {
            lock (gate)
            {
                // Validate without changing an existing mode on a rejected request.
                var validated = new StickMouse(); validated.Configure(enabled, sensitivity, deadzone);
                if (enabled && !available) throw new InvalidOperationException("Activa primero el Xbox virtual para usar el mouse con stick derecho.");
                motion.Configure(enabled, sensitivity, deadzone);
                lastSample = null; error = null;
                message = enabled ? "Mouse habilitado. Centra el stick derecho para empezar." : "Mouse con stick derecho desactivado.";
            }
        }

        internal void SetAvailable(bool value)
        {
            lock (gate)
            {
                available = value;
                if (!value)
                {
                    motion.Configure(false, motion.Sensitivity, motion.Deadzone); lastSample = null;
                    message = "Mouse desactivado. Requiere el Xbox virtual activo.";
                }
            }
        }

        internal void Disable()
        {
            lock (gate)
            {
                motion.Configure(false, motion.Sensitivity, motion.Deadzone); lastSample = null;
                message = "Mouse con stick derecho desactivado.";
            }
        }

        internal StickMouseStatus GetStatus()
        {
            lock (gate) return new StickMouseStatus { Supports = true, Available = available, Enabled = motion.Enabled,
                Active = motion.Active, WaitingForNeutral = motion.WaitingForNeutral, Sensitivity = motion.Sensitivity,
                Deadzone = motion.Deadzone, Message = message, Error = error };
        }

        internal GamepadFrame Process(GamepadFrame input)
        {
            lock (gate)
            {
                if (!available || !motion.Enabled) return input;
                var consumed = input; consumed.RX = consumed.RY = 0;
                try
                {
                    long now = milliseconds();
                    // At most ~60 native inspections/emissions per second, independent
                    // of the controller's faster polling. Every virtual frame still
                    // consumes the right stick while the mouse mode is enabled.
                    if (lastSample.HasValue && now >= lastSample.Value && now - lastSample.Value < 16) return consumed;
                    var desktop = output.Inspect();
                    long inspectedAt = milliseconds();
                    lastSample = inspectedAt;
                    // The source frame predates the native desktop query. A slow
                    // query must not turn that old frame into a fresh mouse sample.
                    if (inspectedAt < now || inspectedAt - now > StickMouse.MaximumSampleGapMilliseconds)
                    {
                        motion.RequireNeutral();
                        message = "Lectura del mouse descartada por demora. Centra el stick derecho para continuar.";
                        return consumed;
                    }
                    var step = motion.Process(input, inspectedAt, desktop.Available, desktop.Foreground);
                    if ((step.DeltaX != 0 || step.DeltaY != 0) && !output.Move(step.DeltaX, step.DeltaY, desktop))
                    {
                        motion.RequireNeutral();
                        message = "Cambió el foco. Centra el stick derecho para continuar.";
                    }
                    else message = !desktop.Available ? "Mouse en pausa: desbloquea Windows y centra el stick derecho."
                        : motion.WaitingForNeutral ? "Centra el stick derecho para continuar con el mouse."
                        : "Mouse activo con el stick derecho. Desactívalo antes de usar ese stick en un juego.";
                    return step.ControllerInput;
                }
                catch (Exception ex)
                {
                    motion.Configure(false, motion.Sensitivity, motion.Deadzone); lastSample = null;
                    error = ex.Message; message = "Mouse desactivado por un error. El mando virtual sigue disponible.";
                    // A failing native send may already have moved the cursor. Keep
                    // this frame consumed; normal game input resumes on the next one.
                    return consumed;
                }
            }
        }
    }
}
