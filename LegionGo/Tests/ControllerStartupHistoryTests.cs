// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void ControllerStartupHistoryTests()
    {
        Test("Saving a startup preference preserves the actual skipped reason across helper instances", () => {
            var f = new StartupFixture(true) { Skip = "A game is open" }; var policy = f.Create();
            policy.RunAtStartup(); var before = Protocol.Encode(policy.GetStatus().LastDecision);
            policy.Configure(true);
            Assert(policy.GetStatus().State == "configured" && Protocol.Encode(policy.GetStatus().LastDecision) == before,
                "saving replaced evidence of the actual startup");
            var next = f.Create();
            Assert(next.GetStatus().LastDecision.State == "skipped" && next.GetStatus().LastDecision.Message.Contains(f.Skip)
                && f.Starts == 0 && f.Store.HistoryWrites == 1, "startup reason did not survive a new instance");
        });
        Test("A pending startup and its final outcome occupy a single history entry", () => {
            var f = new StartupFixture(true); var policy = f.Create(); policy.RunAtStartup();
            var first = policy.GetStatus().LastDecision;
            Assert(first.State == "starting" && first.EnabledAtStartup && f.Store.History.Decisions.Length == 1, "initial decision missing");
            policy.Observe(new ControllerStatus { Active = true, Connected = true, Hidden = true, VirtualSlot = 0 });
            var last = policy.GetStatus().LastDecision;
            Assert(last.State == "active" && first.Id == last.Id && last.StartedUtc == first.StartedUtc
                && f.Store.History.Decisions.Length == 1 && f.Store.Attempt.State == "active", "outcome created a second helper record");
            int writes = f.Store.HistoryWrites;
            policy.Observe(new ControllerStatus { Error = "later input error" }); policy.GetStatus(); policy.GetHistory();
            Assert(f.Store.HistoryWrites == writes && policy.GetStatus().LastDecision.State == "active", "session failure was mislabeled as startup failure");
        });
        Test("Failed startup evidence survives explicit rearming and retains bounded error text", () => {
            var f = new StartupFixture(true); var policy = f.Create(); policy.RunAtStartup();
            policy.Observe(new ControllerStatus { Error = new string('x', 3000) });
            Assert(policy.GetStatus().LastDecision.State == "failed" && policy.GetStatus().LastDecision.Error.Length == 512,
                "startup failure was lost or unbounded");
            policy.Configure(false);
            Assert(policy.GetStatus().LastDecision.State == "failed" && f.Store.Attempt == null && f.Starts == 1,
                "explicit rearm erased failure evidence or started another controller");
        });
        Test("History write failure never interrupts input or removes the durable startup guard", () => {
            var f = new StartupFixture(true); f.Store.FailHistorySave = true;
            var policy = f.Create(); policy.RunAtStartup();
            Assert(f.Starts == 1 && f.Store.Attempt.State == "starting" && policy.GetStatus().HistoryError != null,
                "advisory history changed the startup guard");
            policy.Observe(new ControllerStatus { Active = true, Connected = true, Hidden = true, VirtualSlot = 0 });
            Assert(policy.GetStatus().State == "active" && f.Store.Attempt.State == "active" && f.Starts == 1
                && f.Store.HistoryWrites == 1, "history failure broke input or retried writes in the session");
            MustFail(() => policy.GetHistory());
        });
        Test("Bounded startup history never splits a UTF16 surrogate pair", () => {
            var f = new StartupFixture(true); var policy = f.Create(); policy.RunAtStartup();
            policy.Observe(new ControllerStatus { Error = new string('x', 511) + char.ConvertFromUtf32(0x1F3AE) + " error" });
            Assert(policy.GetStatus().LastDecision.Error.Length == 511 && policy.GetStatus().HistoryError == null,
                "truncation produced malformed history text");
        });
        Test("Corrupt history remains untouched and does not block a valid controller startup", () => {
            var f = new StartupFixture(true); f.Store.History.Decisions = null;
            string original = Protocol.Encode(f.Store.History);
            var policy = f.Create(); policy.RunAtStartup();
            Assert(f.Starts == 1 && policy.GetStatus().State == "starting" && policy.GetStatus().HistoryError != null
                && f.Store.HistoryWrites == 0 && Protocol.Encode(f.Store.History) == original, "corrupt history was overwritten or became an input dependency");
        });
        Test("History is bounded to eight helper runs and callers cannot mutate stored evidence", () => {
            var f = new StartupFixture(); string firstId = null;
            for (int i = 0; i < 10; i++) { var policy = f.Create(); policy.RunAtStartup(); if (i == 0) firstId = policy.GetStatus().LastDecision.Id; }
            var reader = f.Create(); var history = reader.GetHistory();
            Assert(history.Decisions.Length == 8 && history.Decisions.All(d => d.State == "disabled" && !d.EnabledAtStartup)
                && history.Decisions.All(d => d.Id != firstId) && f.Starts == 0, "retention limit or disabled evidence is wrong");
            history.Decisions[0].Message = "edited";
            reader.GetStatus().LastDecision.Message = "edited";
            Assert(reader.GetHistory().Decisions.All(d => d.Message != "edited"), "status/history exposed mutable internal records");
        });
        Test("Startup suppression and interrupted attempts remain distinct historical decisions", () => {
            var f = new StartupFixture(true); f.Store.Suppress = true;
            var suppressed = f.Create(); suppressed.RunAtStartup();
            Assert(suppressed.GetStatus().LastDecision.State == "skipped" && suppressed.GetStatus().LastDecision.EnabledAtStartup
                && f.Starts == 0, "installation suppression looked like a disabled preference");
            f.Store.Attempt = StartupRecord("starting");
            var next = f.Create(); next.RunAtStartup();
            Assert(next.GetStatus().LastDecision.State == "blocked" && next.GetHistory().Decisions.Length == 2
                && f.Starts == 0 && f.Store.Attempt.State == "starting", "history bypassed the interrupted attempt guard");
        });
        Test("History validation rejects incompatible oversized duplicate and incomplete entries", () => {
            var decision = new ControllerStartupDecision { Id = Guid.NewGuid().ToString("N"), StartedUtc = 1, UpdatedUtc = 2,
                State = "skipped", Message = "reason" };
            decision.Validate();
            MustFail(() => new ControllerStartupHistory { Version = 99 }.Validate());
            MustFail(() => new ControllerStartupHistory { Decisions = new ControllerStartupDecision[9] }.Validate());
            MustFail(() => new ControllerStartupHistory { Decisions = new[] { decision, decision } }.Validate());
            foreach (Action<ControllerStartupDecision> corrupt in new Action<ControllerStartupDecision>[] {
                d => d.Id = "wrong", d => d.StartedUtc = 0, d => d.UpdatedUtc = 0, d => d.State = "ready",
                d => d.Message = null, d => d.Message = new string('x', 513), d => d.Error = new string('x', 513) }) {
                var invalid = Protocol.Decode<ControllerStartupDecision>(Protocol.Encode(decision)); corrupt(invalid); MustFail(() => invalid.Validate());
            }
        });
        Test("History lookup is a read-only explicit IPC operation with no background file reads", () => WithRecordingDirectory(folder => {
            var f = new StartupFixture(); var policy = f.Create(); policy.RunAtStartup();
            var hardware = new Fake(); var journal = new Journal();
            int reads = f.Store.HistoryReads, writes = f.Store.HistoryWrites;
            using (var session = new HelperSession(new ControlEngine(hardware, journal), folder, false, startup: policy)) {
                var response = session.Execute(new Request { Operation = "controller_startup_history" });
                Assert(response.Success && response.ControllerStartupHistory.Decisions.Length == 1 && response.ControllerStartup.SupportsHistory,
                    "history response missing");
                for (int i = 0; i < 3; i++) session.Execute(new Request { Operation = "recording_status" });
                Assert(f.Store.HistoryReads == reads && f.Store.HistoryWrites == writes && f.Starts == 0
                    && hardware.Reads.Count == 0 && hardware.Writes.Count == 0 && journal.Saves == 0, "history polling touched hardware or storage");
                foreach (Action<Request> corrupt in new Action<Request>[] {
                    r => r.Version = 99, r => r.Key = "file", r => r.Values = new[] { 1 }, r => r.Ac = true,
                    r => r.Profile = FullProfile(), r => r.Display = new DisplayRequest(), r => r.RtssAppearance = new RtssAppearance() }) {
                    var request = new Request { Operation = "controller_startup_history" }; corrupt(request);
                    Assert(!session.Execute(request).Success, "history command accepted mutation payload");
                }
            }
            var old = Protocol.Decode<Response>("{\"ControllerStartup\":{\"Supported\":true}}");
            Assert(!old.ControllerStartup.SupportsHistory && old.ControllerStartup.LastDecision == null, "old helper claimed history support");
        }));
    }
}
