// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void ControllerModeCycleTests()
    {
        Test("Mode cycle performs only its explicit transition verification and recovery sequence", () => {
            var port = new ModeCycleFake(); ControllerModeCycle.Run(port);
            Assert(port.Events.SequenceEqual(new[] { "initial", "enter", "temporary", "restore", "original", "complete" }), "cycle skipped readback or added an action");
            Assert(port.Mode == 1 && !port.Pending, "cycle did not finish in its original mode");
        });
        Test("Mode cycle rejects a bad initial state without arming or attempting a change", () => {
            var port = new ModeCycleFake { FailOnce = "initial" };
            MustFail(() => ControllerModeCycle.Run(port));
            Assert(port.Events.SequenceEqual(new[] { "initial" }) && port.Mode == 1 && !port.Pending, "initial rejection affected recovery or hardware");
        });
        Test("Mode cycle recovers a transition that changed state then failed before returning", () => {
            var port = new ModeCycleFake { FailOnce = "enter" };
            MustFail(() => ControllerModeCycle.Run(port));
            Assert(port.Events.SequenceEqual(new[] { "initial", "enter", "restore", "original", "complete" }) && port.Mode == 1 && !port.Pending,
                "partial transition did not invoke recovery");
        });
        Test("Mode cycle recovers after a failed free-slot observation and does not report success", () => {
            var port = new ModeCycleFake { FailOnce = "temporary" };
            MustFail(() => ControllerModeCycle.Run(port));
            Assert(port.Mode == 1 && !port.Pending && port.Events.Last() == "complete", "free-slot failure skipped recovery");
        });
        Test("Mode cycle retries restoration and verification after a transient cleanup failure", () => {
            foreach(string stage in new[] { "restore", "original", "complete" }) {
                var port = new ModeCycleFake { FailOnce = stage };
                MustFail(() => ControllerModeCycle.Run(port));
                Assert(port.Mode == 1 && !port.Pending && port.Events.Count(x => x == "restore") == 2, "cleanup failure lost the recovery path");
            }
        });
        Test("Mode cycle retains both the causal failure and failed recovery while keeping intent", () => {
            var port = new ModeCycleFake { FailOnce = "temporary", FailRecovery = true };
            try { ControllerModeCycle.Run(port); throw new Exception("failed recovery accepted"); }
            catch(AggregateException error) {
                Assert(error.InnerExceptions.Count == 2 && error.InnerExceptions[0].Message == "temporary" && error.InnerExceptions[1].Message == "restore",
                    "recovery replaced the causal failure");
            }
            Assert(port.Pending && !port.Events.Contains("complete"), "unconfirmed recovery discarded its original");
        });
        Test("Mode retirement accepts only the same fresh completed operation without a current journal", () => {
            var lease = ModeLease("Restored");
            var outcome = ControllerModeRecoveryRules.RecordOutcome(null, lease, ModeSid, lease.CreatedUtc, recovered: true, completed: true);
            Assert(ControllerModeRecoveryRules.IsCompletedAfterRetirement(null, outcome, ModeSid, lease.Token, lease.CreatedUtc), "valid retired recovery rejected");
            foreach(Action<ControllerModeOutcome> corrupt in new Action<ControllerModeOutcome>[] {
                x => x.Version = 2, x => x.Token = "other", x => x.Sid = "other", x => x.CapturedUtc = lease.CreatedUtc - 1,
                x => x.Recovered = false, x => x.Completed = false }) {
                var bad = Protocol.Decode<ControllerModeOutcome>(Protocol.Encode(outcome)); corrupt(bad);
                Assert(!ControllerModeRecoveryRules.IsCompletedAfterRetirement(null, bad, ModeSid, lease.Token, lease.CreatedUtc), "unrelated or incomplete outcome settled recovery");
            }
            Assert(!ControllerModeRecoveryRules.IsCompletedAfterRetirement(lease, outcome, ModeSid, lease.Token, lease.CreatedUtc)
                && !ControllerModeRecoveryRules.IsCompletedAfterRetirement(null, outcome, ModeSid, null, lease.CreatedUtc)
                && !ControllerModeRecoveryRules.IsCompletedAfterRetirement(null, outcome, ModeSid, lease.Token, 0), "current or unknown ownership was ignored");
        });
        Test("Mode outcome preserves the first failure across recovery and never inherits another token", () => {
            var lease = ModeLease("Recovering");
            var first = ControllerModeRecoveryRules.RecordOutcome(null, lease, ModeSid, 100, "switch", "first", 1);
            var recovered = ControllerModeRecoveryRules.RecordOutcome(first, lease, ModeSid, 200, "recovery", "second", 2, true, true);
            Assert(recovered.Failure == "first" && recovered.WorkerExitCode == 1 && recovered.Completed && recovered.Recovered && !first.Completed,
                "cleanup changed the causal error or mutated the previous snapshot");
            lease.Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var next = ControllerModeRecoveryRules.RecordOutcome(recovered, lease, ModeSid, 300);
            Assert(next.Failure == null && !next.Completed && !next.Recovered, "new operation inherited recovery success");
        });
        Test("A readonly mode-query adapter cannot send configuration even for a supported receiver", () => {
            var reader = new NativeControllerModeDevice();
            MustFail(() => reader.ChangeMode(ModeRoot, ModeLocation, 2));
            MustFail(() => reader.ChangeMode(ModeRoot, ModeLocation, 1));
            var authorized = new NativeControllerModeDevice(_ => { });
            foreach (int unknown in new[] { 0, 3, 255, -1 }) MustFail(() => authorized.ChangeMode(ModeRoot, ModeLocation, unknown));
            MustFail(() => authorized.ChangeMode(ModeTemporaryRoot, ModeLocation, 2));
            MustFail(() => authorized.ChangeMode(ModeRoot, null, 2));
        });
        Test("Mode journal progression cannot rewind recovery or skip restoration", () => {
            foreach(string stage in new[] { "Recovering", "Restored" }) {
                Assert(!ControllerModeRecoveryRules.CanTransitionPhase(stage, "Switching")
                    && !ControllerModeRecoveryRules.CanTransitionPhase(stage, "Temporary")
                    && !ControllerModeRecoveryRules.CanTransitionPhase(stage, "Armed"), "stale coordinator revived a mutation");
            }
            Assert(!ControllerModeRecoveryRules.CanTransitionPhase("Temporary", "Restored")
                && ControllerModeRecoveryRules.CanTransitionPhase("Temporary", "Recovering")
                && ControllerModeRecoveryRules.CanTransitionPhase("Recovering", "Restored")
                && ControllerModeRecoveryRules.CanTransitionPhase("Restored", "Recovering"), "recovery was skipped or could not retry");
            Assert(!ControllerModeRecoveryRules.CanTransitionPhase(null, "Armed") && !ControllerModeRecoveryRules.CanTransitionPhase("invalid", "Switching"), "unknown phase supplied authorization");
        });
    }

    private sealed class ModeCycleFake : IControllerModeCyclePort
    {
        internal readonly List<string> Events = new List<string>();
        internal string FailOnce;
        internal bool FailRecovery, Pending;
        internal int Mode = 1;
        private void Step(string stage) {
            Events.Add(stage);
            if(FailOnce == stage) { FailOnce = null; throw new InvalidOperationException(stage); }
            if(FailRecovery && stage == "restore") throw new InvalidOperationException(stage);
        }
        public void CheckInitial() { Step("initial"); }
        public void EnterDirectInput() { Pending = true; Mode = 2; Step("enter"); }
        public void VerifyTemporary() { Step("temporary"); }
        public void RestoreOriginal() { Step("restore"); Mode = 1; }
        public void VerifyOriginal() { Step("original"); Assert(Mode == 1, "original not restored"); }
        public void CompleteRecovery() { Step("complete"); Pending = false; }
    }
}
