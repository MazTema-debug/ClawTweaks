// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void AutomaticPlayerOneTests()
    {
        Test("Automatic player one disables only the source and restores it before readiness", () => {
            var p = new AutomaticPreparationPort(); var stages = new List<PlayerOneStage>();
            AutomaticPlayerOnePreparation.Run(p, stage => { p.Calls.Add(stage.ToString()); stages.Add(stage); });
            Assert(p.Disables == 1 && p.Connects == 1 && p.Enables == 1 && p.Completions == 1, "normal sequence did not balance the recovery lease");
            Assert(p.Calls.SequenceEqual(new[] { "WaitingForFreeSlot", "disable", "Connecting", "connect", "enable",
                "WaitingForPhysical", "complete", "Ready" }), "source restoration or journal retirement occurred in the wrong order");
            Assert(p.Waits == 0 && stages.Last() == PlayerOneStage.Ready, "normal sequence did not finish promptly");
        });
        Test("Automatic player one uses a free first slot without disabling or enabling a source in P2", () => {
            var p = new AutomaticPreparationPort { Environment = AutomaticPreparationState(1, 0, 1) };
            AutomaticPlayerOnePreparation.Run(p, null);
            Assert(p.Connects == 1 && p.Disables == 0 && p.Enables == 0 && p.Completions == 0, "free first slot unnecessarily changed a device");
        });
        Test("Automatic player one tolerates an identified source whose XInput index is not visible yet", () => {
            var p = new AutomaticPreparationPort { Environment = AutomaticPreparationState(1, 0) };
            AutomaticPlayerOnePreparation.Run(p, null);
            Assert(p.Connects == 1 && p.Disables == 0 && p.Enables == 0, "temporary absent source index caused a device transition");
        });
        Test("Automatic player one rejects ambiguous missing or additional sources before any mutation", () => {
            foreach (var state in new[] { AutomaticPreparationState(0, 0), AutomaticPreparationState(2, 0, 0),
                AutomaticPreparationState(1, 1, 0), AutomaticPreparationState(1, 0, 0, 1), AutomaticPreparationState(-1, 0),
                AutomaticPreparationState(1, -1), null, new PlayerOneEnvironment(),
                new PlayerOneEnvironment { PhysicalDevices = 1, ConnectedSlots = new bool[3] },
                new PlayerOneEnvironment { PhysicalDevices = 1, ConnectedSlots = new bool[5] } })
            {
                var p = new AutomaticPreparationPort { Environment = state };
                MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(p.Disables == 0 && p.Enables == 0 && p.Connects == 0 && p.Completions == 0, "invalid preflight touched a device");
            }
        });
        Test("Automatic player one rereads P1 before disabling and skips a transition if it became free", () => {
            var p = new AutomaticPreparationPort();
            AutomaticPlayerOnePreparation.Run(p, stage => {
                if (stage == PlayerOneStage.WaitingForFreeSlot) p.Environment = AutomaticPreparationState(1, 0, 2);
            });
            Assert(p.Disables == 0 && p.Enables == 0 && p.Connects == 1, "stale occupied-slot reading caused an unnecessary disable");
        });
        Test("Automatic player one refuses a foreign arrival before its disable operation", () => {
            var p = new AutomaticPreparationPort();
            MustFail(() => AutomaticPlayerOnePreparation.Run(p, stage => {
                if (stage == PlayerOneStage.WaitingForFreeSlot) p.Environment = AutomaticPreparationState(1, 1, 0, 1);
            }));
            Assert(p.Disables == 0 && p.Connects == 0 && p.Enables == 0, "foreign arrival was ignored before mutation");
        });
        Test("Automatic player one waits for both source enumeration and cached XInput slot to disappear", () => {
            var p = new AutomaticPreparationPort();
            p.OnDisable = () => p.Environment = AutomaticPreparationState(0, 0, 0);
            p.OnWait = () => p.Environment = p.Waits == 1 ? AutomaticPreparationState(1, 0) : AutomaticPreparationState(0, 0);
            p.OnConnect = () => Assert(p.Waits == 2, "target was connected before both observations were clear");
            AutomaticPlayerOnePreparation.Run(p, null);
            Assert(p.Disables == 1 && p.Connects == 1 && p.Enables == 1 && p.WaitValues.All(x => x == 100), "polling or native operation count differs");
        });
        Test("Automatic player one recovers a disable that changed state and then threw", () => {
            var p = new AutomaticPreparationPort { OnDisable = () => { throw new InvalidOperationException("disable parcial"); } };
            string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
            Assert(error.Contains("disable parcial") && p.Disables == 1 && p.Enables == 1 && p.Connects == 0 && p.Completions == 0,
                "a throwing disable skipped rollback or retired its journal");
        });
        Test("Automatic player one preserves the primary and recovery failures together", () => {
            var p = new AutomaticPreparationPort { OnDisable = () => { throw new InvalidOperationException("disable parcial"); },
                OnEnable = () => { throw new InvalidOperationException("enable rechazado"); } };
            AggregateException errors = null;
            try { AutomaticPlayerOnePreparation.Run(p, null); } catch (Exception error) { errors = error as AggregateException; }
            Assert(errors != null && errors.InnerExceptions.Count == 2 && errors.InnerExceptions[0].Message.Contains("disable parcial")
                && errors.InnerExceptions[1].Message.Contains("enable rechazado") && p.Enables == 1 && p.Completions == 0,
                "recovery failure hid the original failure or retired its journal");
        });
        Test("Automatic player one retries recovery when the initial enable operation throws", () => {
            var p = new AutomaticPreparationPort();
            p.OnEnable = () => { if (p.Enables == 1) throw new InvalidOperationException("enable parcial"); };
            string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
            Assert(error.Contains("enable parcial") && p.Connects == 1 && p.Enables == 2 && p.Completions == 0,
                "throwing enable did not leave recovery pending for finally");
        });
        Test("Automatic player one never connects if disabling does not free P1 within the total budget", () => {
            var p = new AutomaticPreparationPort { Clock = 50000 };
            p.OnDisable = () => p.Environment = AutomaticPreparationState(1, 0, 0);
            string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
            Assert(error.Contains("45 segundos") && p.Clock == 95000 && p.Connects == 0 && p.Enables == 1 && p.Completions == 0,
                "source release timeout reset its deadline or skipped recovery");
        });
        Test("Automatic player one includes disable and enable calls in its total deadline", () => {
            foreach (bool duringDisable in new[] { true, false })
            {
                var p = new AutomaticPreparationPort();
                p.OnDisable = () => { if (duringDisable) p.Clock += 45000; };
                p.OnEnable = () => { if (!duringDisable && p.Enables == 1) p.Clock += 45000; };
                string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(error.Contains("45 segundos") && p.Connects == (duringDisable ? 0 : 1)
                    && p.Enables == (duringDisable ? 1 : 2) && p.Completions == 0, "native transition escaped the shared deadline");
            }
        });
        Test("Automatic player one times out an unconfirmed target index after eight seconds and recovers", () => {
            foreach (bool indexAvailable in new[] { true, false })
            {
                var p = new AutomaticPreparationPort();
                p.OnConnect = () => { p.VirtualSlot = indexAvailable ? 0 : (int?)null;
                    p.Environment = indexAvailable ? AutomaticPreparationState(0, 1) : AutomaticPreparationState(0, 1, 0); };
                string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(error.Contains("8 segundos") && p.Clock == 8000 && p.Connects == 1 && p.Enables == 1 && p.Completions == 0,
                    "unconfirmed connection was accepted or waited without a bound");
            }
        });
        Test("Automatic player one rejects every wrong virtual slot without retries", () => {
            foreach (int slot in new[] { -1, 1, 2, 3, 4 })
            {
                var p = new AutomaticPreparationPort(); p.OnConnect = () => p.VirtualSlot = slot;
                MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(p.Connects == 1 && p.Waits == 0 && p.Enables == 1 && p.Completions == 0, "wrong virtual slot was retried or accepted");
            }
        });
        Test("Automatic player one aborts a foreign arrival while waiting for source release", () => {
            var p = new AutomaticPreparationPort(); p.OnDisable = () => p.Environment = AutomaticPreparationState(0, 0, 0);
            p.OnWait = () => p.Environment = AutomaticPreparationState(0, 1, 1);
            MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
            Assert(p.Connects == 0 && p.Enables == 1 && p.Waits == 1 && p.Completions == 0, "foreign arrival escaped source release checks");
        });
        Test("Automatic player one rechecks the cleared source immediately before its single connection", () => {
            var p = new AutomaticPreparationPort();
            MustFail(() => AutomaticPlayerOnePreparation.Run(p, stage => {
                if (stage == PlayerOneStage.Connecting) p.Environment = AutomaticPreparationState(1, 0, 1);
            }));
            Assert(p.Disables == 1 && p.Connects == 0 && p.Enables == 1, "source returning before connection was tolerated");
        });
        Test("Automatic player one rejects additional controllers during connection and physical recovery", () => {
            foreach (bool duringConnect in new[] { true, false })
            {
                var p = new AutomaticPreparationPort();
                p.OnConnect = () => { if (duringConnect) p.Environment = AutomaticPreparationState(0, 2, 0, 2); };
                p.OnEnable = () => { if (!duringConnect) p.Environment = AutomaticPreparationState(1, 2, 0, 1, 2); };
                MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(p.Connects == 1 && p.Enables == (duringConnect ? 1 : 2) && p.Completions == 0,
                    "foreign controller was accepted or recovery journal retired");
            }
        });
        Test("Automatic player one refuses a physical source returning before the explicit enable", () => {
            var p = new AutomaticPreparationPort(); p.OnConnect = () => p.Environment = AutomaticPreparationState(1, 1, 0, 1);
            MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
            Assert(p.Connects == 1 && p.Enables == 1 && p.Completions == 0, "unsolicited device reappearance was accepted");
        });
        Test("Automatic player one waits for two fresh complete snapshots before retiring recovery", () => {
            var p = new AutomaticPreparationPort(); int readyInspections = 0;
            p.OnInspect = () => { if (p.Enables > 0 && ++readyInspections == 2) p.Environment = AutomaticPreparationState(0, 1, 0); };
            p.OnWait = () => p.IsCanceled = true;
            var stages = new List<PlayerOneStage>(); MustFail(() => AutomaticPlayerOnePreparation.Run(p, stages.Add));
            Assert(!stages.Contains(PlayerOneStage.Ready) && p.Completions == 0 && p.Enables == 2,
                "a stale ready snapshot retired recovery before the second check");
        });
        Test("Automatic player one waits for the physical interface even when both XInput slots have returned", () => {
            var p = new AutomaticPreparationPort(); p.OnEnable = () => p.Environment = AutomaticPreparationState(0, 1, 0, 1);
            p.OnWait = () => p.Environment = AutomaticPreparationState(1, 1, 0, 1);
            AutomaticPlayerOnePreparation.Run(p, null);
            Assert(p.Waits == 1 && p.Enables == 1 && p.Completions == 1, "partial enumeration was mistaken for complete recovery");
        });
        Test("Automatic player one retries physical recovery after a source return timeout", () => {
            var p = new AutomaticPreparationPort(); p.OnEnable = () => p.Environment = AutomaticPreparationState(0, 1, 0);
            string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
            Assert(error.Contains("45 segundos") && p.Clock == 45000 && p.Enables == 2 && p.Completions == 0,
                "source return did not retain the deadline and recovery journal");
        });
        Test("Automatic player one does not announce ready when recovery completion fails", () => {
            var p = new AutomaticPreparationPort { OnComplete = () => { throw new InvalidOperationException("journal no confirmado"); } };
            var stages = new List<PlayerOneStage>(); string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, stages.Add));
            Assert(error.Contains("journal no confirmado") && p.Completions == 1 && p.Enables == 2 && !stages.Contains(PlayerOneStage.Ready),
                "journal completion failure was hidden behind success");
        });
        Test("Automatic player one cancels before mutation and recovers cancellation in later stages", () => {
            foreach (var cancelAt in new[] { PlayerOneStage.WaitingForFreeSlot, PlayerOneStage.Connecting,
                PlayerOneStage.WaitingForPhysical, PlayerOneStage.Ready })
            {
                var p = new AutomaticPreparationPort();
                string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, stage => { if (stage == cancelAt) p.IsCanceled = true; }));
                Assert(error.Contains("cancelada") && p.Disables == (cancelAt == PlayerOneStage.WaitingForFreeSlot ? 0 : 1)
                    && p.Connects == (cancelAt == PlayerOneStage.WaitingForFreeSlot || cancelAt == PlayerOneStage.Connecting ? 0 : 1)
                    && p.Enables == (cancelAt == PlayerOneStage.WaitingForFreeSlot ? 0 : cancelAt == PlayerOneStage.WaitingForPhysical ? 2 : 1),
                    "stage cancellation escaped recovery or connected after cancellation");
            }
            var canceled = new AutomaticPreparationPort { IsCanceled = true };
            MustFail(() => AutomaticPlayerOnePreparation.Run(canceled, null));
            Assert(canceled.Inspections == 0 && canceled.Disables == 0 && canceled.Connects == 0 && canceled.Enables == 0,
                "already canceled operation accessed the backend");
        });
        Test("Automatic player one observes cancellation from native operations and keeps recovery independent", () => {
            foreach (int operation in new[] { 0, 1, 2, 3, 4 })
            {
                var p = new AutomaticPreparationPort();
                p.OnDisable = () => { if (operation == 0) p.IsCanceled = true; };
                p.OnConnect = () => { if (operation == 1) p.IsCanceled = true; };
                p.OnSlot = () => { if (operation == 2) p.IsCanceled = true; };
                p.OnEnable = () => { if (operation == 3) p.IsCanceled = true; };
                p.OnComplete = () => { if (operation == 4) p.IsCanceled = true; };
                string error = MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(error.Contains("cancelada") && p.Disables == 1 && p.Enables == (operation == 3 ? 2 : 1)
                    && p.Completions == (operation == 4 ? 1 : 0), "backend cancellation prevented physical recovery");
            }
        });
        Test("Automatic player one propagates native errors without retrying its virtual connection", () => {
            foreach (int operation in new[] { 0, 1, 2 })
            {
                var p = new AutomaticPreparationPort();
                p.OnInspect = () => { if (operation == 0) throw new InvalidOperationException("lectura fallida"); };
                p.OnConnect = () => { if (operation == 1) throw new InvalidOperationException("conexión fallida"); };
                p.OnSlot = () => { if (operation == 2) throw new InvalidOperationException("índice fallido"); };
                MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(p.Connects == (operation == 0 ? 0 : 1) && p.Enables == (operation == 0 ? 0 : 1)
                    && p.Completions == 0 && p.Waits == 0, "native error was ignored or caused an unsafe retry");
            }
        });
        Test("Automatic player one rejects a stopped backward or invalid operation clock", () => {
            var invalid = new AutomaticPreparationPort { Clock = -1 };
            MustFail(() => AutomaticPlayerOnePreparation.Run(invalid, null));
            Assert(invalid.Inspections == 0 && invalid.Disables == 0, "invalid initial clock reached devices");
            foreach (bool backwards in new[] { true, false })
            {
                var p = new AutomaticPreparationPort { Clock = 1000 };
                p.OnDisable = () => p.Environment = AutomaticPreparationState(0, 0, 0);
                p.OnWait = () => p.Clock -= backwards ? 101 : 100;
                MustFail(() => AutomaticPlayerOnePreparation.Run(p, null));
                Assert(p.Connects == 0 && p.Enables == 1 && p.Waits == 1, "invalid polling clock allowed unlimited mutation");
            }
        });
    }

    private static PlayerOneEnvironment AutomaticPreparationState(int physical, int other, params int[] slots)
    {
        var values = new bool[4]; foreach (int slot in slots) values[slot] = true;
        return new PlayerOneEnvironment { PhysicalDevices = physical, OtherDevices = other, ConnectedSlots = values };
    }

    private sealed class AutomaticPreparationPort : IAutomaticPlayerOnePort
    {
        internal PlayerOneEnvironment Environment = AutomaticPreparationState(1, 0, 0);
        internal long Clock;
        internal bool IsCanceled;
        internal int? VirtualSlot;
        internal int Disables, Connects, Enables, Completions, Inspections, Waits;
        internal Action OnDisable, OnConnect, OnEnable, OnComplete, OnInspect, OnSlot, OnWait;
        internal readonly List<string> Calls = new List<string>();
        internal readonly List<int> WaitValues = new List<int>();
        public long ElapsedMilliseconds => Clock;
        public bool Canceled => IsCanceled;
        public PlayerOneEnvironment Inspect() { Inspections++; OnInspect?.Invoke(); return Environment; }
        public void DisablePhysical() { Disables++; Calls.Add("disable"); Environment = AutomaticPreparationState(0, 0); OnDisable?.Invoke(); }
        public void ConnectVirtual() { Connects++; Calls.Add("connect"); VirtualSlot = 0;
            Environment = Environment.PhysicalDevices == 0 ? AutomaticPreparationState(0, 1, 0) : AutomaticPreparationState(1, 1, 0, 1);
            OnConnect?.Invoke(); }
        public int? ReadVirtualSlot() { OnSlot?.Invoke(); return VirtualSlot; }
        public void EnablePhysical() { Enables++; Calls.Add("enable");
            Environment = Connects == 0 ? AutomaticPreparationState(1, 0, 0) : AutomaticPreparationState(1, 1, 0, 1);
            OnEnable?.Invoke(); }
        public void CompleteRecovery() { Completions++; Calls.Add("complete"); OnComplete?.Invoke(); }
        public void Wait(int milliseconds) { Waits++; WaitValues.Add(milliseconds); Clock += milliseconds; OnWait?.Invoke(); }
    }
}
