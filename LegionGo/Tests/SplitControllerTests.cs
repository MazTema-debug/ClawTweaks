// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void SplitControllerTests()
    {
        Test("Split keeps analog movement triggers and shoulder input on its owning half", () => {
            GamepadFrame left, right;
            SplitControllerProfile.Transform(new GamepadFrame { LX = 25000, LY = -15000, LT = 201, Buttons = 256 }, out left, out right);
            Assert(left.LX > 0 && left.LY > 0 && left.LT == 201 && left.Buttons == 256 && EmptyFrame(right), "left horizontal input crossed halves");
            SplitControllerProfile.Transform(new GamepadFrame { RX = -25000, RY = 15000, RT = 203, Buttons = 512 }, out left, out right);
            Assert(right.LX > 0 && right.LY > 0 && right.LT == 203 && right.Buttons == 256 && EmptyFrame(left), "right horizontal input crossed halves");
        });
        Test("Split maps every shoot direction independently without leaking menu buttons", () => {
            // Player-view left/right/up/down: D-pad up/down/right/left;
            // right half A/Y/X/B when RB/RT point to the player's right.
            int[] leftBits = { 1, 2, 8, 4 }, rightBits = { 4096, 32768, 16384, 8192 };
            for (int i = 0; i < 4; i++) {
                GamepadFrame left, right;
                SplitControllerProfile.Transform(new GamepadFrame { Buttons = (ushort)(leftBits[i] | rightBits[3 - i]) }, out left, out right);
                Assert(left.Buttons == 0 && right.Buttons == 0 && left.LX == 0 && right.LX == 0, "aim pressed an action or moved character");
                Assert(AimDirection(left, i) && AimDirection(right, 3 - i), "shoot direction reversed");
            }
        });
        Test("Split opposing shoot directions cancel on both halves and release clears all output", () => {
            GamepadFrame left, right;
            SplitControllerProfile.Transform(new GamepadFrame { Buttons = 15 | 4096 | 8192 | 16384 | 32768 }, out left, out right);
            Assert(EmptyFrame(left) && EmptyFrame(right), "opposing aim stuck");
            SplitControllerProfile.Transform(new GamepadFrame { Buttons = ushort.MaxValue, LegionButtons = 127, LT = 255, RT = 255 }, out left, out right);
            SplitControllerProfile.Transform(new GamepadFrame(), out left, out right);
            Assert(EmptyFrame(left) && EmptyFrame(right), "release retained output");
        });
        Test("Split each menu and stick click belongs only to its own player", () => {
            foreach (int physical in new[] { 32, 16, 64, 128 }) {
                GamepadFrame left, right; SplitControllerProfile.Transform(new GamepadFrame { Buttons = (ushort)physical }, out left, out right);
                bool ownsLeft = physical != 128;
                ushort expected = (ushort)(physical == 32 ? 32 : physical == 16 ? 16 : 4096);
                Assert((ownsLeft ? left : right).Buttons == expected && EmptyFrame(ownsLeft ? right : left), "menu input crossed players");
            }
        });
        Test("Split each auxiliary remains assigned to the corresponding half", () => {
            int[] outputs = { 32, 32, 8192, 512, 8192, 512, 16 };
            for (int i = 0; i < 7; i++) {
                GamepadFrame left, right; SplitControllerProfile.Transform(new GamepadFrame { LegionButtons = (byte)(1 << i) }, out left, out right);
                bool ownsLeft = i == 0 || i == 2 || i == 3;
                Assert((ownsLeft ? left : right).Buttons == outputs[i] && EmptyFrame(ownsLeft ? right : left), "auxiliary crossed halves");
            }
        });
        Test("Split simultaneous analog and shooting controls remain independent", () => {
            GamepadFrame left, right;
            SplitControllerProfile.Transform(new GamepadFrame { LX = 25000, RX = -25000, Buttons = 1 | 4096, LT = 240, RT = 60 }, out left, out right);
            Assert(left.LX == 0 && right.LX == 0 && left.LY > 0 && right.LY > 0
                && left.RX < 0 && right.RX < 0 && left.RY == 0 && right.RY == 0
                && left.LT == 240 && right.LT == 60, "horizontal movement or shooting crossed players");
        });
        Test("Horizontal split sticks follow the player's four directions at full travel", () => {
            // Each row is player-view left, right, up, down. Raw axes refer to
            // the upright controller, not to the rotated shell in the hand.
            short[] leftX = { 0, 0, short.MaxValue, short.MinValue };
            short[] leftY = { short.MaxValue, short.MinValue, 0, 0 };
            short[] rightX = { 0, 0, short.MinValue, short.MaxValue };
            short[] rightY = { short.MinValue, short.MaxValue, 0, 0 };
            for (int i = 0; i < 4; i++) {
                GamepadFrame left, right;
                SplitControllerProfile.Transform(new GamepadFrame { LX = leftX[i], LY = leftY[i] }, out left, out right);
                Assert(MovementDirection(left, i) && Math.Max(Math.Abs((int)left.LX), Math.Abs((int)left.LY)) >= 32767
                    && EmptyFrame(right), "left horizontal cardinal direction or endpoint incorrect");
                SplitControllerProfile.Transform(new GamepadFrame { RX = rightX[i], RY = rightY[i] }, out left, out right);
                Assert(MovementDirection(right, i) && Math.Max(Math.Abs((int)right.LX), Math.Abs((int)right.LY)) >= 32767
                    && EmptyFrame(left), "right horizontal cardinal direction or endpoint incorrect");
            }
        });
        Test("Horizontal split diagonals retain radial range and aim cancels one opposing axis", () => {
            GamepadFrame left, right;
            SplitControllerProfile.Transform(new GamepadFrame {
                LX = short.MaxValue, LY = short.MinValue, RX = short.MinValue, RY = short.MaxValue,
                Buttons = 8 | 2 | 16384 | 32768
            }, out left, out right);
            foreach (var output in new[] { left, right }) {
                Assert(output.LX > 23000 && output.LY > 23000 && output.LX < 24000 && output.LY < 24000,
                    "diagonal rotation lost signs or radial clamping");
                Assert(output.RX == short.MaxValue && output.RY == short.MaxValue && output.Buttons == 0,
                    "horizontal diagonal aim pressed an action or reversed");
            }
            SplitControllerProfile.Transform(new GamepadFrame { Buttons = 1 | 2 | 8 | 4096 | 32768 | 16384 }, out left, out right);
            Assert(AimDirection(left, 2) && AimDirection(right, 2), "opposed horizontal aim suppressed the remaining vertical direction");
        });
        Test("Horizontal split does not rotate combined Desktop or an editable layout", () => {
            var desktop = DesktopControllerProfile.CreateLayout();
            var editable = desktop.Copy(); editable.InvertLeftY = true;
            var input = new GamepadFrame { LX = 25000, LY = 15000, RX = -25000, RY = -15000, Buttons = 1 | 32768 };
            var before = Protocol.Encode(editable);
            GamepadFrame left, right; SplitControllerProfile.Transform(input, out left, out right);
            var combined = desktop.Transform(input); var custom = editable.Transform(input);
            Assert(combined.LX > 0 && combined.LY > 0 && combined.RX < 0 && combined.RY < 0 && combined.Buttons == input.Buttons,
                "split changed the combined Desktop orientation");
            Assert(custom.LX > 0 && custom.LY < 0 && custom.RX < 0 && custom.RY < 0 && Protocol.Encode(editable) == before,
                "split changed the editable layout or its saved inversion");
        });
        Test("Left Menu and right M3 independently join pause and release their own player", () => {
            GamepadFrame left, right;
            SplitControllerProfile.Transform(new GamepadFrame { Buttons = 16 | 32, LegionButtons = 64 }, out left, out right);
            Assert(left.Buttons == (16 | 32) && right.Buttons == 16, "left View or Menu crossed into player two");
            SplitControllerProfile.Transform(new GamepadFrame { LegionButtons = 64 }, out left, out right);
            Assert(EmptyFrame(left) && right.Buttons == 16, "left release cleared right Start or left output stuck");
            SplitControllerProfile.Transform(new GamepadFrame { Buttons = 16 }, out left, out right);
            Assert(left.Buttons == 16 && EmptyFrame(right), "right release cleared left Start or right output stuck");
            SplitControllerProfile.Transform(new GamepadFrame(), out left, out right);
            Assert(EmptyFrame(left) && EmptyFrame(right), "menu controls retained output after release");
        });
        Test("Split radial deadzones and immutable baseline do not alter Desktop or stored profiles", () => {
            var first = SplitControllerProfile.CreateLayout(); first.Validate(); first.Map[0] = 0; first.LeftDeadzone = 30;
            var next = SplitControllerProfile.CreateLayout();
            Assert(next.Rumble == 0 && next.LeftDeadzone == 10 && next.Map[0] == 4096 && DesktopControllerProfile.CreateLayout().Rumble == 100, "split modified shared baseline");
            GamepadFrame left, right; SplitControllerProfile.Transform(new GamepadFrame { LX = 50, RY = -500 }, out left, out right);
            Assert(EmptyFrame(left) && EmptyFrame(right), "split stick noise reached a player");
        });
        Test("Split reservation frees P2 even when P1 was already free", () => {
            var f = new ModeP1Fixture(targets: 2) { PhysicalSlot = 1 };
            f.OnConnect = () => Assert(f.Mode.Mode == 2 && f.Journal, "connected before source vacated both slots");
            f.OnRestore = () => Assert(f.Connected && f.Port.ReadVirtualSlots().SequenceEqual(new int?[] { 0, 1 }), "restored before both targets reserved");
            AutomaticPlayerOnePreparation.Run(f.Port, null, 2);
            Assert(f.Enters == 1 && f.Connects == 1 && f.Restores == 1 && f.Completions == 1 && !f.Journal, "P2 remained physical");
        });
        Test("Split reservation does not cycle a physical source already outside P1 and P2", () => {
            var f = new ModeP1Fixture(targets: 2) { PhysicalSlot = 2 };
            AutomaticPlayerOnePreparation.Run(f.Port, null, 2);
            Assert(f.Enters == 0 && f.ModeReads == 0 && f.Restores == 0 && f.Connects == 1, "free pair changed receiver mode");
        });
        Test("Split waits for both target indices before restoring physical input", () => {
            var f = new ModeP1Fixture(targets: 2) { VirtualSlotsOverride = new int?[] { 0, null } };
            f.OnWait = () => { Assert(f.Restores == 0, "restored before second index"); f.VirtualSlotsOverride = new int?[] { 0, 1 }; };
            AutomaticPlayerOnePreparation.Run(f.Port, null, 2);
            Assert(f.Clock == 100 && f.Restores == 1 && f.Completions == 1, "did not wait for second index");
        });
        Test("Split rejects swapped duplicate or foreign virtual indices and still restores the source", () => {
            foreach (int?[] indices in new[] { new int?[] { 0, 2 }, new int?[] { 1, 0 }, new int?[] { 0, 0 }, new int?[] { -1, 1 }, null, new int?[] { 0 } }) {
                var f = new ModeP1Fixture(targets: 2);
                f.VirtualSlotsOverride = indices;
                f.NullSlotRead = indices == null;
                MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null, 2));
                Assert(f.Enters == 1 && f.Restores == 1 && f.Completions == 0 && f.Journal, "bad second index bypassed recovery");
            }
        });
        Test("Split partial connection failure recovers physical input and never announces readiness", () => {
            var f = new ModeP1Fixture(targets: 2); f.OnConnect = () => { throw new InvalidOperationException("second connect failed"); };
            string message = MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null, 2));
            Assert(message.Contains("second connect failed") && f.Connects == 1 && f.Restores == 1 && f.Completions == 0, "partial connection falsely ready");
        });
        Test("Split missing second index expires within the shared eight second connection budget", () => {
            var f = new ModeP1Fixture(targets: 2) { VirtualSlotsOverride = new int?[] { 0, null } };
            string message = MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null, 2));
            Assert(message.Contains("8 segundos") && f.Clock == 8000 && f.Restores == 1 && f.Completions == 0, "second index retried without deadline");
        });
        Test("Split cancellation and foreign arrival never retire the physical recovery journal", () => {
            foreach (bool cancel in new[] { true, false }) {
                var f = new ModeP1Fixture(targets: 2);
                f.OnConnect = () => { if (cancel) f.CanceledValue = true; else f.OverrideEnvironment = AutomaticPreparationState(0, 3, 0, 1, 2); };
                MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null, 2));
                Assert(f.Restores == 1 && f.Completions == 0 && f.Journal, "cancellation or third device ignored");
            }
        });
        Test("Split count and slot-query support are validated before touching any backend", () => {
            foreach (int count in new[] { 0, 2, 3 }) {
                var p = new AutomaticPreparationPort(); MustFail(() => AutomaticPlayerOnePreparation.Run(p, null, count));
                Assert(p.Inspections == 0 && p.Disables == 0 && p.Connects == 0, "invalid target count reached devices");
            }
        });
        Test("Split requires neutral controls at initial activation as well as on recovery", () => {
            var p = new ControllerResume(new ControllerPowerState(), new ResumeFake(), 0, waitForNeutral: true);
            Assert(!p.AcceptInput(new GamepadFrame { Buttons = 16 }, SplitControllerProfile.CreateLayout()), "initial held start joined a player");
            Assert(p.AcceptInput(new GamepadFrame(), SplitControllerProfile.CreateLayout())
                && p.AcceptInput(new GamepadFrame { Buttons = 16 }, SplitControllerProfile.CreateLayout()), "fresh start did not rearm");
        });
        Test("Split IPC is explicit immutable and makes no power writes or saved originals", () => {
            var h = new Fake(); var j = new Journal(); var c = new SplitEngineController(); var engine = new ControlEngine(h, j, c);
            engine.Execute(new Request { Operation = "status" }); Assert(c.Starts == 0, "status started split");
            Assert(engine.Execute(new Request { Operation = "controller_split" }).Success && c.Starts == 1 && h.Writes.Count == 0 && j.Saves == 0, "split changed power");
            Assert(engine.Execute(new Request { Operation = "controller_stop" }).Success && c.Stops == 1, "stop did not use common physical return");
        });
        Test("Split rejects wrong protocol mutable payload unsupported machine and old helper", () => {
            for (int i = 0; i < 5; i++) {
                var h = new Fake(); var c = new SplitEngineController(); var request = new Request { Operation = "controller_split" };
                if (i == 0) request.Version = 99;
                if (i == 1) request.Profile = FullProfile();
                if (i == 2) request.Values = new[] { 1 };
                if (i == 3) h.Identity.Cpu = "Other";
                IControllerService controller = i == 4 ? (IControllerService)new FakeController() : c;
                Assert(!new ControlEngine(h, new Journal(), controller).Execute(request).Success && c.Starts == 0 && h.Writes.Count == 0, "invalid split request reached native preparation");
            }
        });
        Test("Split diagnostics preserve two bounded player numbers without exporting input", () => {
            var report = DiagnosticFixture(); report.Controller = Diagnostics.Controller(new ControllerStatus { Active = true, TwoPlayers = true, VirtualSlot = 0, SecondVirtualSlot = 1 });
            var copy = Diagnostics.SafeCopy(report, "0.1.28.0");
            Assert(copy.Controller.TwoPlayers && copy.Controller.SecondVirtualPlayer == 2 && copy.Controller.VirtualPlayer == 1, "second player missing");
            report.Controller.SecondVirtualPlayer = 99; copy = Diagnostics.SafeCopy(report, "0.1.28.0");
            Assert(copy.Controller.SecondVirtualPlayer == null, "invalid second player exported");
            report.Controller.Active = false; report.Controller.SecondVirtualPlayer = 2;
            Assert(Diagnostics.SafeCopy(report, "0.1.28.0").Controller.SecondVirtualPlayer == null, "inactive index reported as current");
        });
        Test("Explicit split cancels pending Desktop without changing startup preference or applying energy", () => WithRecordingDirectory(folder => {
            var f = new StartupFixture(true); var startup = f.Create(); startup.RunAtStartup();
            var h = new Fake(); var j = new Journal(); var c = new SplitEngineController();
            using (var session = new HelperSession(new ControlEngine(h, j, c), folder, false, startup: startup)) {
                var invalid = session.Execute(new Request { Operation = "controller_split", Values = new[] { 1 } });
                Assert(!invalid.Success && startup.GetStatus().State == "starting", "bad payload canceled unrelated startup");
                Assert(session.Execute(new Request { Operation = "controller_split" }).Success && startup.GetStatus().State == "canceled"
                    && f.Store.Preferences.Enabled && f.Starts == 1 && c.Starts == 1 && h.Writes.Count == 0 && j.Saves == 0, "Desktop startup replaced explicit split");
            }
        }));
    }
    private static bool EmptyFrame(GamepadFrame f) => f.Buttons == 0 && f.LX == 0 && f.LY == 0 && f.RX == 0 && f.RY == 0 && f.LT == 0 && f.RT == 0 && f.LegionButtons == 0;
    private static bool AimDirection(GamepadFrame f, int i) => i == 0 ? f.RX < 0 && f.RY == 0 : i == 1 ? f.RX > 0 && f.RY == 0 : i == 2 ? f.RY > 0 && f.RX == 0 : f.RY < 0 && f.RX == 0;
    private static bool MovementDirection(GamepadFrame f, int i) => AimDirection(new GamepadFrame { RX = f.LX, RY = f.LY }, i);
    private sealed class SplitEngineController : IControllerService, ISplitControllerService
    {
        internal int Starts, Stops;
        public void PrepareSplitControllers() { Starts++; }
        public ControllerLayout Layout => new ControllerLayout();
        public ControllerStatus GetStatus() => new ControllerStatus();
        public void Preflight(ControllerLayout layout) { }
        public void Apply(ControllerLayout layout) { if (!layout.Enabled) Stops++; }
        public void Dispose() { }
    }
}
