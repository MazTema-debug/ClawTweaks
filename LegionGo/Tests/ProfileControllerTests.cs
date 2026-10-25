// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static ProfileDefinition FullProfile() => new ProfileDefinition { Id = Guid.NewGuid().ToString("D"), Name = "Test", Mode = 255,
        Limits = new[] { 12, 14, 16 }, BoostAC = 2, BoostDC = 0, EppAC = 50, EppDC = 80, WindowsMode = 0,
        Controller = new ControllerLayout { Enabled = true } };
    private static void ProfileControllerTests()
    {
        Test("Legacy profiles migrate without silently adding CPU or controller changes", () => {
            var c = ProfileCatalog.Migrate("[{\"Name\":\"Ahorro\",\"Limits\":[10,10,10]}]");
            var p = c.Profiles.Single(); Assert(p.Mode == 255 && p.Limits[0] == 10 && p.Controller == null && p.BoostAC == null && p.EppDC == null, "migration changed behavior");
            Assert(ProfileCatalog.Migrate("[]").Profiles.Count == 0, "empty migration");
        });
        Test("Profile catalogue updates stable IDs and refuses duplicate names and malformed data", () => {
            var first = FullProfile(); var c = new ProfileCatalog().Upsert(first); var renamed = first.Copy(); renamed.Name = "Renamed";
            var next = c.Upsert(renamed); Assert(next.Profiles.Count == 1 && next.Profiles[0].Name == "Renamed" && c.Profiles[0].Name == "Test", "rename mutated old catalogue");
            var duplicate = first.Copy(); duplicate.Id = Guid.NewGuid().ToString(); bool rejected = false;
            try { c.Upsert(duplicate); } catch (ArgumentException) { rejected = true; } Assert(rejected, "duplicate accepted");
            first.Controller.Map[0] = int.MaxValue; rejected = false;
            try { c.Upsert(first); } catch (ArgumentException) { rejected = true; } Assert(rejected, "invalid map accepted");
        });
        Test("Twenty complete profiles fit bounded storage and roundtrip", () => {
            var c = new ProfileCatalog(); for (int i = 0; i < 20; i++) { var p = FullProfile(); p.Name += i; c = c.Upsert(p); }
            var text = Protocol.Encode(c); Assert(text.Length < Protocol.MaxMessageLength, "catalogue exceeds protocol");
            var copy = Protocol.Decode<ProfileCatalog>(text); copy.Validate(); Assert(copy.Profiles[19].Controller.Enabled, "controller lost");
            bool rejected = false; try { c.Upsert(FullProfile()); } catch (ArgumentException) { rejected = true; } Assert(rejected, "21st profile accepted");
        });
        Test("Whole profile applies AC DC mode limits and layout with durable originals", () => {
            var h = new Fake(); var j = new Journal(); var c = new FakeController(); var p = FullProfile();
            var r = new ControlEngine(h, j, c).Execute(new Request { Operation = "profile_apply", Profile = p });
            Assert(r.Success && h.Data["Mode"] == 255 && h.Data["Spl"] == 12 && h.Data["Fppt"] == 16, r.Message);
            Assert(h.Cpu(Setting.Epp, true, "plan-A") == 50 && h.Cpu(Setting.Epp, false, "plan-A") == 80 && h.Cpu(Setting.CpuBoost, false, "plan-A") == 0, "CPU settings");
            Assert(c.Layout.Enabled && j.Values.Count == 9 && j.Saves == 1, "profile journal/layout");
        });
        Test("Controller preflight failure prevents every power write", () => {
            var h = new Fake(); var j = new Journal(); var c = new FakeController { FailPreflight = true };
            var r = new ControlEngine(h, j, c).Execute(new Request { Operation = "profile_apply", Profile = FullProfile() });
            Assert(!r.Success && h.Writes.Count == 0 && j.Saves == 0, "preflight was too late");
        });
        Test("Late controller failure rolls power back including custom tuple and exact boost modes", () => {
            var h = new Fake { ResetLimitsOnMode = true }; h.Data["Mode"] = 255;
            var c = new FakeController { FailApplyOnce = true }; var e = new ControlEngine(h, new Journal(), c);
            var r = e.Execute(new Request { Operation = "profile_apply", Profile = FullProfile() });
            Assert(!r.Success && h.Data["Mode"] == 255 && h.Data["Spl"] == 15 && h.Data["Sppt"] == 20 && h.Data["Fppt"] == 25, "tuple rollback");
            Assert(h.Cpu(Setting.CpuBoost, true, "plan-A") == 4 && h.Cpu(Setting.CpuBoost, false, "plan-A") == 4 && !c.Layout.Enabled, "rollback changed original Boost/controller");
        });
        Test("Profile CPU failure reverses earlier power changes without touching the controller", () => {
            var h = new Fake { FailOnce = Setting.Epp }; h.Data["Mode"] = 255; var c = new FakeController();
            var r = new ControlEngine(h, new Journal(), c).Execute(new Request { Operation = "profile_apply", Profile = FullProfile() });
            Assert(!r.Success && h.Data["Spl"] == 15 && c.Applies == 0 && h.Cpu(Setting.CpuBoost, true, "plan-A") == 4, "partial profile remained");
        });
        Test("Profile backup failure prevents hardware and controller activation", () => {
            var h = new Fake(); var c = new FakeController(); var r = new ControlEngine(h, new Journal { FailSave = true }, c).Execute(new Request { Operation = "profile_apply", Profile = FullProfile() });
            Assert(!r.Success && h.Writes.Count == 0 && c.Applies == 0, "writes without backup");
        });
        Test("Legacy TDP-only profile preserves current controller and CPU settings", () => {
            var h = new Fake(); var c = new FakeController(); c.Apply(new ControllerLayout { Enabled = true });
            var p = ProfileCatalog.Migrate("[{\"Name\":\"Old\",\"Limits\":[10,10,10]}]").Profiles[0];
            var r = new ControlEngine(h, new Journal(), c).Execute(new Request { Operation = "profile_apply", Profile = p });
            Assert(r.Success && c.Applies == 1 && c.Layout.Enabled && h.Cpu(Setting.CpuBoost, false, "plan-A") == 4, "legacy apply changed extra settings");
        });
        Test("Malformed profile fails before any writes on supported and unsupported hardware", () => {
            foreach (bool supported in new[] { true, false }) {
                var h = new Fake(); if (!supported) h.Identity.Cpu = "Other"; var p = FullProfile(); p.Limits[0] = 50;
                var r = new ControlEngine(h, new Journal()).Execute(new Request { Operation = "profile_apply", Profile = p });
                Assert(!r.Success && h.Writes.Count == 0, "bad profile wrote hardware"); }
        });
        Test("Mapping uses original input simultaneously and clears released buttons", () => {
            var map = new ControllerLayout(); map.Map[0] = 8192; map.Map[1] = 4096;
            Assert(map.Transform(new GamepadFrame { Buttons = 4096 }).Buttons == 8192, "swap chained");
            Assert(map.Transform(new GamepadFrame { Buttons = 12288 }).Buttons == 12288, "simultaneous buttons lost");
            Assert(map.Transform(new GamepadFrame()).Buttons == 0, "stuck button");
            map.Map[0] = map.Map[1] = 16384; Assert(map.Transform(new GamepadFrame { Buttons = 12288 }).Buttons == 16384, "many-to-one mapping");
            Assert(map.Transform(new GamepadFrame { Buttons = 15 }).Buttons == 0, "opposed dpad");
        });
        Test("Stick radial deadzone inversion trigger swapping and range edges", () => {
            var map = new ControllerLayout { InvertLeftY = true, SwapTriggers = true };
            var result = map.Transform(new GamepadFrame { LX = 100, LY = -100, LT = 17, RT = 255 });
            Assert(result.LX == 0 && result.LY == 0 && result.LT == 255 && result.RT == 17, "deadzone/trigger swap");
            result = map.Transform(new GamepadFrame { LY = short.MinValue }); Assert(result.LY == short.MaxValue, "overflow at negative extreme");
            map.SwapSticks = true; result = map.Transform(new GamepadFrame { RX = short.MinValue }); Assert(result.LX == short.MinValue && result.RX == 0, "stick swap");
        });
        Test("HidHide lease preserves foreign entries and restores only its own changes", () => {
            var c = new FakeHide { Active = true, Devices = new[] { "other" }, Applications = new[] { "other-app" } };
            var lease = new HideLease { Device = "pad", Application = "helper", AddedApplication = true, ActiveBefore = true };
            lease.Activate(c); c.Devices = c.Devices.Concat(new[] { "new-foreign" }).ToArray(); lease.Restore(c);
            Assert(c.Active && c.Devices.SequenceEqual(new[] { "other", "new-foreign" }) && c.Applications.SequenceEqual(new[] { "other-app" }), "foreign configuration lost");
        });
        Test("Early physical hiding is reverified against its exact owner and identity without configuration writes", () => {
            var config = new FakeHide { Active = true, Devices = new[] { "pad", "foreign" }, Applications = new[] { "helper", "foreign-app" } };
            var lease = new HideLease { Device = "pad", Application = "helper", OwnerPid = 123, OwnerStarted = 456 };
            lease.VerifyActive(config, "PAD", "HELPER", 123, 456);
            foreach (Action verify in new Action[] {
                () => lease.VerifyActive(config, "different-pad", "helper", 123, 456),
                () => lease.VerifyActive(config, "pad", "different-app", 123, 456),
                () => lease.VerifyActive(config, "pad", "helper", 124, 456),
                () => lease.VerifyActive(config, "pad", "helper", 123, 457) }) MustFail(verify);
            Assert(config.Devices.SequenceEqual(new[] { "pad", "foreign" }) && config.Applications.SequenceEqual(new[] { "helper", "foreign-app" }), "verification changed a hide entry");
            config.Devices = new[] { "foreign" }; MustFail(() => lease.VerifyActive(config, "pad", "helper", 123, 456));
            config.Devices = new[] { "pad", "foreign" }; config.Active = false;
            MustFail(() => lease.VerifyActive(config, "pad", "helper", 123, 456));
            config.Active = true; config.Inverted = true;
            MustFail(() => lease.VerifyActive(config, "pad", "helper", 123, 456));
        });
        Test("HidHide lease rejects inverted lists and inactive foreign entries without writes", () => {
            foreach (var c in new[] { new FakeHide { Inverted = true }, new FakeHide { Devices = new[] { "foreign" } }, new FakeHide { Active = true, Devices = new[] { "pad" } } }) {
                bool rejected = false; try { HideLease.Check(c, "pad"); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "unsafe hide configuration accepted"); }
        });
        Test("HidHide restores partial activation and keeps pre-existing helper exemption", () => {
            var c = new FakeHide { Applications = new[] { "helper" } }; var lease = new HideLease { Device = "pad", Application = "helper", ActiveBefore = false, AddedApplication = false };
            c.Devices = new[] { "pad" }; lease.Restore(c); Assert(!c.Active && c.Devices.Length == 0 && c.Applications.Single() == "helper", "partial cleanup removed pre-existing state");
            lease.Activate(c); lease.Restore(c); Assert(!c.Active && c.Devices.Length == 0, "active state not restored");
        });
        Test("Controller stop does not alter power settings", () => {
            var h = new Fake(); var c = new FakeController(); c.Apply(new ControllerLayout { Enabled = true });
            var r = new ControlEngine(h, new Journal(), c).Execute(new Request { Operation = "controller_stop" });
            Assert(r.Success && !c.Layout.Enabled && h.Writes.Count == 0, "controller stop changed power");
        });
    }
    private sealed class FakeController : IControllerService
    {
        public bool FailPreflight, FailApplyOnce; public int Applies;
        public ControllerLayout Layout { get; private set; } = new ControllerLayout();
        public void Preflight(ControllerLayout layout) { if (FailPreflight) throw new Exception("missing driver"); }
        public void Apply(ControllerLayout layout) { Applies++; Layout = layout.Copy(); if (FailApplyOnce) { FailApplyOnce = false; throw new Exception("controller failed"); } }
        public ControllerStatus GetStatus() => new ControllerStatus { Active = Layout.Enabled };
        public void Dispose() { }
    }
    private sealed class FakeHide : IHideConfiguration
    {
        public bool Active { get; set; }
        public bool Inverted { get; set; }
        public string[] Applications { get; set; } = new string[0];
        public string[] Devices { get; set; } = new string[0];
    }
}
