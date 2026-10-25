// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void PhysicalPlayerOneReturnTests()
    {
        Test("A physical controller already in player one requires no mode cycle", () => {
            var port = new PhysicalReturnFake { Before = new[] { true, false, false, false } };
            Assert(PhysicalPlayerOneReturn.Run(port) == 0 && port.Checks == 1 && port.Cycles == 0, "unchanged physical slot caused a mode write");
        });
        Test("A lone physical controller in a later slot gets one verified supervised cycle", () => {
            foreach (int slot in new[] { 1, 2, 3 }) {
                var before = new bool[4]; before[slot] = true;
                var port = new PhysicalReturnFake { Before = before };
                Assert(PhysicalPlayerOneReturn.Run(port) == 0 && port.Cycles == 1 && port.Reads == 2, "later physical slot was not verified after a single cycle");
            }
        });
        Test("Missing ambiguous or malformed physical slot observations never authorize a cycle", () => {
            foreach (var before in new[] { null, new bool[3], new bool[5], new bool[4], new[] { true, true, false, false } }) {
                var port = new PhysicalReturnFake { Before = before };
                MustFail(() => PhysicalPlayerOneReturn.Run(port));
                Assert(port.Cycles == 0, "invalid observation reached mode switching");
            }
        });
        Test("Physical return preserves guard failures without touching a device", () => {
            var port = new PhysicalReturnFake { GuardFailure = true };
            MustFail(() => PhysicalPlayerOneReturn.Run(port));
            Assert(port.Cycles == 0 && port.Reads == 0, "failed machine game or ownership guard continued");
        });
        Test("Unconfirmed physical return and a failed cycle cannot trigger a second cycle", () => {
            foreach (var after in new[] { null, new bool[4], new[] { false, true, false, false }, new[] { true, true, false, false } }) {
                var port = new PhysicalReturnFake { After = after };
                MustFail(() => PhysicalPlayerOneReturn.Run(port));
                Assert(port.Cycles == 1, "unconfirmed return was repeated or reported success");
            }
            var failed = new PhysicalReturnFake { CycleFailure = true };
            MustFail(() => PhysicalPlayerOneReturn.Run(failed));
            Assert(failed.Cycles == 1 && failed.Reads == 1, "cycle failure was hidden or repeated");
        });
        Test("Only the explicit physical return command uses the dedicated return operation", () => {
            var hardware = new Fake(); var journal = new Journal(); var controller = new PhysicalReturnControllerFake();
            var engine = new ControlEngine(hardware, journal, controller);
            Assert(engine.Execute(new Request { Operation = "controller_stop" }).Success && controller.Returns == 1 && controller.Applies == 0,
                "explicit return bypassed its dedicated operation");
            engine.Execute(new Request { Operation = "status" });
            Assert(controller.Returns == 1 && hardware.Writes.Count == 0 && journal.Saves == 0, "status retried return or changed energy");
            foreach (var invalid in new[] {
                new Request { Operation = "controller_stop", Key = "Mode" },
                new Request { Operation = "controller_stop", Values = new[] { 1 } },
                new Request { Operation = "controller_stop", Ac = true },
                new Request { Operation = "controller_stop", GameBarInput = new GameBarInputContext() } })
                Assert(!engine.Execute(invalid).Success && controller.Returns == 1, "mixed command authorized physical reconnection");
        });
    }
    private sealed class PhysicalReturnFake : IPhysicalPlayerOneReturnPort
    {
        internal bool[] Before = { false, false, true, false }, After = { true, false, false, false };
        internal int Checks, Reads, Cycles;
        internal bool GuardFailure, CycleFailure;
        public void CheckInitial() { Checks++; if (GuardFailure) throw new InvalidOperationException("guard rejected"); }
        public bool[] ReadSlots() { Reads++; return Cycles == 0 ? Before : After; }
        public void Cycle() { Cycles++; if (CycleFailure) throw new InvalidOperationException("cycle failed after independent recovery"); }
    }
    private sealed class PhysicalReturnControllerFake : IControllerService, IPhysicalControllerReturn
    {
        internal int Returns, Applies;
        public ControllerLayout Layout => new ControllerLayout();
        public ControllerStatus GetStatus() => new ControllerStatus();
        public void Preflight(ControllerLayout layout) { }
        public void Apply(ControllerLayout layout) { Applies++; }
        public void ReturnPhysical() { Returns++; }
        public void Dispose() { }
    }
}
