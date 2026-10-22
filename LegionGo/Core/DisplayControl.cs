// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class DisplayMode
    {
        [DataMember] public int Width { get; set; }
        [DataMember] public int Height { get; set; }
        [DataMember] public int Frequency { get; set; }
        public bool Same(DisplayMode other) => other != null && Width == other.Width && Height == other.Height && Frequency == other.Frequency;
        public DisplayMode Copy() => new DisplayMode { Width = Width, Height = Height, Frequency = Frequency };
        public string Label => ToString();
        public override string ToString() => Width + " × " + Height + " · " + Frequency + " Hz";
    }
    [DataContract]
    public sealed class DisplayInfo
    {
        [DataMember] public string DeviceName { get; set; }
        [DataMember] public string Adapter { get; set; }
        [DataMember] public string Monitor { get; set; }
        [DataMember] public int ActiveDisplays { get; set; }
        [DataMember] public int Orientation { get; set; }
        [DataMember] public DisplayMode Current { get; set; }
        [DataMember] public DisplayMode[] Modes { get; set; }
        [DataMember] public bool CanChange { get; set; }
        [DataMember] public string Restriction { get; set; }
        [DataMember] public string CapturedAtUtc { get; set; }
    }
    [DataContract]
    public sealed class DisplayRequest
    {
        [DataMember(EmitDefaultValue = false)] public string SnapshotId { get; set; }
        [DataMember(EmitDefaultValue = false)] public DisplayMode Mode { get; set; }
        [DataMember(EmitDefaultValue = false)] public string TrialToken { get; set; }
    }
    [DataContract]
    public sealed class DisplayStatus
    {
        [DataMember] public bool Supported { get; set; }
        [DataMember(EmitDefaultValue = false)] public DisplayInfo Info { get; set; }
        [DataMember(EmitDefaultValue = false)] public string SnapshotId { get; set; }
        [DataMember(EmitDefaultValue = false)] public string TrialToken { get; set; }
        [DataMember] public string Phase { get; set; }
        [DataMember] public bool Pending { get; set; }
        [DataMember] public bool RecoveryPending { get; set; }
        [DataMember] public int RemainingSeconds { get; set; }
        [DataMember(EmitDefaultValue = false)] public string DeadlineUtc { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Error { get; set; }
    }
    public interface IDisplayControl : IDisposable
    {
        bool Supported { get; }
        DisplayStatus Inspect();
        DisplayStatus Preview(DisplayRequest request);
        DisplayStatus Decide(string token, bool confirm);
        DisplayStatus Recover();
        void CancelPending();
    }
}
