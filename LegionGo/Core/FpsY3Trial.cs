// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    // A deliberately bounded first native route, separate from general activation.
    public static class FpsY3TrialRules
    {
        public const int DurationMilliseconds = 120000;
        // Experimental allowance for the original Y3 press while its native
        // substitution is verified. Live input must still be fresh separately.
        public const int MaxPulseAgeMilliseconds = 1000;
        public static bool IsKey(int key) => key == 0x1b || key == 0x2d
            || key >= 0x30 && key <= 0x39 || key >= 0x41 && key <= 0x5a || key >= 0x70 && key <= 0x87;
        public static int Require(RemapProfile profile)
        {
            if (profile == null) throw new ArgumentException("Selecciona una distribución guardada.");
            profile.Validate();
            var changed = profile.Bindings.Where(b => b.Action.Kind != RemapActionKind.Original).ToArray();
            if (profile.Mode != RemapMode.Fps || changed.Length != 1 || changed[0].Input != RemapInput.Y3
                || changed[0].Action.Kind != RemapActionKind.Keyboard || changed[0].Behavior != RemapBehavior.Tap
                || changed[0].Action.Keys.Length != 1 || !IsKey(changed[0].Action.Keys[0]))
                throw new InvalidOperationException("La prueba admite solo FPS: Y3 → Teclado, una letra, número, F1–F24, Insert o Escape, en Pulso. El resto debe conservar Original.");
            return changed[0].Action.Keys[0];
        }
        public static bool CanUse(RemapProfile profile)
        { try { Require(profile); return true; } catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } }
        public static RemapCapabilities Capabilities() => new RemapCapabilities {
            Mode = RemapMode.Fps, Inputs = new[] { RemapInput.Y3 }, ReplacedInputs = new[] { RemapInput.Y3 }, Keyboard = true
        };
        public static bool Decode(ushort product, byte[] report, out bool down)
        {
            down = false;
            if (product != 0x61ee || report == null || report.Length != 64
                || report[0] != 4 || report[1] != 0x3c || report[2] != 0x74) return false;
            down = (report[20] & 0x20) != 0; return true;
        }
    }

    [DataContract]
    public sealed class FpsY3TrialStatus
    {
        [DataMember] public bool Available { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public bool RecoveryPending { get; set; }
        [DataMember(EmitDefaultValue = false)] public long SessionOrdinal { get; set; }
        [DataMember] public string ProfileId { get; set; }
        [DataMember] public int Key { get; set; }
        [DataMember] public long PulsesSent { get; set; }
        [DataMember] public int RemainingMilliseconds { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Error { get; set; }
    }

    // Exported only by an explicit read-only diagnostic request. No keys,
    // window handles, device identifiers, paths, tokens or raw HID reports.
    [DataContract]
    public sealed class FpsY3AttemptDiagnostic
    {
        [DataMember] public long Number { get; set; }
        [DataMember] public long SinceStartMs { get; set; }
        [DataMember] public long Sequence { get; set; }
        [DataMember] public long SampleAtMs { get; set; }
        [DataMember] public long InitialAgeMs { get; set; } = -1;
        [DataMember] public long PreflightMs { get; set; } = -1;
        [DataMember] public long InputReadMs { get; set; } = -1;
        [DataMember] public long DesktopInspectMs { get; set; } = -1;
        [DataMember] public long EngineMs { get; set; } = -1;
        [DataMember] public long EngineAgeMs { get; set; } = -1;
        [DataMember] public bool Down { get; set; }
        [DataMember] public bool Armed { get; set; }
        [DataMember] public bool DesktopAvailable { get; set; }
        [DataMember] public long VerifyMs { get; set; } = -1;
        [DataMember] public long NativeVerifyMs { get; set; } = -1;
        [DataMember] public long VerifyStartMs { get; set; } = -1;
        [DataMember] public long VerifyEndMs { get; set; } = -1;
        [DataMember] public string VerifyResult { get; set; }
        [DataMember] public long SubsequentReadMs { get; set; } = -1;
        [DataMember] public long SubsequentSequence { get; set; } = -1;
        [DataMember] public long SubsequentAtMs { get; set; } = -1;
        [DataMember] public long SubsequentAgeMs { get; set; } = -1;
        [DataMember(EmitDefaultValue = false)] public long? SubsequentAgeBeforeSendMs { get; set; }
        [DataMember] public bool SubsequentDown { get; set; }
        [DataMember] public long OriginalAgeAfterVerifyMs { get; set; } = -1;
        [DataMember] public long OriginalAgeBeforeSendMs { get; set; } = -1;
        [DataMember] public long PulseMs { get; set; } = -1;
        [DataMember] public long PulseStartMs { get; set; } = -1;
        [DataMember] public long PulseEndMs { get; set; } = -1;
        [DataMember] public int AcceptedEvents { get; set; } = -1;
        [DataMember] public string Decision { get; set; }
    }
    [DataContract]
    public sealed class FpsY3Diagnostics
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public int Capacity { get; set; }
        [DataMember] public long SessionOrdinal { get; set; }
        [DataMember] public bool ActiveAtSnapshot { get; set; }
        [DataMember] public bool RecoveryPendingAtSnapshot { get; set; }
        [DataMember] public long Dropped { get; set; }
        [DataMember] public long SetupMs { get; set; } = -1;
        [DataMember] public long VerifierWarmupMs { get; set; } = -1;
        [DataMember] public FpsY3AttemptDiagnostic[] Attempts { get; set; } = new FpsY3AttemptDiagnostic[0];
    }
}
