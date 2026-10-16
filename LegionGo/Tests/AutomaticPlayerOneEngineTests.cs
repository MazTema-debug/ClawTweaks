// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void AutomaticPlayerOneEngineTests()
    {
        Test("Automatic P1 opcode passes only a copied layout and never applies power", () => {
            var h = new Fake(); var j = new Journal(); var c = new AutomaticEngineController(); var profile = FullProfile();
            var response = new ControlEngine(h, j, c).Execute(new Request { Operation = "controller_player_one_auto", Profile = profile });
            Assert(response.Success && c.Starts == 1 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0, "automatic preparation applied profile power");
            Assert(!ReferenceEquals(c.Prepared, profile.Controller) && !ReferenceEquals(c.Prepared.Map, profile.Controller.Map), "layout aliases the request");
            c.Prepared.Map[0] = 0;
            Assert(profile.Controller.Map[0] != 0, "preparation modified the stored layout");
        });
        Test("Busy P1 requires a separate explicit opcode without applying power", () => {
            var h = new Fake(); var j = new Journal(); var c = new AutomaticEngineController(); var profile = FullProfile();
            var response = new ControlEngine(h, j, c).Execute(new Request { Operation = "controller_player_one_busy", Profile = profile });
            Assert(response.Success && c.BusyStarts == 1 && c.Starts == 0 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0, "explicit operation invoked ordinary profile or power writes");
            Assert(!ReferenceEquals(c.Prepared, profile.Controller) && !ReferenceEquals(c.Prepared.Map, profile.Controller.Map), "busy preparation aliases saved layout");
        });
        Test("Ordinary P1 and polling never opt in to reconnecting busy readers", () => {
            var h = new Fake(); var c = new AutomaticEngineController(); var engine = new ControlEngine(h, new Journal(), c);
            engine.Execute(new Request { Operation = "status" });
            Assert(engine.Execute(new Request { Operation = "controller_player_one_auto", Profile = FullProfile() }).Success, "ordinary P1 failed");
            Assert(c.Starts == 1 && c.BusyStarts == 0, "ordinary operation opted into busy removal");
            Assert(!Protocol.Decode<ControllerStatus>("{}").SupportsBusyReaderReconnection, "old helper opted in");
        });
        Test("Busy P1 rejects old helpers and invalid requests before any action", () => {
            var old = new EnginePreparationController(); var h = new Fake();
            Assert(!new ControlEngine(h, new Journal(), old).Execute(new Request { Operation = "controller_player_one_busy", Profile = FullProfile() }).Success
                && old.PrepareAttempts == 0 && h.Writes.Count == 0, "old helper ran experimental operation");
            for (int mode = 0; mode < 5; mode++)
            {
                h = new Fake(); var c = new AutomaticEngineController();
                var request = new Request { Operation = "controller_player_one_busy", Profile = FullProfile() };
                if (mode == 0) h.Identity.Cpu = "Other";
                if (mode == 1) request.Version = 999;
                if (mode == 2) request.Profile = null;
                if (mode == 3) request.Profile.Controller.Enabled = false;
                if (mode == 4) request.Profile.EppAC = 101;
                Assert(!new ControlEngine(h, new Journal(), c).Execute(request).Success && c.BusyStarts == 0 && c.Applies == 0 && h.Writes.Count == 0, "invalid busy request changed state");
            }
        });
        Test("Automatic P1 rejects old manual-only helpers rather than invoking FPS", () => {
            var h = new Fake(); var j = new Journal(); var c = new EnginePreparationController();
            var response = new ControlEngine(h, j, c).Execute(new Request { Operation = "controller_player_one_auto", Profile = FullProfile() });
            Assert(!response.Success && response.Message.Contains("0.1.11") && c.PrepareAttempts == 0 && c.Applies == 0 && h.Writes.Count == 0, "old helper accepted automatic semantics");
        });
        Test("Automatic P1 validates model protocol and the complete profile before starting", () => {
            for (int mode = 0; mode < 5; mode++)
            {
                var h = new Fake(); var j = new Journal(); var c = new AutomaticEngineController();
                var request = new Request { Operation = "controller_player_one_auto", Profile = FullProfile() };
                if (mode == 0) h.Identity.Cpu = "Other";
                if (mode == 1) request.Version = 999;
                if (mode == 2) request.Profile = null;
                if (mode == 3) request.Profile.Controller.Enabled = false;
                if (mode == 4) request.Profile.EppAC = 101;
                var response = new ControlEngine(h, j, c).Execute(request);
                Assert(!response.Success && c.Starts == 0 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0, "invalid request started reconnection");
            }
        });
        Test("Automatic P1 prevents profile power writes until canceled", () => {
            var h = new Fake(); var j = new Journal(); var c = new AutomaticEngineController(); var engine = new ControlEngine(h, j, c);
            Assert(engine.Execute(new Request { Operation = "controller_player_one_auto", Profile = FullProfile() }).Success, "did not start");
            var powerOnly = FullProfile(); powerOnly.Controller = null;
            Assert(!engine.Execute(new Request { Operation = "profile_apply", Profile = powerOnly }).Success && h.Writes.Count == 0, "power-only profile bypassed the gate");
            Assert(engine.Execute(new Request { Operation = "controller_stop" }).Success && !c.Preparing && c.Applies == 1, "cancellation was unavailable");
            Assert(j.Saves == 0, "cancel wrote power originals");
        });
        Test("Automatic preparation capability is optional and survives IPC", () => {
            var old = Protocol.Decode<ControllerStatus>("{\"SupportsPlayerOnePreparation\":true}");
            Assert(old.SupportsPlayerOnePreparation && !old.SupportsAutomaticPlayerOnePreparation, "old helper advertised automatic recovery");
            var current = Protocol.Decode<ControllerStatus>(Protocol.Encode(new ControllerStatus { SupportsAutomaticPlayerOnePreparation = true }));
            Assert(current.SupportsAutomaticPlayerOnePreparation, "capability lost during IPC");
        });
        Test("Status polling never starts automatic P1 or restores a saved session", () => {
            var h = new Fake(); var j = new Journal(); var c = new AutomaticEngineController(); var engine = new ControlEngine(h, j, c);
            for (int i = 0; i < 3; i++) Assert(engine.Execute(new Request { Operation = "status" }).Success, "status failed");
            Assert(c.Starts == 0 && c.Applies == 0 && h.Writes.Count == 0 && j.Saves == 0, "startup/status changed the controller or power");
        });
    }
    private sealed class AutomaticEngineController : IControllerService, IAutomaticPlayerOneControllerService, IBusyPlayerOneControllerService
    {
        internal int Starts, Applies, BusyStarts;
        internal bool Preparing;
        internal ControllerLayout Prepared;
        public ControllerLayout Layout { get; private set; } = new ControllerLayout();
        public void PreparePlayerOneAutomatic(ControllerLayout layout) { if (Preparing) throw new InvalidOperationException("Already preparing"); Starts++; Prepared = layout; Preparing = true; }
        public void PreparePlayerOneWithBusyReaders(ControllerLayout layout) { if (Preparing) throw new InvalidOperationException("Already preparing"); BusyStarts++; Prepared = layout; Preparing = true; }
        public void Preflight(ControllerLayout layout) { if (Preparing) throw new InvalidOperationException("Preparing"); }
        public void Apply(ControllerLayout layout) { Applies++; Layout = layout; Preparing = false; }
        public ControllerStatus GetStatus() => new ControllerStatus { PreparingPlayerOne = Preparing, SupportsAutomaticPlayerOnePreparation = true };
        public void Dispose() { }
    }
}
