// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void ControllerStartupTests()
    {
        Test("Desktop is a fresh built-in Xbox layout independent of editable profiles", () => {
            var first = DesktopControllerProfile.CreateLayout(); first.Validate();
            Assert(DesktopControllerProfile.Name == "Desktop" && first.Enabled && first.Map.SequenceEqual(ControllerLayout.Buttons)
                && first.LeftDeadzone == 10 && first.RightDeadzone == 10 && first.Rumble == 100
                && !first.SwapSticks && !first.SwapTriggers && !first.InvertLeftY && !first.InvertRightY && !first.UsesLegionButtons, "Desktop baseline is not the fixed standard layout");
            first.Map[0] = 0; first.LegionMap[0] = 4096; first.LeftDeadzone = 30; first.Enabled = false;
            var next = DesktopControllerProfile.CreateLayout();
            Assert(next.Enabled && next.Map[0] == 4096 && next.LegionMap.All(x => x == 0) && next.LeftDeadzone == 10
                && !ReferenceEquals(first.Map, next.Map) && !ReferenceEquals(first.LegionMap, next.LegionMap), "editing a copy changed the integrated Desktop");
        });
        Test("Desktop ignores mutable button labels when manufacturing its immutable baseline", () => {
            int original = ControllerLayout.Buttons[0];
            try { ControllerLayout.Buttons[0] = 0; Assert(DesktopControllerProfile.CreateLayout().Map[0] == 4096, "factory aliased mutable metadata"); }
            finally { ControllerLayout.Buttons[0] = original; }
        });
        Test("Startup defaults off and status never starts a controller or writes preferences", () => {
            var f = new StartupFixture(); var policy = f.Create();
            for(int i = 0; i < 5; i++) Assert(!policy.GetStatus().Enabled && policy.GetStatus().ProfileName == "Desktop", "default startup was enabled");
            policy.RunAtStartup(); policy.RunAtStartup();
            Assert(f.Starts == 0 && f.Store.AttemptWrites == 0 && f.Store.PreferenceWrites == 0, "disabled startup or polling changed a session");
        });
        Test("Saving Desktop startup only saves a boolean and rearms without starting input", () => {
            var f = new StartupFixture(); var p = f.Create(); p.Configure(true);
            Assert(p.GetStatus().Enabled && f.Store.Preferences.Enabled && f.Store.PreferenceWrites == 1 && f.Store.Clears == 1 && f.Starts == 0 && f.Store.AttemptWrites == 0, "saving started a controller");
            p.Configure(false);
            Assert(!p.GetStatus().Enabled && !f.Store.Preferences.Enabled && f.Starts == 0, "saving OFF affected input");
        });
        Test("Startup writes durable intent before its single Desktop preparation", () => {
            var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup(); p.RunAtStartup();
            Assert(f.Events.SequenceEqual(new[] { "attempt:starting", "start" }) && f.Starts == 1 && p.GetStatus().State == "starting", "startup order or single attempt changed");
            Assert(Protocol.Encode(f.StartedLayout) == Protocol.Encode(DesktopControllerProfile.CreateLayout()), "startup used an editable profile");
        });
        Test("A failed initial startup record prevents every controller action", () => {
            var f = new StartupFixture(true); f.Store.FailAttemptSave = true; var p = f.Create();
            p.RunAtStartup(); p.RunAtStartup();
            Assert(f.Starts == 0 && p.GetStatus().State == "blocked" && f.Store.Attempt == null, "startup proceeded without a durable guard");
        });
        Test("A completed Desktop startup persists P1 before allowing the next helper", () => {
            var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup();
            p.Observe(new ControllerStatus { PreparingPlayerOne = true });
            Assert(f.Store.Attempt.State == "starting", "an in-progress preparation claimed readiness");
            p.Observe(new ControllerStatus { Active = true, Connected = true, Hidden = true, VirtualSlot = 0 });
            Assert(f.Store.Attempt.State == "active" && p.GetStatus().State == "active", "P1 readiness was not recorded");
            int writes = f.Store.AttemptWrites;
            p.Observe(new ControllerStatus { Error = "later session error" }); p.GetStatus();
            Assert(f.Store.AttemptWrites == writes && p.GetStatus().State == "active", "later unrelated activity replaced the startup outcome");
            f.Current = new ControllerStatus(); var next = f.Create(); next.RunAtStartup();
            Assert(f.Starts == 2, "a previous successful launch blocked the next helper start");
        });
        Test("Failed or interrupted startup blocks a new helper until explicitly rearmed", () => {
            foreach(string state in new[] { "starting", "failed" }) {
                var f = new StartupFixture(true); f.Store.Attempt = StartupRecord(state); var p = f.Create();
                p.RunAtStartup(); p.RunAtStartup();
                Assert(f.Starts == 0 && p.GetStatus().State == "blocked", "interrupted startup retried a mode change");
                p.Configure(true); p.RunAtStartup();
                Assert(f.Starts == 0, "saving rearmed and retried in the same process");
                var next = f.Create(); next.RunAtStartup(); Assert(f.Starts == 1, "explicit rearm did not permit the next helper");
            }
        });
        Test("Native startup errors are retained and never retried automatically", () => {
            var f = new StartupFixture(true) { StartFailure = true }; var p = f.Create(); p.RunAtStartup(); p.RunAtStartup();
            Assert(f.Starts == 1 && f.Store.Attempt.State == "failed" && p.GetStatus().Error.Contains("native startup"), "failed startup retried or lost its cause");
            var next = f.Create(); next.RunAtStartup(); Assert(f.Starts == 1 && next.GetStatus().State == "blocked", "new helper retried a known startup failure");
        });
        Test("Wrong slots disconnected sources and unavailable final states cannot confirm startup", () => {
            foreach(var state in new[] { new ControllerStatus { Active = true, Connected = true, VirtualSlot = 1 },
                new ControllerStatus { Active = true, Connected = false, VirtualSlot = 0 }, new ControllerStatus(), new ControllerStatus { Error = "source error" } }) {
                var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup(); p.Observe(state);
                Assert(p.GetStatus().State == "failed" && f.Store.Attempt.State == "failed", "unconfirmed controller marked startup ready");
            }
        });
        Test("Explicit stop cancels only pending startup without retry or controller commands", () => {
            var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup(); p.CancelPending(); p.CancelPending();
            p.Observe(new ControllerStatus { Active = true, Connected = true, Hidden = true, VirtualSlot = 0 }); p.RunAtStartup();
            Assert(f.Starts == 1 && f.Store.Attempt.State == "canceled" && p.GetStatus().State == "canceled", "late observation revived canceled startup");
        });
        Test("Installation suppression preserves enabled preferences without startup actions", () => {
            var f = new StartupFixture(true); f.Store.Suppress = true; var p = f.Create(); p.RunAtStartup();
            Assert(p.GetStatus().Enabled && p.GetStatus().State == "skipped" && !f.Store.Suppress && f.Starts == 0 && f.Store.PreferenceWrites == 0, "installation initiated a controller or changed the preference");
            var next = f.Create(); next.RunAtStartup(); Assert(f.Starts == 1, "installation suppression persisted forever");
        });
        Test("Detected game or unavailable controller state skips startup before persisting intent", () => {
            foreach(int cause in new[] { 0, 1, 2, 3 }) {
                var f = new StartupFixture(true);
                if(cause == 0) f.Skip = "A game is open";
                if(cause == 1) f.Current = null;
                if(cause == 2) f.Current.Active = true;
                if(cause == 3) f.Current.PreparingPlayerOne = true;
                var p = f.Create(); p.RunAtStartup();
                Assert(f.Starts == 0 && f.Store.AttemptWrites == 0 && p.GetStatus().State == "skipped", "startup interrupted another session");
            }
        });
        Test("Corrupt preferences suppression and attempts fail closed without overwriting originals", () => {
            foreach(int cause in new[] { 0, 1, 2, 3 }) {
                var f = new StartupFixture(true);
                if(cause == 0) f.Store.FailPreferenceRead = true;
                if(cause == 1) f.Store.Preferences.Version = 99;
                if(cause == 2) f.Store.Attempt = StartupRecord("unknown");
                if(cause == 3) f.Store.FailSuppression = true;
                var p = f.Create(); p.RunAtStartup();
                Assert(f.Starts == 0 && f.Store.AttemptWrites == 0 && f.Store.PreferenceWrites == 0 && p.GetStatus().State == "blocked", "corrupt startup state authorized input");
            }
        });
        Test("Unsupported machines cannot enable or attempt Desktop startup", () => {
            var f = new StartupFixture(true); var p = f.Create(false);
            MustFail(() => p.Configure(true)); p.RunAtStartup();
            Assert(!p.GetStatus().Supported && f.Starts == 0 && f.Store.PreferenceWrites == 0 && f.Store.AttemptWrites == 0, "machine gate was bypassed");
        });
        Test("Saving is refused during preparation and never consumes the current startup guard", () => {
            var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup();
            MustFail(() => p.Configure(false));
            Assert(f.Store.Preferences.Enabled && f.Store.Clears == 0 && f.Store.Attempt.State == "starting", "settings overwrote in-progress startup");
            var another = new StartupFixture(); another.Current.PreparingPlayerOne = true;
            MustFail(() => another.Create().Configure(true)); Assert(another.Store.PreferenceWrites == 0, "settings bypassed another P1 preparation");
        });
        Test("Outcome persistence failure preserves a blocking durable starting record without breaking input", () => {
            var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup(); f.Store.FailAttemptSave = true;
            p.Observe(new ControllerStatus { Active = true, Connected = true, Hidden = true, VirtualSlot = 0 });
            Assert(p.GetStatus().State == "active" && p.GetStatus().Error != null && f.Store.Attempt.State == "starting", "persistence failure lost its startup guard");
            f.Store.FailAttemptSave = false; var next = f.Create(); next.RunAtStartup(); Assert(f.Starts == 1 && next.GetStatus().State == "blocked", "a missing outcome caused an automatic retry");
        });
        Test("Startup DTOs reject missing required fields incompatible versions and invalid attempt states", () => {
            foreach(string json in new[] { "{}", "{\"Version\":1}", "{\"Enabled\":true}" })
                MustFail(() => Protocol.Decode<ControllerStartupPreferences>(json));
            MustFail(() => new ControllerStartupPreferences { Version = 2 }.Validate());
            foreach(Action<ControllerStartupAttempt> corrupt in new Action<ControllerStartupAttempt>[] {
                a => a.Version = 2, a => a.Id = "bad", a => a.StartedUtc = 0, a => a.State = "ready", a => a.Error = new string('x', 2049) }) {
                var attempt = StartupRecord("starting"); corrupt(attempt); MustFail(() => attempt.Validate());
            }
            var old = Protocol.Decode<Response>("{}"); Assert(old.ControllerStartup == null, "old helper claimed startup capability");
            var response = Protocol.Decode<Response>(Protocol.Encode(new Response { ControllerStartup = new ControllerStartupStatus { Supported = true, ProfileName = "Desktop", Enabled = true } }));
            Assert(response.ControllerStartup.Enabled && response.ControllerStartup.ProfileName == "Desktop", "startup status was lost over IPC");
        });
        Test("Desktop engine opcode uses only its integrated layout and never invokes saved power", () => {
            var h = new Fake(); var journal = new Journal(); var controller = new AutomaticEngineController();
            var response = new ControlEngine(h, journal, controller).Execute(new Request { Operation = "controller_desktop" });
            Assert(response.Success && controller.ModeStarts == 1 && controller.Applies == 0 && h.Writes.Count == 0 && journal.Saves == 0
                && Protocol.Encode(controller.Prepared) == Protocol.Encode(DesktopControllerProfile.CreateLayout()), "Desktop used a saved profile or power");
        });
        Test("Desktop rejects editable layout payloads old helpers unsupported machines and wrong protocol", () => {
            foreach(int invalid in new[] { 0, 1, 2, 3, 4 }) {
                var h = new Fake(); var c = new AutomaticEngineController(); var request = new Request { Operation = "controller_desktop" };
                if(invalid == 0) request.Profile = FullProfile();
                if(invalid == 1) request.Values = new[] { 1 };
                if(invalid == 2) request.Version = 99;
                if(invalid == 3) h.Identity.Cpu = "Other";
                var engine = new ControlEngine(h, new Journal(), invalid == 4 ? (IControllerService)new EnginePreparationController() : c);
                Assert(!engine.Execute(request).Success && c.ModeStarts == 0 && c.Applies == 0 && h.Writes.Count == 0, "invalid Desktop request reached the controller");
            }
        });
        Test("Startup IPC saves only its boolean and status polling never arms native input", () => WithRecordingDirectory(folder => {
            var f = new StartupFixture(); var policy = f.Create(); var h = new Fake(); var journal = new Journal();
            using(var session = new HelperSession(new ControlEngine(h, journal), folder, false, startup: policy)) {
                Assert(session.Execute(new Request { Operation = "controller_startup_status" }).ControllerStartup.Supported, "startup status unavailable");
                foreach(int[] invalid in new[] { null, new int[0], new[] { 2 }, new[] { -1 }, new[] { 1, 0 } })
                    Assert(!session.Execute(new Request { Operation = "controller_startup_set", Values = invalid }).Success, "invalid preference accepted");
                Assert(!session.Execute(new Request { Operation = "controller_startup_set", Values = new[] { 1 }, Profile = FullProfile() }).Success, "editable startup layout accepted");
                Assert(!session.Execute(new Request { Operation = "controller_startup_set", Values = new[] { 1 }, Version = 99 }).Success, "wrong protocol enabled startup");
                Assert(session.Execute(new Request { Operation = "controller_startup_set", Values = new[] { 1 } }).ControllerStartup.Enabled, "preference not saved");
                for(int i = 0; i < 3; i++) session.Execute(new Request { Operation = "recording_status" });
                Assert(f.Starts == 0 && h.Writes.Count == 0 && h.Reads.Count == 0 && journal.Saves == 0 && f.Store.PreferenceWrites == 1, "status or saving altered controller/power");
            }
        }));
        Test("A helper without startup support rejects configuration rather than guessing", () => WithRecordingDirectory(folder => {
            using(var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
                Assert(!session.Execute(new Request { Operation = "controller_startup_set", Values = new[] { 1 } }).Success, "old helper claimed startup support");
        }));
        Test("Startup requires hidden physical input and error-free P1 rather than just an index", () => {
            foreach(int failure in new[] { 0, 1, 2 }) {
                var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup();
                var value = new ControllerStatus { Active = true, Connected = true, Hidden = true, VirtualSlot = 0 };
                if(failure == 0) value.Hidden = false;
                if(failure == 1) value.Error = "source failure";
                if(failure == 2) value.SlotError = "slot failure";
                p.Observe(value);
                Assert(f.Store.Attempt.State == "failed" && p.GetStatus().State == "failed", "partial ready state became successful startup");
            }
        });
        Test("Preference write and verification failures never rearm or start a controller", () => {
            foreach(bool write in new[] { true, false }) {
                var f = new StartupFixture(); var p = f.Create();
                if(write) f.Store.FailPreferenceSave = true; else f.Store.IgnorePreferenceSave = true;
                MustFail(() => p.Configure(true));
                Assert(f.Starts == 0 && f.Store.Clears == 0 && p.GetStatus().State == "blocked", "unconfirmed save rearmed input");
            }
        });
        Test("Concurrent startup callers share one durable attempt and one preparation", () => {
            var f = new StartupFixture(true); var p = f.Create();
            System.Threading.Tasks.Task.WaitAll(System.Threading.Tasks.Task.Run(() => p.RunAtStartup()), System.Threading.Tasks.Task.Run(() => p.RunAtStartup()));
            Assert(f.Starts == 1 && f.Store.AttemptWrites == 1, "concurrent launch paths started multiple controllers");
        });
        Test("Explicit controller stop and suspension cancel pending startup without power writes", () => WithRecordingDirectory(folder => {
            foreach(bool suspend in new[] { true, false }) {
                var f = new StartupFixture(true); var p = f.Create(); p.RunAtStartup();
                var h = new Fake(); var journal = new Journal();
                using(var session = new HelperSession(new ControlEngine(h, journal), folder, false, startup: p)) {
                    if(suspend) session.Suspend(); else Assert(session.Execute(new Request { Operation = "controller_stop" }).Success, "stop failed");
                    Assert(p.GetStatus().State == "canceled" && f.Store.Attempt.State == "canceled" && f.Starts == 1 && h.Writes.Count == 0 && journal.Saves == 0,
                        "stop or suspension restarted Desktop or changed power");
                }
            }
        }));
    }

    private static ControllerStartupAttempt StartupRecord(string state) => new ControllerStartupAttempt {
        Id = "0123456789abcdef0123456789abcdef", StartedUtc = 1000000, State = state
    };
    private sealed class StartupFixture
    {
        internal readonly StartupMemory Store = new StartupMemory();
        internal readonly List<string> Events = new List<string>();
        internal ControllerStatus Current = new ControllerStatus();
        internal ControllerLayout StartedLayout;
        internal int Starts;
        internal string Skip;
        internal bool StartFailure;
        internal StartupFixture(bool enabled = false) { Store.Preferences.Enabled = enabled; Store.Events = Events; }
        internal ControllerStartup Create(bool supported = true) => new ControllerStartup(Store, layout => {
            Starts++; Events.Add("start"); StartedLayout = layout;
            if(StartFailure) throw new InvalidOperationException("native startup failed");
        }, () => Current, () => Skip, supported);
    }
    private sealed class StartupMemory : IControllerStartupStorage
    {
        internal ControllerStartupPreferences Preferences = new ControllerStartupPreferences();
        internal ControllerStartupAttempt Attempt;
        internal List<string> Events;
        internal int AttemptWrites, PreferenceWrites, Clears;
        internal bool FailAttemptSave, FailPreferenceRead, FailSuppression, Suppress, FailPreferenceSave, IgnorePreferenceSave;
        public ControllerStartupPreferences LoadPreferences() {
            if(FailPreferenceRead) throw new InvalidOperationException("invalid preference");
            return Protocol.Decode<ControllerStartupPreferences>(Protocol.Encode(Preferences));
        }
        public void SavePreferences(ControllerStartupPreferences value) {
            if(FailPreferenceSave) throw new InvalidOperationException("cannot save preference");
            PreferenceWrites++; if(!IgnorePreferenceSave) Preferences = Protocol.Decode<ControllerStartupPreferences>(Protocol.Encode(value));
        }
        public ControllerStartupAttempt LoadAttempt() => Attempt == null ? null : Protocol.Decode<ControllerStartupAttempt>(Protocol.Encode(Attempt));
        public void SaveAttempt(ControllerStartupAttempt value) {
            if(FailAttemptSave) throw new InvalidOperationException("cannot save attempt");
            AttemptWrites++; Events?.Add("attempt:" + value.State); Attempt = Protocol.Decode<ControllerStartupAttempt>(Protocol.Encode(value));
        }
        public void ClearAttempt() { Clears++; Attempt = null; }
        public bool ConsumeInstallationSuppression() { if(FailSuppression) throw new InvalidOperationException("invalid suppression"); bool result = Suppress; Suppress = false; return result; }
    }
}
