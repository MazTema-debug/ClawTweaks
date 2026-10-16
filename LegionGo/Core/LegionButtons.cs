// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    // One shared vendor reader supplies both shortcuts and the virtual controller.
    public interface ILegionControllerButtons
    {
        void SetControllerInputEnabled(bool enabled);
        void ResetControllerInput();
        byte GetControllerButtons();
        void SetPreparationPaused(bool paused);
    }
    public interface ILegionButtonCapture
    {
        void StartCapture(int buttonIndex, int seconds);
        void CancelCapture();
    }

    // This cache has no I/O. Reading stale input disarms it, so a reconnect-held
    // button cannot become a virtual press until a fresh neutral report arrives.
    public sealed class LegionButtonInputGate
    {
        private bool armed;
        private byte buttons;
        private long lastReport = -1;
        public void Observe(int value, long milliseconds)
        {
            if ((value & ~3) != 0 || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (lastReport >= 0 && (milliseconds < lastReport || milliseconds - lastReport > 1000)) Reset();
            lastReport = milliseconds;
            if (value == 0) armed = true;
            buttons = armed ? (byte)value : (byte)0;
        }
        public byte Read(long milliseconds, LegionShortcutMode reserved)
        {
            int mask = ReservedButtons(reserved);
            if (lastReport < 0) return 0;
            if (milliseconds < lastReport || milliseconds - lastReport > 1000) { Reset(); return 0; }
            return (byte)(buttons & ~mask);
        }
        public static int ReservedButtons(LegionShortcutMode mode)
        {
            if (mode < LegionShortcutMode.Disabled || mode > LegionShortcutMode.Both)
                throw new ArgumentOutOfRangeException(nameof(mode));
            return (int)mode;
        }
        public void Reset() { armed = false; buttons = 0; lastReport = -1; }
    }

    [DataContract]
    public sealed class LegionButtonCaptureStatus
    {
        [DataMember] public bool Active { get; set; }
        [DataMember] public bool Completed { get; set; }
        [DataMember] public bool Cancelled { get; set; }
        [DataMember] public int ButtonIndex { get; set; }
        [DataMember] public int SecondsRemaining { get; set; }
        [DataMember] public string Header { get; set; }
        [DataMember] public int CandidateOffset { get; set; }
        [DataMember] public int BaselineBits { get; set; }
        [DataMember] public int CurrentBits { get; set; }
        [DataMember] public int ChangedBits { get; set; }
        [DataMember] public int PressedBits { get; set; }
        [DataMember] public int ReleasedBits { get; set; }
        [DataMember] public int Samples { get; set; }
        [DataMember] public int Transitions { get; set; }
        [DataMember] public string Message { get; set; }
    }

    // Deliberately records only candidate digital bits in two known formats.
    // Auxiliary masks are observations, not assignments to Y/M physical names.
    // No axes, IMU, keyboard data, raw packets or arbitrary strings are retained.
    public sealed class LegionButtonCaptureSession
    {
        private readonly LegionButtonCaptureStatus status;
        private readonly long started;
        private readonly int duration;
        private long lastTime;
        private int formatOffset = -1;
        public bool Active => status.Active;
        public LegionButtonCaptureSession(int buttonIndex, int seconds, long milliseconds)
        {
            if (buttonIndex < 0 || buttonIndex >= ControllerLayout.LegionLabels.Length)
                throw new ArgumentOutOfRangeException(nameof(buttonIndex));
            if (seconds < 1 || seconds > 30) throw new ArgumentOutOfRangeException(nameof(seconds));
            started = lastTime = milliseconds; duration = seconds * 1000;
            status = new LegionButtonCaptureStatus { Active = true, ButtonIndex = buttonIndex,
                SecondsRemaining = seconds, Message = "Deja todos los botones libres durante el primer segundo; después pulsa y suelta solo el botón elegido." };
        }
        public bool Tick(long milliseconds)
        {
            if (!status.Active) return false;
            if (milliseconds < lastTime) { Cancel("Captura detenida: el reloj retrocedió. Repite la identificación."); return true; }
            lastTime = milliseconds;
            if (milliseconds - started >= duration)
            {
                status.Active = false; status.Completed = true; status.SecondsRemaining = 0;
                status.Message = status.Samples == 0 ? "Captura terminada sin informes compatibles. No se cambió el firmware."
                    : status.Transitions == 0 ? "Captura terminada sin cambios de bits candidatos; el botón sigue sin identificar."
                    : "Captura terminada. Los cambios observados aún no asignan un bit a un botón físico.";
                return true;
            }
            status.SecondsRemaining = (int)((duration - (milliseconds - started) + 999) / 1000);
            return false;
        }
        public void Observe(byte[] report, long milliseconds)
        {
            Tick(milliseconds);
            if (!status.Active) return;
            int offset;
            if (!LegionButtonReport.TryGetButtonOffset(report, out offset)) return;
            if (formatOffset >= 0 && formatOffset != offset)
            { Cancel("El formato del informe cambió durante la captura. Repite la identificación sin reconectar los mandos."); return; }
            formatOffset = offset;
            status.Header = offset == 16 ? "04 00 A1" : "04 3C 74";
            status.CandidateOffset = status.ButtonIndex < 2 ? offset : offset + 2;
            int bits = report[status.CandidateOffset] & (status.ButtonIndex < 2 ? 0xc0 : 0xfc);
            if (status.Samples == 0 || milliseconds - started < 1000)
            {
                status.BaselineBits = status.CurrentBits = bits;
                if (status.Samples < 1000000) status.Samples++;
                return;
            }
            int changed = status.CurrentBits ^ bits;
            status.ChangedBits |= changed;
            status.PressedBits |= changed & bits;
            status.ReleasedBits |= changed & status.CurrentBits;
            if (changed != 0 && status.Transitions < 1000000) status.Transitions++;
            status.CurrentBits = bits;
            if (status.Samples < 1000000) status.Samples++;
            status.Message = "Pulsa y suelta solo el botón elegido. Los bits candidatos todavía no tienen nombre físico confirmado.";
        }
        public void Cancel(string message = "Captura cancelada. Suelta los botones para rearmar las acciones.")
        {
            if (!status.Active) return;
            status.Active = false; status.Cancelled = true; status.SecondsRemaining = 0; status.Message = message;
        }
        public LegionButtonCaptureStatus GetStatus(long milliseconds)
        {
            Tick(milliseconds);
            return new LegionButtonCaptureStatus { Active = status.Active, Completed = status.Completed, Cancelled = status.Cancelled,
                ButtonIndex = status.ButtonIndex, SecondsRemaining = status.SecondsRemaining, Header = status.Header,
                CandidateOffset = status.CandidateOffset, BaselineBits = status.BaselineBits, CurrentBits = status.CurrentBits,
                ChangedBits = status.ChangedBits, PressedBits = status.PressedBits, ReleasedBits = status.ReleasedBits,
                Samples = status.Samples, Transitions = status.Transitions, Message = status.Message };
        }
    }
}
