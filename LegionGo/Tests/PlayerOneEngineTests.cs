// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void PlayerOneEngineTests()
    {
        Test("Player one opcode starts only a copied controller layout and preserves power originals", () => {
            var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController();
            j.Values.Add(new OriginalValue { Address = new Address { Setting = Setting.ChargeLimit }, Value = 0 });
            var engine = new ControlEngine(h, j, c); engine.Execute(new Request { Operation = "status" });
            var powerBefore = h.Data.ToDictionary(x => x.Key, x => x.Value); string originalsBefore = Protocol.Encode(j.Values.ToArray());
            var profile = FullProfile(); profile.Controller.Map[0] = 8192; profile.Controller.LeftDeadzone = 23;
            profile.Controller.SwapTriggers = true; profile.Controller.Rumble = 35;
            string expectedLayout = Protocol.Encode(profile.Controller);
            var response = engine.Execute(new Request { Operation = "controller_player_one", Profile = profile });
            Assert(response.Success && response.Message.Contains("iniciada") && c.Preparing && c.PrepareStarts == 1, response.Message);
            Assert(c.Applies == 0 && c.Preflights == 0 && !c.Layout.Enabled, "preparation applied the full profile synchronously");
            Assert(Protocol.Encode(c.PreparedLayout) == expectedLayout && !ReferenceEquals(c.PreparedLayout, profile.Controller)
                && !ReferenceEquals(c.PreparedLayout.Map, profile.Controller.Map), "engine lost layout settings or retained caller-owned arrays");
            profile.Controller.Map[0] = 0;
            Assert(Protocol.Encode(c.PreparedLayout) == expectedLayout, "editing the original request changed the running preparation");
            Assert(h.Writes.Count == 0 && j.Saves == 0 && Protocol.Encode(j.Values.ToArray()) == originalsBefore
                && h.Data.Count == powerBefore.Count && powerBefore.All(x => h.Data[x.Key] == x.Value), "controller preparation altered power or its recovery journal");
        });
        Test("Player one rejects unsupported model CPU and manufacturer before reaching the controller", () => {
            for (int field = 0; field < 3; field++)
            {
                var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController();
                if (field == 0) { h.Identity.Model = "Other"; h.Identity.Version = "Other"; }
                if (field == 1) h.Identity.Cpu = "AMD Ryzen Z1";
                if (field == 2) h.Identity.Manufacturer = "Other";
                var response = new ControlEngine(h, j, c).Execute(new Request { Operation = "controller_player_one", Profile = FullProfile() });
                Assert(!response.Success && c.PrepareAttempts == 0 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0,
                    "hardware identity gate allowed preparation or power changes");
            }
        });
        Test("Player one rejects protocol mismatch missing request and unknown opcode without side effects", () => {
            foreach (var request in new[] { null, new Request { Version = 99, Operation = "controller_player_one", Profile = FullProfile() },
                new Request { Operation = "controller_player1", Profile = FullProfile() } })
            {
                var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController();
                var response = new ControlEngine(h, j, c).Execute(request);
                Assert(!response.Success && c.PrepareAttempts == 0 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0,
                    "invalid protocol or opcode reached a mutating route");
            }
        });
        Test("Player one validates the entire saved profile before requesting preparation", () => {
            var nullProfile = (ProfileDefinition)null;
            var invalidPower = FullProfile(); invalidPower.Limits = new[] { 10, 40, 40 };
            var invalidMap = FullProfile(); invalidMap.Controller.Map[0] = int.MaxValue;
            var invalidName = FullProfile(); invalidName.Name = "";
            var invalidId = FullProfile(); invalidId.Id = "invalid";
            foreach (var profile in new[] { nullProfile, invalidPower, invalidMap, invalidName, invalidId })
            {
                var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController();
                var response = new ControlEngine(h, j, c).Execute(new Request { Operation = "controller_player_one", Profile = profile });
                Assert(!response.Success && c.PrepareAttempts == 0 && h.Writes.Count == 0 && j.Saves == 0,
                    "a malformed profile reached preparation");
            }
        });
        Test("Player one requires an enabled virtual controller layout", () => {
            foreach (bool missing in new[] { true, false })
            {
                var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController(); var profile = FullProfile();
                if (missing) profile.Controller = null; else profile.Controller.Enabled = false;
                var response = new ControlEngine(h, j, c).Execute(new Request { Operation = "controller_player_one", Profile = profile });
                Assert(!response.Success && c.PrepareAttempts == 0 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0,
                    "physical or unchanged controller mode started the assistant");
            }
        });
        Test("Player one reports a missing capability on old or absent controller services", () => {
            foreach (IControllerService controller in new IControllerService[] { null, new FakeController() })
            {
                var h = new Fake(); var j = new Journal();
                var response = new ControlEngine(h, j, controller).Execute(new Request { Operation = "controller_player_one", Profile = FullProfile() });
                Assert(!response.Success && response.Message.Contains("Actualiza el helper") && h.Writes.Count == 0 && j.Saves == 0,
                    "old service silently accepted an unsupported assistant command");
                if (controller is FakeController old) Assert(old.Applies == 0, "old service received an accidental normal apply");
            }
        });
        Test("Preparation failure or a duplicate request never applies power as a fallback", () => {
            foreach (bool duplicate in new[] { false, true })
            {
                var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController { RejectPreparation = !duplicate };
                var engine = new ControlEngine(h, j, c); var request = new Request { Operation = "controller_player_one", Profile = FullProfile() };
                if (duplicate) Assert(engine.Execute(request).Success, "initial preparation did not start");
                var response = engine.Execute(request);
                Assert(!response.Success && c.PrepareStarts == (duplicate ? 1 : 0) && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0,
                    "failed preparation retried normal profile application or changed originals");
            }
        });
        Test("An in-progress assistant rejects controller profiles before every power write", () => {
            var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController(); var engine = new ControlEngine(h, j, c);
            Assert(engine.Execute(new Request { Operation = "controller_player_one", Profile = FullProfile() }).Success, "assistant did not start");
            var response = engine.Execute(new Request { Operation = "profile_apply", Profile = FullProfile() });
            Assert(!response.Success && c.Preparing && c.PrepareStarts == 1 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0,
                "profile preflight ran after power writes or canceled the active assistant");
        });
        Test("An in-progress assistant also rejects power-only profiles with no controller field", () => {
            var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController(); var engine = new ControlEngine(h, j, c);
            Assert(engine.Execute(new Request { Operation = "controller_player_one", Profile = FullProfile() }).Success, "assistant did not start");
            var profile = FullProfile(); profile.Controller = null;
            var response = engine.Execute(new Request { Operation = "profile_apply", Profile = profile });
            Assert(!response.Success && c.Preparing && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0,
                "a power-only profile bypassed the preparation guard");
        });
        Test("Controller stop cancels an in-progress assistant without changing power or originals", () => {
            var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController(); var engine = new ControlEngine(h, j, c);
            Assert(engine.Execute(new Request { Operation = "controller_player_one", Profile = FullProfile() }).Success, "assistant did not start");
            var response = engine.Execute(new Request { Operation = "controller_stop" });
            Assert(response.Success && !c.Preparing && !c.Layout.Enabled && c.Applies == 1 && c.Stops == 1 && c.PrepareStarts == 1
                && h.Writes.Count == 0 && j.Saves == 0, "explicit cancel was blocked by preflight or applied power");
        });
        Test("Status telemetry and periodic helper polls never start or restart player one preparation", () => {
            var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController(); var engine = new ControlEngine(h, j, c);
            using (var session = new HelperSession(engine, Path.GetTempPath(), false, controller: c))
            {
                foreach (var operation in new[] { "status", "telemetry", "recording_status", "recording_status" })
                    Assert(session.Execute(new Request { Operation = operation }).Success, "read-only poll failed");
                Assert(c.PrepareAttempts == 0 && c.Applies == 0, "poll started an assistant automatically");
                var started = session.Execute(new Request { Operation = "controller_player_one", Profile = FullProfile() });
                Assert(started.Success && started.Controller.PreparingPlayerOne && started.Controller.SupportsPlayerOnePreparation,
                    "helper response lost assistant state or capability");
                foreach (var operation in new[] { "recording_status", "status", "telemetry" })
                {
                    var poll = session.Execute(new Request { Operation = operation });
                    Assert(poll.Success && poll.Controller.PreparingPlayerOne, "read-only poll discarded the active preparation state");
                }
                Assert(c.PrepareStarts == 1 && c.PrepareAttempts == 1 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0
                    && !session.Recorder.GetStatus().Active, "poll restarted preparation, recording or power application");
            }
        });
        Test("Player one optional capability and preparation fields preserve older IPC responses", () => {
            var old = Protocol.Decode<Response>("{\"Version\":1,\"Success\":true,\"Controller\":{\"Active\":true,\"VirtualSlot\":1}}");
            Assert(old.Controller.Active && old.Controller.VirtualSlot == 1 && !old.Controller.SupportsPlayerOnePreparation
                && !old.Controller.PreparingPlayerOne, "old helper response accidentally advertised assistant support");
            var current = new Response { Success = true, Controller = new ControllerStatus {
                SupportsPlayerOnePreparation = true, PreparingPlayerOne = true, Message = "Vuelve FPS a OFF" } };
            var copy = Protocol.Decode<Response>(Protocol.Encode(current));
            Assert(copy.Controller.SupportsPlayerOnePreparation && copy.Controller.PreparingPlayerOne && copy.Controller.Message == current.Controller.Message,
                "new assistant metadata did not survive IPC");
            string absent = Protocol.Encode(new ControllerStatus());
            Assert(!absent.Contains("SupportsPlayerOnePreparation") && !absent.Contains("PreparingPlayerOne"), "false optional fields unexpectedly changed old response shape");
        });
    }

    private sealed class EnginePreparationController : IControllerService, IPlayerOneControllerService
    {
        internal bool Preparing, RejectPreparation;
        internal int PrepareAttempts, PrepareStarts, Preflights, Applies, Stops;
        internal ControllerLayout PreparedLayout;
        public ControllerLayout Layout { get; private set; } = new ControllerLayout();
        public void PreparePlayerOne(ControllerLayout layout)
        {
            PrepareAttempts++;
            if (Preparing || RejectPreparation) throw new InvalidOperationException("Preparación no disponible o ya en progreso.");
            PreparedLayout = layout; Preparing = true; PrepareStarts++;
        }
        public void Preflight(ControllerLayout layout)
        {
            Preflights++;
            if (Preparing) throw new InvalidOperationException("Finaliza o cancela la preparación del jugador 1 antes de aplicar un perfil.");
        }
        public void Apply(ControllerLayout layout)
        {
            Applies++;
            if (!layout.Enabled) { Preparing = false; Stops++; Layout = layout.Copy(); return; }
            if (Preparing) throw new InvalidOperationException("No se puede aplicar durante la preparación.");
            Layout = layout.Copy();
        }
        public ControllerStatus GetStatus() => new ControllerStatus { Active = Layout.Enabled,
            SupportsPlayerOnePreparation = true, PreparingPlayerOne = Preparing };
        public void Dispose() { }
    }
}
