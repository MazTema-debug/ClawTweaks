// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void PlayerOnePreparationTests()
    {
        Test("Player one connects directly when its slot is free without requesting FPS", () => {
            var p = new PreparationPort { Environment = PreparationState(1, 0, 1) };
            p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(1, 1, 0, 1); };
            var stages = new List<PlayerOneStage>(); PlayerOnePreparation.Run(p, stages.Add);
            Assert(p.Connects == 1 && p.Waits == 0 && stages.SequenceEqual(new[] {
                PlayerOneStage.Connecting, PlayerOneStage.WaitingForPhysical, PlayerOneStage.Ready }), "free P1 requested an unnecessary hardware transition");
        });
        Test("Player one waits for FPS to release every slot and for the physical source to return", () => {
            var p = new PreparationPort { Environment = PreparationState(1, 0, 0) };
            p.OnWait = () => {
                if (p.Connects == 0) p.Environment = p.Waits == 1 ? PreparationState(0, 0, 0) : PreparationState(0, 0);
                else p.Environment = PreparationState(1, 1, 0, 1);
            };
            p.OnConnect = () => { Assert(!p.Environment.ConnectedSlots.Any(x => x), "connected before cached source slot disappeared");
                p.VirtualSlot = 0; p.Environment = PreparationState(0, 1, 0); };
            var stages = new List<PlayerOneStage>(); PlayerOnePreparation.Run(p, stages.Add);
            Assert(p.Connects == 1 && p.Waits == 3 && stages.SequenceEqual(new[] { PlayerOneStage.WaitingForFreeSlot,
                PlayerOneStage.Connecting, PlayerOneStage.WaitingForPhysical, PlayerOneStage.Ready }), "manual FPS sequence or state reporting differs");
            Assert(p.WaitValues.All(x => x == 100), "polling did not use bounded 100ms waits");
        });
        Test("Player one accepts FPS already on and connects before waiting for the source", () => {
            var p = new PreparationPort { Environment = PreparationState(0, 0) };
            p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(0, 1, 0); };
            p.OnWait = () => p.Environment = PreparationState(1, 1, 0, 2);
            var stages = new List<PlayerOneStage>(); PlayerOnePreparation.Run(p, stages.Add);
            Assert(p.Connects == 1 && p.Waits == 1 && !stages.Contains(PlayerOneStage.WaitingForFreeSlot), "already free slot was not used promptly");
        });
        Test("Player one refuses ambiguous physical sources and foreign devices before connecting", () => {
            foreach (var environment in new[] { PreparationState(2, 0, 0), PreparationState(1, 1, 0),
                PreparationState(0, 0, 1), PreparationState(1, 0, 0, 1) })
            {
                var p = new PreparationPort { Environment = environment };
                MustFail(() => PlayerOnePreparation.Run(p, null)); Assert(p.Connects == 0, "ambiguous initial state connected a virtual target");
            }
        });
        Test("Player one rejects missing invalid slot arrays and unknown device counts", () => {
            var invalid = new[] { null, new PlayerOneEnvironment(),
                new PlayerOneEnvironment { ConnectedSlots = new bool[3] },
                new PlayerOneEnvironment { ConnectedSlots = new bool[5] }, PreparationState(-1, 0), PreparationState(0, -1) };
            foreach (var environment in invalid)
            {
                var p = new PreparationPort { Environment = environment };
                MustFail(() => PlayerOnePreparation.Run(p, null)); Assert(p.Connects == 0, "invalid snapshot reached ConnectVirtual");
            }
        });
        Test("Player one aborts if another controller arrives while waiting for FPS", () => {
            var p = new PreparationPort { Environment = PreparationState(1, 0, 0) };
            p.OnWait = () => p.Environment = PreparationState(1, 1, 0, 1);
            MustFail(() => PlayerOnePreparation.Run(p, null));
            Assert(p.Connects == 0 && p.Waits == 1, "foreign arrival was ignored while waiting");
        });
        Test("Player one rechecks the free slot after progress before its only connection", () => {
            var p = new PreparationPort { Environment = PreparationState(0, 0) };
            MustFail(() => PlayerOnePreparation.Run(p, stage => {
                if (stage == PlayerOneStage.Connecting) p.Environment = PreparationState(1, 0, 0);
            }));
            Assert(p.Connects == 0, "stale free-slot observation authorized connection");
        });
        Test("Player one never retries or accepts a different or out of range virtual slot", () => {
            foreach (int value in new[] { -1, 1, 2, 3, 4 })
            {
                var p = new PreparationPort { Environment = PreparationState(0, 0) };
                p.OnConnect = () => { p.VirtualSlot = value; p.Environment = PreparationState(0, 1, 0); };
                MustFail(() => PlayerOnePreparation.Run(p, null)); Assert(p.Connects == 1 && p.Waits == 0, "wrong virtual slot was accepted or retried");
            }
        });
        Test("Player one waits for a confirmed target index and actual XInput slot presence", () => {
            var p = new PreparationPort { Environment = PreparationState(0, 0) };
            p.OnConnect = () => p.Environment = PreparationState(0, 1);
            p.OnWait = () => {
                if (p.Waits == 1) p.VirtualSlot = 0;
                else p.Environment = PreparationState(1, 1, 0, 1);
            };
            PlayerOnePreparation.Run(p, null);
            Assert(p.Connects == 1 && p.Waits == 2, "unconfirmed index or absent slot was treated as ready");
        });
        Test("Player one index confirmation has its own eight second timeout", () => {
            foreach (bool slotVisible in new[] { false, true })
            {
                var p = new PreparationPort { Environment = PreparationState(0, 0) };
                p.OnConnect = () => { p.Environment = slotVisible ? PreparationState(0, 1, 0) : PreparationState(0, 1);
                    p.VirtualSlot = slotVisible ? (int?)null : 0; };
                string error = MustFail(() => PlayerOnePreparation.Run(p, null));
                Assert(p.Connects == 1 && p.Clock == 8000 && error.Contains("8 segundos") && error.Contains("FPS en OFF"), "connection timeout was unbounded or lost recovery instruction");
            }
        });
        Test("Player one never connects if the source does not free its XInput slot", () => {
            var p = new PreparationPort { Environment = PreparationState(1, 0, 0), Clock = 50000 };
            string error = MustFail(() => PlayerOnePreparation.Run(p, null));
            Assert(p.Connects == 0 && p.Clock == 140000 && error.Contains("90 segundos") && error.Contains("FPS en OFF"), "waiting for FPS connected early or had an absolute rather than operation deadline");
        });
        Test("Player one source return shares the total ninety second budget", () => {
            var p = new PreparationPort { Environment = PreparationState(1, 0, 0) };
            p.OnWait = () => { if (p.Connects == 0 && p.Clock >= 89000) p.Environment = PreparationState(0, 0); };
            p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(0, 1, 0); };
            string error = MustFail(() => PlayerOnePreparation.Run(p, null));
            Assert(p.Connects == 1 && p.Clock == 90000 && error.Contains("90 segundos"), "later stage reset the total deadline");
        });
        Test("Player one never announces ready while physical or virtual enumeration is incomplete", () => {
            foreach (var pending in new[] { PreparationState(0, 1, 0), PreparationState(1, 0, 0, 1), PreparationState(1, 1, 0) })
            {
                var p = new PreparationPort { Environment = PreparationState(0, 0) };
                p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = pending; };
                var stages = new List<PlayerOneStage>(); MustFail(() => PlayerOnePreparation.Run(p, stages.Add));
                Assert(!stages.Contains(PlayerOneStage.Ready) && p.Clock == 90000, "partial enumeration was accepted");
            }
        });
        Test("Player one tolerates XInput source return before SetupAPI enumeration without early readiness", () => {
            var p = new PreparationPort { Environment = PreparationState(0, 0) };
            p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(0, 1, 0, 1); };
            p.OnWait = () => p.Environment = PreparationState(1, 1, 0, 1);
            var stages = new List<PlayerOneStage>(); PlayerOnePreparation.Run(p, stage => {
                if (stage == PlayerOneStage.Ready) Assert(p.Waits == 1, "ready accepted before source enumeration caught up");
                stages.Add(stage);
            });
            Assert(p.Connects == 1 && p.Waits == 1 && stages.Last() == PlayerOneStage.Ready,
                "separate XInput and SetupAPI observations were incorrectly treated as an unknown third controller");
        });
        Test("Player one rejects foreign arrivals during connection and source return", () => {
            foreach (bool duringConnection in new[] { true, false })
            foreach (bool thirdInterfaceVisible in new[] { true, false })
            {
                var p = new PreparationPort { Environment = PreparationState(0, 0) };
                p.OnConnect = () => { p.VirtualSlot = duringConnection ? (int?)null : 0; p.Environment = PreparationState(0, 1, 0); };
                p.OnWait = () => p.Environment = PreparationState(1, thirdInterfaceVisible ? 2 : 1, 0, 1, 2);
                MustFail(() => PlayerOnePreparation.Run(p, null));
                Assert(p.Connects == 1 && p.Waits == 1, "foreign arrival was tolerated or caused a reconnect");
            }
        });
        Test("Player one verifies the ready arrangement again before returning", () => {
            var p = new PreparationPort { Environment = PreparationState(0, 0) }; int readyInspections = 0;
            p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(1, 1, 0, 1); };
            p.OnInspect = () => {
                if (p.Connects == 1 && ++readyInspections == 3) p.Environment = PreparationState(0, 1, 0);
            };
            p.OnWait = () => { p.IsCanceled = true; };
            var stages = new List<PlayerOneStage>(); MustFail(() => PlayerOnePreparation.Run(p, stages.Add));
            Assert(!stages.Contains(PlayerOneStage.Ready), "stale readiness survived a disappearing physical source");
        });
        Test("Player one supports cancellation before all stages and never connects after cancellation", () => {
            foreach (var cancelAt in new[] { PlayerOneStage.WaitingForFreeSlot, PlayerOneStage.Connecting,
                PlayerOneStage.WaitingForPhysical, PlayerOneStage.Ready })
            {
                var p = new PreparationPort { Environment = cancelAt == PlayerOneStage.WaitingForFreeSlot ? PreparationState(1, 0, 0) : PreparationState(0, 0) };
                p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(1, 1, 0, 1); };
                string error = MustFail(() => PlayerOnePreparation.Run(p, stage => { if (stage == cancelAt) p.IsCanceled = true; }));
                Assert(p.Connects == (cancelAt == PlayerOneStage.WaitingForFreeSlot || cancelAt == PlayerOneStage.Connecting ? 0 : 1)
                    && error.Contains("cancelada") && error.Contains("FPS en OFF"), "stage cancellation was ignored");
            }
            var canceled = new PreparationPort { IsCanceled = true }; MustFail(() => PlayerOnePreparation.Run(canceled, null));
            Assert(canceled.Inspections == 0 && canceled.Connects == 0, "already canceled preparation accessed the backend");
        });
        Test("Player one observes cancellation from inspection connection and target query", () => {
            foreach (int operation in new[] { 0, 1, 2 })
            {
                var p = new PreparationPort { Environment = PreparationState(0, 0) };
                p.OnInspect = () => { if (operation == 0) p.IsCanceled = true; };
                p.OnConnect = () => { p.VirtualSlot = 0; p.Environment = PreparationState(1, 1, 0, 1); if (operation == 1) p.IsCanceled = true; };
                p.OnSlot = () => { if (operation == 2) p.IsCanceled = true; };
                var stages = new List<PlayerOneStage>(); MustFail(() => PlayerOnePreparation.Run(p, stages.Add));
                Assert(p.Connects == (operation == 0 ? 0 : 1) && !stages.Contains(PlayerOneStage.Ready), "backend cancellation was not checked promptly");
            }
        });
        Test("Player one fails on native inspection connection and index errors without retrying", () => {
            foreach (int operation in new[] { 0, 1, 2 })
            {
                var p = new PreparationPort { Environment = PreparationState(0, 0) };
                p.OnInspect = () => { if (operation == 0) throw new InvalidOperationException("lectura no disponible"); };
                p.OnConnect = () => { if (operation == 1) throw new InvalidOperationException("conexión rechazada"); p.Environment = PreparationState(0, 1, 0); };
                p.OnSlot = () => { if (operation == 2) throw new InvalidOperationException("puesto no disponible"); };
                MustFail(() => PlayerOnePreparation.Run(p, null));
                Assert(p.Connects == (operation == 0 ? 0 : 1) && p.Waits == 0, "backend error was masked or retried");
            }
        });
        Test("Player one requires a valid advancing monotonic clock", () => {
            var negative = new PreparationPort { Clock = -1 }; MustFail(() => PlayerOnePreparation.Run(negative, null));
            Assert(negative.Inspections == 0 && negative.Connects == 0, "invalid clock reached input APIs");
            foreach (bool backwards in new[] { false, true })
            {
                var p = new PreparationPort { Environment = PreparationState(1, 0, 0), Clock = 1000 };
                p.OnWait = () => p.Clock -= backwards ? 101 : 100;
                MustFail(() => PlayerOnePreparation.Run(p, null)); Assert(p.Connects == 0 && p.Waits == 1, "stopped or backward clock caused an unlimited wait");
            }
        });
    }

    private static PlayerOneEnvironment PreparationState(int physical, int other, params int[] slots)
    {
        var values = new bool[4]; foreach (int slot in slots) values[slot] = true;
        return new PlayerOneEnvironment { PhysicalDevices = physical, OtherDevices = other, ConnectedSlots = values };
    }

    private sealed class PreparationPort : IPlayerOnePort
    {
        internal PlayerOneEnvironment Environment = PreparationState(0, 0);
        internal long Clock;
        internal bool IsCanceled;
        internal int? VirtualSlot;
        internal int Connects, Waits, Inspections;
        internal Action OnConnect, OnWait, OnInspect, OnSlot;
        internal readonly List<int> WaitValues = new List<int>();
        public long ElapsedMilliseconds => Clock;
        public bool Canceled => IsCanceled;
        public PlayerOneEnvironment Inspect() { Inspections++; OnInspect?.Invoke(); return Environment; }
        public void ConnectVirtual() { Connects++; OnConnect?.Invoke(); }
        public int? ReadVirtualSlot() { OnSlot?.Invoke(); return VirtualSlot; }
        public void Wait(int milliseconds) { Waits++; WaitValues.Add(milliseconds); Clock += milliseconds; OnWait?.Invoke(); }
    }
}
