// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    public enum LegionShortcutMode { Disabled, Left, Right, Both }

    [DataContract]
    public sealed class LegionShortcutStatus
    {
        [DataMember] public LegionShortcutMode Mode { get; set; }
        [DataMember] public bool Connected { get; set; }
        [DataMember] public bool Left { get; set; }
        [DataMember] public bool Right { get; set; }
        [DataMember] public long Reports { get; set; }
        [DataMember] public long Activations { get; set; }
        [DataMember] public string LastActivationUtc { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Diagnostic { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SupportsButtonMapping { get; set; }
        [DataMember(EmitDefaultValue = false)] public int SupportedButtons { get; set; }
        [DataMember(EmitDefaultValue = false)] public int ReservedButtons { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool ControllerInputEnabled { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool PreparationPaused { get; set; }
        [DataMember(EmitDefaultValue = false)] public byte ControllerButtons { get; set; }
        [DataMember(EmitDefaultValue = false)] public LegionButtonCaptureStatus Capture { get; set; }
        [DataMember(EmitDefaultValue = false)] public FpsButtonKeysStatus FpsKeys { get; set; }
    }
    public interface ILegionShortcuts
    {
        LegionShortcutStatus GetStatus();
        void Configure(LegionShortcutMode mode);
    }

    // Original Legion Go vendor reports, described by GoTweaks' MIT-licensed
    // LegionButtonMonitor. No firmware initialization or remapping is performed.
    public static class LegionButtonReport
    {
        // Normalized profile slots: Legion L/R, then individually identified Y/M
        // controls. Unconfirmed physical names must never be inferred from a bit.
        public const int SupportedControllerButtons = 0x7f;

        public static bool TryDecode(byte[] report, out int buttons)
        {
            buttons = 0;
            int index;
            if (!TryGetButtonOffset(report, out index)) return false;
            buttons = ((report[index] & 0x80) != 0 ? 1 : 0) | ((report[index] & 0x40) != 0 ? 2 : 0);
            return true;
        }
        public static bool TryGetButtonOffset(byte[] report, out int index)
        {
            index = 0;
            if (report == null || report.Length != 64 || report[0] != 0x04) return false;
            if (report[1] == 0 && report[2] == 0xa1) index = 16;
            else if (report[1] == 0x3c && report[2] == 0x74) index = 18;
            else return false; // Reject command replies, status/IMU and unrelated HID reports.
            return true;
        }

        public static bool TryDecodeControllerButtons(byte[] report, out int buttons)
        {
            int offset;
            buttons = 0;
            if (!TryGetButtonOffset(report, out offset)) return false;
            buttons = ((report[offset] & 0x80) != 0 ? 1 : 0) | ((report[offset] & 0x40) != 0 ? 2 : 0);
            if ((report[offset + 2] & 0x80) != 0) buttons |= 1 << 2; // Y1
            if ((report[offset + 2] & 0x40) != 0) buttons |= 1 << 3; // Y2
            if ((report[offset + 2] & 0x20) != 0) buttons |= 1 << 4; // Y3
            if ((report[offset + 2] & 0x04) != 0) buttons |= 1 << 5; // M2
            if ((report[offset + 2] & 0x08) != 0) buttons |= 1 << 6; // M3
            return true;
        }
    }

    // One activation per deliberate press; reconnect/enable always requires a
    // neutral report first. A long reporting gap also requires release/re-arm.
    public sealed class LegionShortcutGate
    {
        private bool armed;
        private long pressedAt = -1, lastReport = -1;
        public bool Observe(LegionShortcutMode mode, int buttons, long milliseconds)
        {
            if (mode < LegionShortcutMode.Disabled || mode > LegionShortcutMode.Both)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (lastReport >= 0 && (milliseconds < lastReport || milliseconds - lastReport > 1000)) Reset();
            lastReport = milliseconds;
            if (mode == LegionShortcutMode.Disabled) { armed = false; pressedAt = -1; return false; }
            if (buttons == 0) { armed = true; pressedAt = -1; return false; }
            int required = (int)mode;
            if (!armed || (buttons & required) != required) { pressedAt = -1; return false; }
            if (pressedAt < 0) pressedAt = milliseconds;
            if (milliseconds - pressedAt < 60) return false;
            armed = false; pressedAt = -1;
            return true;
        }
        public void Reset() { armed = false; pressedAt = lastReport = -1; }
    }
}
