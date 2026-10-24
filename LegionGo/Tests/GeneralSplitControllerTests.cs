// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void GeneralSplitControllerTests()
    {
        Test("General split horizontal D-pad and face positions send ABXY only to their own player", () => {
            int[] leftBits = { 4, 2, 1, 8 }, rightBits = { 8192, 32768, 4096, 16384 };
            int[] buttons = { 4096, 8192, 16384, 32768 }; // player-view down/right/left/up
            for (int i = 0; i < 4; i++) {
                GamepadFrame left, right;
                SplitControllerProfile.Transform(new GamepadFrame { Buttons = (ushort)leftBits[i] }, SplitControllerPreset.General, out left, out right);
                Assert(OnlyButtons(left, buttons[i]) && EmptyFrame(right), "D-pad sent an axis, wrong face button or crossed players");
                SplitControllerProfile.Transform(new GamepadFrame { Buttons = (ushort)rightBits[i] }, SplitControllerPreset.General, out left, out right);
                Assert(OnlyButtons(right, buttons[i]) && EmptyFrame(left), "right face position sent an axis, wrong button or crossed players");
            }
        });
        Test("General split retains rotated movement range deadzones triggers and independent aim preset", () => {
            foreach (short value in new short[] { short.MinValue, -24000, -500, 0, 500, 24000, short.MaxValue }) {
                var input = new GamepadFrame { LX = value, LY = value, RX = value, RY = value, LT = 220, RT = 60, Buttons = 1 | 4096 };
                GamepadFrame generalLeft, generalRight, isaacLeft, isaacRight;
                SplitControllerProfile.Transform(input, SplitControllerPreset.General, out generalLeft, out generalRight);
                SplitControllerProfile.Transform(input, out isaacLeft, out isaacRight);
                Assert(generalLeft.LX == isaacLeft.LX && generalLeft.LY == isaacLeft.LY
                    && generalRight.LX == isaacRight.LX && generalRight.LY == isaacRight.LY
                    && generalLeft.LT == 220 && generalRight.LT == 60, "preset changed horizontal movement or triggers");
                Assert(generalLeft.Buttons == 16384 && generalRight.Buttons == 16384
                    && generalLeft.RX == 0 && generalLeft.RY == 0 && generalRight.RX == 0 && generalRight.RY == 0,
                    "General retained the aiming stick or lost face actions");
                Assert(isaacLeft.Buttons == 0 && isaacRight.Buttons == 0 && isaacLeft.RX < 0 && isaacRight.RX < 0,
                    "General changed the existing Isaac preset");
            }
        });
        Test("General split permits face-button chords cancels impossible D-pad opposites and fully releases", () => {
            GamepadFrame left, right;
            SplitControllerProfile.Transform(new GamepadFrame { Buttons = 1 | 2 | 8 | 4096 | 32768 }, SplitControllerPreset.General, out left, out right);
            Assert(OnlyButtons(left, 32768) && OnlyButtons(right, 16384 | 8192), "General treated face buttons as opposed stick axes");
            for (int dpad = 0; dpad < 16; dpad++) for (int face = 0; face < 16; face++) {
                SplitControllerProfile.Transform(new GamepadFrame { Buttons = (ushort)(dpad | (face << 12)), LX = 25000, RX = -25000 },
                    SplitControllerPreset.General, out left, out right);
                Assert(left.RX == 0 && left.RY == 0 && right.RX == 0 && right.RY == 0, "a General combination populated RS");
            }
            SplitControllerProfile.Transform(new GamepadFrame(), SplitControllerPreset.General, out left, out right);
            Assert(EmptyFrame(left) && EmptyFrame(right), "General retained a released axis or button");
        });
        Test("General split preserves menu shoulder click and auxiliary ownership from Isaac", () => {
            foreach (int button in new[] { 16, 32, 64, 128, 256, 512 }) {
                GamepadFrame left, right, beforeLeft, beforeRight;
                var input = new GamepadFrame { Buttons = (ushort)button };
                SplitControllerProfile.Transform(input, out beforeLeft, out beforeRight);
                SplitControllerProfile.Transform(input, SplitControllerPreset.General, out left, out right);
                Assert(Protocol.Encode(left) == Protocol.Encode(beforeLeft) && Protocol.Encode(right) == Protocol.Encode(beforeRight), "General reassigned a menu/shoulder/click");
            }
            for (int i = 0; i < 7; i++) {
                GamepadFrame left, right, beforeLeft, beforeRight;
                var input = new GamepadFrame { LegionButtons = (byte)(1 << i) };
                SplitControllerProfile.Transform(input, out beforeLeft, out beforeRight);
                SplitControllerProfile.Transform(input, SplitControllerPreset.General, out left, out right);
                Assert(Protocol.Encode(left) == Protocol.Encode(beforeLeft) && Protocol.Encode(right) == Protocol.Encode(beforeRight), "General reassigned an auxiliary");
            }
        });
        Test("Unknown split preset is rejected rather than selecting a different layout", () => {
            GamepadFrame left, right;
            foreach (int preset in new[] { -1, 2, int.MaxValue })
                MustFail(() => SplitControllerProfile.Transform(new GamepadFrame(), (SplitControllerPreset)preset, out left, out right));
        });
        Test("General split IPC explicitly selects only General without power or profile writes", () => {
            var h = new Fake(); var j = new Journal(); var c = new GeneralSplitEngineController(); var engine = new ControlEngine(h, j, c);
            engine.Execute(new Request { Operation = "status" });
            Assert(c.GeneralStarts == 0 && c.IsaacStarts == 0, "status activated split");
            Assert(engine.Execute(new Request { Operation = "controller_split_general" }).Success && c.GeneralStarts == 1 && c.IsaacStarts == 0
                && h.Writes.Count == 0 && j.Saves == 0, "General fell back to Isaac or applied energy");
            Assert(engine.Execute(new Request { Operation = "controller_split" }).Success && c.IsaacStarts == 1 && c.GeneralStarts == 1,
                "legacy Isaac operation changed its preset");
            Assert(engine.Execute(new Request { Operation = "controller_stop" }).Success && c.Stops == 1, "General bypassed physical return");
        });
        Test("Both split operations reject foreign payloads invalid protocol and unsupported hardware", () => {
            foreach (string operation in new[] { "controller_split", "controller_split_general" }) for (int i = 0; i < 8; i++) {
                var h = new Fake(); var j = new Journal(); var c = new GeneralSplitEngineController(); var request = new Request { Operation = operation };
                if (i == 0) request.Profile = FullProfile();
                if (i == 1) request.Values = new[] { 1 };
                if (i == 2) request.Key = "mode";
                if (i == 3) request.Ac = true;
                if (i == 4) request.RtssAppearance = new RtssAppearance();
                if (i == 5) request.Display = new DisplayRequest();
                if (i == 6) request.Version = 99;
                if (i == 7) h.Identity.Cpu = "Other";
                Assert(!new ControlEngine(h, j, c).Execute(request).Success && c.GeneralStarts == 0 && c.IsaacStarts == 0
                    && h.Writes.Count == 0 && j.Saves == 0, "foreign split request reached a backend");
            }
        });
        Test("A helper supporting only Isaac rejects General without a silent fallback", () => {
            var h = new Fake(); var c = new SplitEngineController();
            var result = new ControlEngine(h, new Journal(), c).Execute(new Request { Operation = "controller_split_general" });
            Assert(!result.Success && c.Starts == 0 && h.Writes.Count == 0, "old split support silently selected Isaac");
        });
        Test("General split status roundtrip retains capability and diagnostics only for an active pair", () => {
            var old = Protocol.Decode<ControllerStatus>("{\"SupportsSplitController\":true,\"TwoPlayers\":true}");
            Assert(old.SupportsSplitController && !old.SupportsGeneralSplitController && !old.GeneralSplit, "legacy status fabricated General support");
            var status = Protocol.Decode<ControllerStatus>(Protocol.Encode(new ControllerStatus {
                SupportsSplitController = true, SupportsGeneralSplitController = true, GeneralSplit = true, TwoPlayers = true, Active = true,
                VirtualSlot = 0, SecondVirtualSlot = 1
            }));
            var report = DiagnosticFixture(); report.Controller = Diagnostics.Controller(status);
            Assert(status.SupportsGeneralSplitController && status.GeneralSplit && Diagnostics.SafeCopy(report, "0.1.34.0").Controller.GeneralSplit,
                "General status or diagnostic was lost");
            report.Controller.Active = false;
            Assert(!Diagnostics.SafeCopy(report, "0.1.34.0").Controller.GeneralSplit, "inactive General preset reported as current");
            status.TwoPlayers = false;
            Assert(!Diagnostics.Controller(status).GeneralSplit, "combined controller claimed General split");
        });
        Test("Explicit General cancels pending Desktop only for its immutable request and preserves startup preference", () => WithRecordingDirectory(folder => {
            var f = new StartupFixture(true); var startup = f.Create(); startup.RunAtStartup();
            var h = new Fake(); var j = new Journal(); var c = new GeneralSplitEngineController();
            using (var session = new HelperSession(new ControlEngine(h, j, c), folder, false, startup: startup)) {
                foreach (var invalid in new[] {
                    new Request { Operation = "controller_split_general", Values = new[] { 1 } },
                    new Request { Operation = "controller_split_general", Key = "mode" },
                    new Request { Operation = "controller_split_general", Ac = true }
                }) Assert(!session.Execute(invalid).Success && startup.GetStatus().State == "starting", "bad request canceled Desktop");
                Assert(session.Execute(new Request { Operation = "controller_split_general" }).Success && startup.GetStatus().State == "canceled"
                    && f.Store.Preferences.Enabled && c.GeneralStarts == 1 && c.IsaacStarts == 0 && h.Writes.Count == 0 && j.Saves == 0,
                    "General altered startup preference, selected Isaac or applied power");
            }
        }));
    }
    private static bool OnlyButtons(GamepadFrame frame, int expected)
    { var buttons = frame.Buttons; frame.Buttons = 0; return buttons == expected && EmptyFrame(frame); }
    private sealed class GeneralSplitEngineController : IControllerService, ISplitControllerService, IGeneralSplitControllerService
    {
        internal int GeneralStarts, IsaacStarts, Stops;
        public void PrepareSplitControllers() { IsaacStarts++; }
        public void PrepareGeneralSplitControllers() { GeneralStarts++; }
        public ControllerLayout Layout => new ControllerLayout();
        public ControllerStatus GetStatus() => new ControllerStatus { SupportsSplitController = true, SupportsGeneralSplitController = true };
        public void Preflight(ControllerLayout layout) { }
        public void Apply(ControllerLayout layout) { if (!layout.Enabled) Stops++; }
        public void Dispose() { }
    }
}
