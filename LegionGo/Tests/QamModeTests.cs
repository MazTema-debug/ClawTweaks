// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;
using LegionTweaks.QamProbe;

internal static partial class Program
{
    private static QamModeRequest ModeCommand(int mode, string id = null) => new QamModeRequest {
        Operation = "apply_mode", Mode = mode, RequestId = id ?? Guid.NewGuid().ToString("N") };
    private static void QamModeTests()
    {
        Test("QAM mode service rejects Custom and all non-preset operations before reading or writing", () => {
            var hardware = new Fake(); var journal = new Journal(); var service = new QamModeService(new ControlEngine(hardware, journal));
            foreach (int value in new[] { -1, 0, 4, 255, int.MaxValue }) Assert(!service.Execute(ModeCommand(value)).Success, "non-preset accepted");
            foreach (string operation in new[] { null, "", "set", "tdp", "restore", "profile_apply", "controller_stop", "display_resolution" })
                Assert(!service.Execute(new QamModeRequest { Operation = operation, Mode = 1 }).Success, "other operation accepted");
            Assert(!service.Execute(new QamModeRequest { Operation = "query", Mode = 1 }).Success, "mixed query accepted");
            var wrong = ModeCommand(1); wrong.Version++;
            Assert(!service.Execute(wrong).Success && !service.Execute(ModeCommand(1, "bad")).Success, "bad protocol/id accepted");
            Assert(hardware.Reads.Count == 0 && hardware.Writes.Count == 0 && journal.Saves == 0, "invalid command reached hardware");
        });
        Test("QAM mode inspection and draft selection cannot save originals or apply changes", () => {
            var hardware = new Fake(); var journal = new Journal(); var service = new QamModeService(new ControlEngine(hardware, journal));
            var draft = new QamModeDraft();
            Assert(draft.BeginApply() == null, "unconnected draft created a command");
            draft.Observe(service.Execute(new QamModeRequest { Operation = "query" }));
            Assert(draft.Confirmed == 2 && draft.Selected == 2 && !draft.CanApply, "binding selected another mode");
            draft.Move(-1); draft.Move(1); draft.Move(1);
            Assert(draft.Selected == 3 && draft.CanApply && hardware.Writes.Count == 0 && journal.Saves == 0, "selection applied or saved");
        });
        Test("QAM mode application shares the engine originals and changes only Lenovo mode", () => {
            foreach (int mode in new[] { 1, 2, 3 }) {
                var hardware = new Fake(); hardware.Data["Mode"] = 255;
                var journal = new Journal(); var controller = new LiveStatusController();
                var engine = new ControlEngine(hardware, journal, controller);
                var response = new QamModeService(engine).Execute(ModeCommand(mode));
                Assert(response.Success && response.CanApply && QamModeService.CurrentMode(response.State) == mode, "mode not verified: " + response.Message);
                Assert(hardware.Writes.SequenceEqual(new[] { "Mode=" + mode }) && controller.Applies == 0, "mode touched another setting/controller");
                Assert(journal.Values.Count == 4 && journal.Values.Single(x => x.Address.Setting == Setting.Mode).Value == 255, "lost mode or custom tuple originals");
                Assert(engine.Execute(new Request { Operation = "restore" }).Success && hardware.Data["Mode"] == 255, "same engine could not restore its originals");
            }
        });
        Test("QAM preset changes preserve the first saved mode and inactive limits across repeated applications", () => {
            var hardware = new Fake(); var journal = new Journal(); var engine = new ControlEngine(hardware,journal);
            var service = new QamModeService(engine);
            Assert(service.Execute(ModeCommand(1)).Success && service.Execute(ModeCommand(3)).Success, "mode changes failed");
            Assert(journal.Values.Count == 4 && journal.Values.Single(x => x.Address.Setting == Setting.Mode).Value == 2
                && journal.Values.Single(x => x.Address.Setting == Setting.Spl).Value == 15, "later change overwrote first originals");
            Assert(hardware.Writes.All(x => x.StartsWith("Mode=")) && !hardware.Writes.Any(x => x.StartsWith("Cpu") || x.StartsWith("Epp")), "preset wrote limits/CPU");
        });
        Test("QAM mode request IDs prevent an immediate replay and cannot be reused for another preset", () => {
            var hardware = new Fake(); var engine = new ControlEngine(hardware,new Journal()); var service = new QamModeService(engine);
            var request = ModeCommand(1); var first = service.Execute(request); int writes = hardware.Writes.Count;
            var repeated = service.Execute(request);
            Assert(first.Success && repeated.Success && hardware.Writes.Count == writes && repeated.State.CapturedAtUtc == first.State.CapturedAtUtc, "replay wrote or renewed capture");
            repeated.State.Values[0].Value = 999;
            Assert(service.Execute(request).State.Values[0].Value != 999, "caller mutated cached response");
            Assert(!service.Execute(ModeCommand(3,request.RequestId)).Success && hardware.Writes.Count == writes, "ID changed meaning");
        });
        Test("QAM mode failures retain backups attempt rollback and never claim verified success", () => {
            var hardware = new Fake { FailOnce = Setting.Mode }; hardware.Data["Mode"] = 255;
            var journal = new Journal(); var service = new QamModeService(new ControlEngine(hardware,journal));
            var response = service.Execute(ModeCommand(1));
            Assert(!response.Success && !response.CanApply && hardware.Data["Mode"] == 255 && journal.Values.Count == 4, "failure was success or lost backup/rollback");
            var refused = new Fake(); var failedJournal = new Journal { FailSave = true };
            Assert(!new QamModeService(new ControlEngine(refused,failedJournal)).Execute(ModeCommand(1)).Success && refused.Writes.Count == 0, "write preceded durable originals");
            var ignored = new Fake { IgnoreSetting = Setting.Mode };
            Assert(!new QamModeService(new ControlEngine(ignored,new Journal())).Execute(ModeCommand(1)).Success, "ignored firmware write became success");
        });
        Test("QAM mode model gate and unreadable backup values block writes", () => {
            var hardware = new Fake(); hardware.Identity.Model = "other"; hardware.Identity.Version = "other";
            Assert(!new QamModeService(new ControlEngine(hardware,new Journal())).Execute(ModeCommand(1)).Success && hardware.Writes.Count == 0, "USB-independent model gate failed");
            hardware = new Fake { FailRead = Setting.Sppt }; var journal = new Journal();
            Assert(!new QamModeService(new ControlEngine(hardware,journal)).Execute(ModeCommand(1)).Success && hardware.Writes.Count == 0 && journal.Saves == 0, "missing backup allowed change");
        });
        Test("QAM mode draft requires explicit apply serializes requests and disarms after an uncertain result", () => {
            var service = new QamModeService(new ControlEngine(new Fake(),new Journal())); var draft = new QamModeDraft();
            draft.Observe(service.Execute(new QamModeRequest { Operation = "query" })); draft.Move(-1);
            var request = draft.BeginApply();
            Assert(request.Mode == 1 && draft.Busy && draft.BeginApply() == null, "double apply was possible");
            draft.Move(1); Assert(draft.Selected == 1, "draft changed during apply");
            draft.Complete(null);
            Assert(!draft.Busy && !draft.Available && !draft.CanApply && draft.Selected == 1, "uncertain result allowed replay");
            draft.Observe(service.Execute(new QamModeRequest { Operation = "query" }));
            var next = draft.BeginApply(); Assert(next != null && next.RequestId != request.RequestId, "explicit new request reused old intent");
            draft.Complete(service.Execute(next)); Assert(draft.Confirmed == 1 && !draft.CanApply, "verified mode remained actionable");
        });
        Test("QAM horizontal navigation requires a neutral eligible report and fires once per press", () => {
            var input = new QamInput();
            Assert(QamReport(input,0,0x02,focused:true) == QamAction.None, "startup left acted");
            QamReport(input,10,focused:true);
            Assert(QamReport(input,20,0x02,focused:true) == QamAction.Left && QamReport(input,30,0x02,focused:true) == QamAction.None, "left repeated");
            QamReport(input,40,focused:true);
            Assert(QamReport(input,50,0x01,focused:true) == QamAction.Right, "right missing");
            Assert(QamReport(input,60,0x01) == QamAction.None, "hidden panel navigated");
        });
    }
}
