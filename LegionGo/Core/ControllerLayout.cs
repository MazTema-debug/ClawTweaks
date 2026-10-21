// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class ControllerLayout
    {
        public static readonly int[] Buttons = { 4096, 8192, 16384, 32768, 256, 512, 32, 16, 64, 128, 1, 2, 4, 8, 1024 };
        public static readonly string[] Labels = { "A", "B", "X", "Y", "LB", "RB", "Vista / Back", "Menú / Start", "LS", "RS", "Cruceta ↑", "Cruceta ↓", "Cruceta ←", "Cruceta →", "Guía" };
        public static readonly string[] LegionLabels = { "Legion L", "Legion R", "Y1", "Y2", "Y3", "M2", "M3" };
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public int[] Map { get; set; } = (int[])Buttons.Clone();
        [DataMember] public int LeftDeadzone { get; set; } = 10;
        [DataMember] public int RightDeadzone { get; set; } = 10;
        [DataMember] public bool SwapSticks { get; set; }
        [DataMember] public bool InvertLeftY { get; set; }
        [DataMember] public bool InvertRightY { get; set; }
        [DataMember] public bool SwapTriggers { get; set; }
        [DataMember] public int Rumble { get; set; } = 100;
        // Seven normalized digital controls. Old profiles omit this optional map.
        [DataMember(EmitDefaultValue = false)] public int[] LegionMap { get; set; }
        public bool UsesLegionButtons => LegionMap != null && LegionMap.Length == LegionLabels.Length && LegionMap.Any(x => x != 0);
        public ControllerLayout Copy() => Protocol.Decode<ControllerLayout>(Protocol.Encode(this));
        public void Validate()
        {
            if (Map == null || Map.Length != Buttons.Length || Map.Any(x => x != 0 && !Buttons.Contains(x))
                || (LegionMap != null && (LegionMap.Length != LegionLabels.Length
                    || LegionMap.Where((x, i) => x != 0 && (((1 << i) & LegionButtonReport.SupportedControllerButtons) == 0 || !Buttons.Contains(x))).Any()))
                || LeftDeadzone < 0 || LeftDeadzone > 30 || RightDeadzone < 0 || RightDeadzone > 30 || Rumble < 0 || Rumble > 100)
                throw new ArgumentException("Distribución del mando inválida.");
        }
        public GamepadFrame Transform(GamepadFrame input)
        {
            ushort buttons = 0;
            for (int i = 0; i < Buttons.Length; i++) if ((input.Buttons & Buttons[i]) != 0) buttons |= (ushort)Map[i];
            if (LegionMap != null)
                for (int i = 0; i < LegionLabels.Length; i++)
                    if ((LegionButtonReport.SupportedControllerButtons & input.LegionButtons & (1 << i)) != 0) buttons |= (ushort)LegionMap[i];
            // Opposed D-pad directions cancel. Multiple inputs may map to one output.
            if ((buttons & 3) == 3) buttons &= 0xfffc;
            if ((buttons & 12) == 12) buttons &= 0xfff3;
            short leftX, leftY, rightX, rightY;
            Stick(input.LX, input.LY, LeftDeadzone, InvertLeftY, out leftX, out leftY);
            Stick(input.RX, input.RY, RightDeadzone, InvertRightY, out rightX, out rightY);
            return new GamepadFrame { Buttons = buttons, LX = SwapSticks ? rightX : leftX, LY = SwapSticks ? rightY : leftY,
                RX = SwapSticks ? leftX : rightX, RY = SwapSticks ? leftY : rightY,
                LT = SwapTriggers ? input.RT : input.LT, RT = SwapTriggers ? input.LT : input.RT };
        }
        private static void Stick(short x, short y, int deadzone, bool invertY, out short resultX, out short resultY)
        {
            double nx = x / (x < 0 ? 32768.0 : 32767.0), ny = y / (y < 0 ? 32768.0 : 32767.0);
            if (invertY) ny = -ny;
            double magnitude = Math.Sqrt(nx * nx + ny * ny), threshold = deadzone / 100.0;
            if (magnitude <= threshold) { resultX = resultY = 0; return; }
            double scale = (Math.Min(1, magnitude) - threshold) / (1 - threshold) / magnitude;
            resultX = Scale(nx * scale); resultY = Scale(ny * scale);
        }
        private static short Scale(double v) => (short)Math.Round(Math.Max(-1, Math.Min(1, v)) * (v < 0 ? 32768 : 32767));
    }
    [DataContract]
    public struct GamepadFrame
    {
        [DataMember] public ushort Buttons;
        [DataMember] public byte LT, RT;
        [DataMember] public short LX, LY, RX, RY;
        [DataMember(EmitDefaultValue = false)] public byte LegionButtons;
        public bool EmergencyChord => (Buttons & (256 | 512 | 32 | 16)) == (256 | 512 | 32 | 16);
    }
    [DataContract]
    public sealed class ControllerStatus
    {
        [DataMember(EmitDefaultValue = false)] public bool SupportsBusyReaderReconnection { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public bool Connected { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool Suspended { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool RecoveringInput { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool WaitingForNeutral { get; set; }
        [DataMember(EmitDefaultValue = false)] public int ResumeCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public int ReconnectCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public string LastRecoveryKind { get; set; }
        [DataMember(EmitDefaultValue = false)] public string PowerEvent { get; set; }
        [DataMember(EmitDefaultValue = false)] public string PowerEventAtUtc { get; set; }
        [DataMember] public bool Hidden { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool PreparingPlayerOne { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SupportsPlayerOnePreparation { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SupportsAutomaticPlayerOnePreparation { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SupportsModePlayerOnePreparation { get; set; }
        [DataMember] public int? VirtualSlot { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SlotCheckedAtUtc { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SlotError { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public long Frames { get; set; }
        [DataMember] public GamepadFrame Input { get; set; }
        [DataMember] public GamepadFrame Output { get; set; }
    }
    public interface IControllerService : IDisposable
    {
        ControllerLayout Layout { get; }
        ControllerStatus GetStatus();
        void Preflight(ControllerLayout layout);
        void Apply(ControllerLayout layout);
    }
}
