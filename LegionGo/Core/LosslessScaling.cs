// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class LosslessScalingStatus
    {
        [DataMember] public bool Supported { get; set; }
        [DataMember] public bool Installed { get; set; }
        [DataMember] public bool? Running { get; set; }
        [DataMember] public string CapturedAtUtc { get; set; }
        [DataMember(EmitDefaultValue = false)] public string FileVersion { get; set; }
        [DataMember] public string Message { get; set; }
    }

    public interface ILosslessScalingStatusProvider
    {
        bool Supported { get; }
        LosslessScalingStatus Inspect();
    }

    public static class LosslessScalingAccess
    {
        // The widget launches this fixed Steam URI under its own user context.
        // No executable, arguments, key sequence or URL comes from IPC/settings.
        public const string LaunchUri = "steam://run/993090";
    }
}
