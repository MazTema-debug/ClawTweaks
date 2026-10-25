// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    public interface ILegionGamepadInput
    {
        void SetGamepadInputEnabled(bool enabled);
        bool GamepadInputPaused { get; }
        bool TryReadGamepad(out GamepadFrame frame);
    }

    // Original-Go protocol facts from the attributed MIT GoTweaks reference.
    // Only the two original-Go formats already accepted by the shared reader.
    // The 04:3c:74 offsets were also checked on the target. No writer,
    // firmware initialization, model inference from PID, or output mapping.
    public static class LegionGamepadReport
    {
        public static bool TryDecode(byte[] report, out GamepadFrame frame)
        {
            frame = new GamepadFrame();
            int offset;
            if (!LegionButtonReport.TryGetButtonOffset(report, out offset)) return false;
            ushort buttons = 0;
            int first = report[offset], second = report[offset + 1], extra = report[offset + 2];
            if ((first & 0x20) != 0) buttons |= 64;
            if ((first & 0x10) != 0) buttons |= 128;
            if ((first & 0x08) != 0) buttons |= 1;
            if ((first & 0x04) != 0) buttons |= 2;
            if ((first & 0x02) != 0) buttons |= 4;
            if ((first & 0x01) != 0) buttons |= 8;
            if ((second & 0x80) != 0) buttons |= 4096;
            if ((second & 0x40) != 0) buttons |= 8192;
            if ((second & 0x20) != 0) buttons |= 16384;
            if ((second & 0x10) != 0) buttons |= 32768;
            if ((second & 0x08) != 0) buttons |= 256;
            if ((second & 0x02) != 0) buttons |= 512;
            if ((extra & 0x02) != 0) buttons |= 32;
            if ((extra & 0x01) != 0) buttons |= 16;
            frame = new GamepadFrame { Buttons = buttons, LT = report[offset + 4], RT = report[offset + 5],
                LX = Axis(report[offset - 4], false), LY = Axis(report[offset - 3], true),
                RX = Axis(report[offset - 2], false), RY = Axis(report[offset - 1], true) };
            return true;
        }
        private static short Axis(byte raw, bool invert)
        {
            int delta = raw - 128;
            double normalized = delta / (delta < 0 ? 128.0 : 127.0);
            if (invert) normalized = -normalized;
            return (short)Math.Round(normalized * (normalized < 0 ? 32768 : 32767));
        }
    }

    // The single owning HID loop supplies this cache. Expiry and reset never
    // substitute an old held state for an unavailable source.
    public sealed class LegionGamepadCache
    {
        public const int FreshMilliseconds = 250;
        private long captured = -1;
        private GamepadFrame frame;
        public void Observe(byte[] report, long now)
        {
            GamepadFrame next;
            if (!LegionGamepadReport.TryDecode(report, out next)) { Reset(); return; }
            frame = next; captured = now;
        }
        public bool TryRead(long now, out GamepadFrame value)
        {
            value = new GamepadFrame();
            if (captured < 0 || now < captured || now - captured > FreshMilliseconds) { Reset(); return false; }
            value = frame; return true;
        }
        public void Reset() { captured = -1; frame = new GamepadFrame(); }
    }

    public sealed class ControllerInputRouteGate
    {
        private bool initialized, direct, waiting, requireDirectRelease;
        public bool WaitingForNeutral => waiting;
        public bool NeedsDirectNeutral(bool useDirect) => !useDirect && (direct || requireDirectRelease);
        public bool Accept(bool useDirect, bool available, GamepadFrame frame, ControllerLayout layout, GamepadFrame? directEvidence = null)
        {
            if (!initialized || direct != useDirect) {
                requireDirectRelease = initialized && direct && !useDirect;
                waiting = initialized || useDirect; initialized = true; direct = useDirect;
            }
            if (!available) { waiting = true; return false; }
            if (!waiting) return true;
            // SDL's focus-filtered zero is not proof that the button used to
            // close Game Bar was physically released.
            if (requireDirectRelease && (!directEvidence.HasValue || !Neutral(directEvidence.Value, layout))) return false;
            if (!Neutral(frame, layout)) return false;
            waiting = requireDirectRelease = false; return true;
        }
        private static bool Neutral(GamepadFrame frame, ControllerLayout layout)
        {
            if (frame.Buttons != 0 || frame.LegionButtons != 0 || frame.LT > 5 || frame.RT > 5) return false;
            int left = Math.Max(1000, layout.LeftDeadzone * 32768 / 100);
            int right = Math.Max(1000, layout.RightDeadzone * 32768 / 100);
            if (Math.Abs((int)frame.LX) > left || Math.Abs((int)frame.LY) > left
                || Math.Abs((int)frame.RX) > right || Math.Abs((int)frame.RY) > right) return false;
            return true;
        }
    }
}
