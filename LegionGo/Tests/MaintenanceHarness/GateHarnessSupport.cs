// SPDX-License-Identifier: AGPL-3.0-only
// Fakes used solely by the standalone net48 integration-test entry point.
using System;
using LegionTweaks;

internal static partial class Program
{
    private static RemapProfile TrialProfile(int key = 0x51) => new RemapProfile {
        Mode = RemapMode.Fps, Bindings = new[] { new RemapBinding { Input = RemapInput.Y3,
            Behavior = RemapBehavior.Tap, Action = new RemapAction { Kind = RemapActionKind.Keyboard, Keys = new[] { key } } } } };
    private sealed class TrialSource : IFpsY3Source
    {
        internal long Now; internal int Starts, Stops;
        internal FpsY3Sample Sample = SampleValue();
        private static FpsY3Sample SampleValue() {
            var report = new byte[64]; report[0] = 4; report[1] = 0x3c; report[2] = 0x74;
            return new FpsY3Sample { Product = 0x61ee, Path = "synthetic-device", Report = report };
        }
        public long NowMilliseconds => Now;
        public bool CanBeginY3 => Starts == Stops;
        public void BeginY3Read() { Starts++; }
        public void EndY3Read() { Stops++; }
        public FpsY3Sample ReadY3() => Sample;
    }
    private sealed class TrialLease : IFpsY3Lease
    {
        internal int Starts, Restores; internal bool IsPending, IsMuted, FailBegin, FailRestore, AbandonmentObserved;
        internal Action OnBegin;
        public long VerifierWarmupMs => 0; public long LastNativeVerifyMs => 0;
        public bool Pending => IsPending || AbandonmentObserved; public bool PhysicalRecoveryPending => IsPending; public bool Muted => IsMuted;
        public string DevicePath { get; private set; }
        public void Begin(string path) {
            Starts++; DevicePath = path; IsPending = IsMuted = true; OnBegin?.Invoke();
            if (FailBegin) throw new InvalidOperationException("synthetic begin failure");
        }
        public void Verify() { }
        public void Restore() {
            if (!IsPending) return; Restores++;
            if (FailRestore) throw new InvalidOperationException("synthetic recovery failure");
            IsPending = IsMuted = false;
        }
    }
    private sealed class TrialOutput : IFpsGuardedKeyOutput
    {
        public string LastPulseDecision => "synthetic"; public int LastAcceptedEvents => 0;
        public MouseDesktop Inspect() => new MouseDesktop { Available = true, Foreground = 1 };
        public bool Pulse(int key, MouseDesktop expected) => false;
        public bool Pulse(int key, MouseDesktop expected, Func<bool> canSend) => false;
        public bool ReleaseOwned() => true;
    }
}
