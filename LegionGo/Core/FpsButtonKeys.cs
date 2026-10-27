// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    public interface IFpsButtonKeys
    {
        void SetFpsKeys(bool enabled);
    }

    [DataContract]
    public sealed class FpsButtonKeysStatus
    {
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public bool WaitingForRelease { get; set; }
        [DataMember] public long InsertSent { get; set; }
        [DataMember] public long EscapeSent { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
    }

    // Observed on the independently verified original 8APU1 in FPS mode.
    // The caller still owns the machine gate and the single passive HID reader.
    // These FPS masks deliberately do not change LegionButtonReport's gamepad map.
    public sealed class FpsButtonKeys
    {
        public const int InsertAction = 1, EscapeAction = 2, FreshMilliseconds = 250;
        private bool enabled, armed;
        private int previous;
        private long last = -1, foreground;
        public bool Enabled => enabled;
        public bool WaitingForRelease => enabled && !armed;

        public void Configure(bool value) { enabled = value; Reset(); }
        public void Reset() { armed = false; previous = 0; last = -1; foreground = 0; }

        public static bool TryDecode(ushort product, byte[] report, out int buttons)
        {
            buttons = 0;
            if (product != 0x61ee || report == null || report.Length != 64
                || report[0] != 0x04 || report[1] != 0x3c || report[2] != 0x74) return false;
            buttons = ((report[20] & 0x20) != 0 ? InsertAction : 0)
                | ((report[20] & 0x04) != 0 ? EscapeAction : 0);
            return true;
        }

        // One short key pulse per physical press; never retain a keyboard key.
        // Focus/mode changes, stale input and blocked desktops require fresh release.
        public int Observe(ushort product, byte[] report, long now, bool available, long window)
        {
            int buttons;
            if (!enabled || !available || window == 0 || !TryDecode(product, report, out buttons))
            { Reset(); return 0; }
            if (last < 0 || now < last || now - last > FreshMilliseconds || foreground != window)
            { armed = false; previous = 0; }
            last = now; foreground = window;
            if (!armed) { if (buttons == 0) armed = true; previous = buttons; return 0; }
            int pressed = buttons & ~previous;
            previous = buttons;
            return pressed;
        }
    }
}
