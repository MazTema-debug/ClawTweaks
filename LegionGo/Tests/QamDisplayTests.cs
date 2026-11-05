// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;
using LegionTweaks.QamProbe;

internal static partial class Program
{
    private static QamDisplayRequest RefreshCommand(int frequency, string id = null) => new QamDisplayRequest {
        Operation = "set_refresh", Frequency = frequency, RequestId = id ?? Guid.NewGuid().ToString("N") };
    private static DisplayBackendFake QamScreen(int width = 1280, int height = 800, int frequency = 60)
    {
        var backend = new DisplayBackendFake();
        backend.Current.Info.Current = new DisplayMode { Width = width, Height = height, Frequency = frequency };
        backend.Current.Info.Modes = new[] { 60, 144 }.Select(hz => new DisplayMode { Width = width, Height = height, Frequency = hz }).ToArray();
        backend.Current.Info.CapturedAtUtc = DateTime.UtcNow.ToString("O");
        return backend;
    }
    private static void QamDisplayTests()
    {
        Test("QAM display accepts only typed queries or 60/144 Hz before any provider call", () => {
            int calls = 0;
            var service = new QamDisplayService(true, () => { calls++; return null; }, hz => { calls++; return null; });
            foreach (int hz in new[] { -1, 0, 59, 120, 145, int.MaxValue }) Assert(!service.Execute(RefreshCommand(hz)).Success, "bad Hz accepted");
            foreach (string op in new[] { null, "", "set", "display_resolution", "profile_apply", "restore", "apply_mode" })
                Assert(!service.Execute(new QamDisplayRequest { Operation = op, Frequency = 60 }).Success, "other operation accepted");
            Assert(!service.Execute(new QamDisplayRequest { Operation = "query", Frequency = 60 }).Success, "mixed query accepted");
            var badVersion = RefreshCommand(60); badVersion.Version++;
            Assert(!service.Execute(badVersion).Success && !service.Execute(RefreshCommand(60, "bad")).Success && calls == 0, "invalid request reached provider");
            var unsupported = new QamDisplayService(false, () => { calls++; return null; }, hz => { calls++; return null; });
            Assert(!unsupported.Execute(RefreshCommand(144)).Success && !unsupported.Execute(new QamDisplayRequest { Operation = "query" }).Supported
                && calls == 0, "model/CPU gate reached backend");
        });
        Test("QAM display polling and a same-Hz no-op preserve the widget snapshot token", () => WithRecordingDirectory(folder => {
            var backend = QamScreen();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var widget = display.Inspect();
                for (int i = 0; i < 3; i++) {
                    var read = display.InspectQam();
                    Assert(read.Info.Current.Frequency == 60 && read.SnapshotId == null && read.TrialToken == null, "QAM exported widget token");
                }
                var noOp = display.ApplyQamRefreshRate(60);
                Assert(noOp.SnapshotId == null && backend.Applies == 0 && backend.Tests == 0, "same Hz wrote or shared token");
                var applied = display.ApplyRefreshRate(new DisplayRequest { SnapshotId = widget.SnapshotId, RefreshRate = 144 });
                Assert(applied.Error == null && applied.Info.Current.Frequency == 144 && backend.Applies == 1, "poll invalidated widget token");
            }
        }));
        Test("QAM refresh switches both ways at every preset and non-preset resolution without a supervisor", () => WithRecordingDirectory(folder => {
            foreach (int height in new[] { 800, 1000, 1200, 1600, 900 }) foreach (int hz in new[] { 60, 144 }) {
                var backend = QamScreen(height * 8 / 5, height, hz == 60 ? 144 : 60); int launches = 0;
                using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++)) {
                    var service = new QamDisplayService(true, display.InspectQam, display.ApplyQamRefreshRate);
                    var read = service.Execute(new QamDisplayRequest { Operation = "query" });
                    Assert(read.Success && read.Can60 && read.Can144 && backend.Applies == 0, "query wrote or hid capabilities");
                    var applied = service.Execute(RefreshCommand(hz));
                    Assert(applied.Success && applied.Info.Current.Frequency == hz && applied.Info.Current.Height == height
                        && applied.Info.Current.Width == height * 8 / 5 && applied.Info.Orientation == 90, "Hz changed dimensions/orientation");
                    Assert(backend.Applies == 1 && backend.Tests == 1 && backend.Restores == 0 && launches == 0
                        && !File.Exists(Path.Combine(folder, DisplayStore.PendingName)), "write armed trial or rollback");
                    string wire = Protocol.Encode(applied);
                    Assert(!wire.Contains("SnapshotId") && !wire.Contains("TrialToken") && !wire.Contains("NativeMode"), "internal display state leaked");
                }
            }
        }));
        Test("QAM refresh uses fresh pixel dimensions rather than an earlier query", () => WithRecordingDirectory(folder => {
            var backend = QamScreen();
            using (var display = new DisplayControlService(folder, true, backend)) {
                display.InspectQam();
                backend.Current = QamScreen(1600, 1000).Current;
                var result = display.ApplyQamRefreshRate(144);
                Assert(result.Error == null && result.Info.Current.Width == 1600 && result.Info.Current.Height == 1000
                    && result.Info.Current.Frequency == 144, "stale dimensions were reapplied");
            }
        }));
        Test("QAM refresh refuses unavailable modes and changed topology before a native write", () => WithRecordingDirectory(folder => {
            foreach (bool missing in new[] { true, false }) {
                var backend = QamScreen();
                if (missing) backend.Current.Info.Modes = new[] { backend.Current.Info.Current.Copy() };
                else backend.Queried = count => { if (count == 3) backend.Current.Info.Orientation = 0; };
                using (var display = new DisplayControlService(folder, true, backend)) {
                    var result = new QamDisplayService(true, display.InspectQam, display.ApplyQamRefreshRate).Execute(RefreshCommand(144));
                    Assert(!result.Success && !result.CanSetRefreshRate && backend.Applies == 0 && backend.Restores == 0, "unsafe display write");
                }
            }
        }));
        Test("QAM display honors pending recovery and model gates without applying", () => WithRecordingDirectory(folder => {
            var backend = QamScreen();
            using (var unsupported = new DisplayControlService(folder, false, backend)) {
                bool rejected = false;
                try { unsupported.ApplyQamRefreshRate(144); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && backend.Queries == 0, "unsupported model enumerated display");
            }
            new DisplayStore(folder).Arm(DisplayJob(DateTime.UtcNow.Ticks));
            using (var display = new DisplayControlService(folder, true, backend)) {
                var service = new QamDisplayService(true, display.InspectQam, display.ApplyQamRefreshRate);
                var query = service.Execute(new QamDisplayRequest { Operation = "query" });
                Assert(query.Success && !query.CanSetRefreshRate && !query.Can144, "pending recovery enabled controls");
                Assert(!service.Execute(RefreshCommand(144)).Success && backend.Applies == 0 && backend.Tests == 0, "pending recovery allowed write");
            }
        }));
        Test("QAM partial display failure reports actual state never rolls back or replays the request", () => WithRecordingDirectory(folder => {
            var backend = QamScreen(); backend.ApplyFailsAfterChange = true;
            using (var display = new DisplayControlService(folder, true, backend)) {
                var service = new QamDisplayService(true, display.InspectQam, display.ApplyQamRefreshRate);
                var request = RefreshCommand(144); var result = service.Execute(request);
                Assert(!result.Success && !result.CanSetRefreshRate && result.Info.Current.Frequency == 144 && backend.Restores == 0, "partial failure invented result or restored");
                Assert(!service.Execute(request).Success && backend.Applies == 1, "failed request replayed");
                Assert(service.Execute(new QamDisplayRequest { Operation = "query" }).Info.Current.Frequency == 144 && backend.Applies == 1, "recovery query wrote");
            }
        }));
        Test("QAM display request cache retains capture time isolates callers and rejects changed intent", () => WithRecordingDirectory(folder => {
            var backend = QamScreen();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var service = new QamDisplayService(true, display.InspectQam, display.ApplyQamRefreshRate);
                var request = RefreshCommand(144); var first = service.Execute(request);
                string capture = first.Info.CapturedAtUtc; first.Info.Current.Height = 9999;
                var again = service.Execute(request);
                Assert(again.Success && again.Info.Current.Height == 800 && again.Info.CapturedAtUtc == capture && backend.Applies == 1, "cache was mutated or replayed");
                Assert(!service.Execute(RefreshCommand(60, request.RequestId)).Success && backend.Applies == 1, "ID changed meaning");
            }
        }));
        Test("QAM display shares the widget gate across fresh capture native validation and apply", () => WithRecordingDirectory(folder => {
            var backend = QamScreen();
            using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            using (var observerStarted = new ManualResetEventSlim())
            using (var display = new DisplayControlService(folder, true, backend)) {
                backend.Queried = count => { if (count == 2) { entered.Set(); if (!release.Wait(3000)) throw new Exception("release timed out"); } };
                var write = Task.Run(() => display.ApplyQamRefreshRate(144));
                Assert(entered.Wait(3000), "write did not reach validation");
                var other = Task.Run(() => { observerStarted.Set(); return display.Inspect(); });
                try {
                    Assert(observerStarted.Wait(3000) && !other.Wait(40) && backend.Queries == 2, "widget interleaved inside write");
                }
                finally { release.Set(); }
                Assert(write.Result.Error == null && other.Result.Info.Current.Frequency == 144 && backend.Applies == 1, "shared service deadlocked or lost apply");
            }
        }));
        Test("QAM display controls bind without commands require explicit activation and block double applies", () => {
            var controls = new QamRefreshControls();
            Assert(controls.BeginApply(144) == null, "unconnected control produced a command");
            var response = new QamDisplayResponse { Success = true, Supported = true, CanSetRefreshRate = true, Can60 = true, Can144 = true, Info = QamScreen().Current.Info };
            controls.Observe(response);
            Assert(controls.Current == 60 && !controls.CanApply(60) && controls.CanApply(144) && controls.BeginApply(120) == null, "capability/current binding failed");
            var request = controls.BeginApply(144);
            Assert(request.Valid && request.Frequency == 144 && controls.Busy && controls.BeginApply(144) == null, "double apply allowed");
            controls.Complete(null);
            Assert(!controls.Busy && !controls.CanApply(144), "unknown outcome armed controls");
            response.Can144 = false; controls.Observe(response);
            Assert(!controls.CanApply(144), "unavailable Hz enabled");
            response.Can144 = true; response.Supported = false; controls.Observe(response);
            Assert(!controls.CanApply(144), "unsupported helper enabled changes");
        });
        Test("QAM display capabilities expire and cached captures cannot revive them", () => {
            var now = DateTime.UtcNow; long elapsed = 0;
            var freshness = new TelemetryFreshness(() => now, () => elapsed);
            var controls = new QamRefreshControls(freshness);
            var response = new QamDisplayResponse { Success = true, Supported = true, CanSetRefreshRate = true, Can144 = true, Info = QamScreen().Current.Info };
            response.Info.CapturedAtUtc = now.ToString("O"); controls.Observe(response);
            Assert(controls.CanApply(144), "fresh response refused");
            response.Info.CapturedAtUtc = now.AddSeconds(4).ToString("O"); controls.Observe(response);
            Assert(!controls.CanApply(144) && controls.Current == null, "bad new timestamp reused previous freshness");
            response.Info.CapturedAtUtc = now.ToString("O"); controls.Observe(response);
            elapsed = 6001; now = now.AddSeconds(7);
            Assert(!controls.CanApply(144) && controls.Current == null, "stale response still actionable");
            now = now.AddSeconds(-7); controls.Observe(response);
            Assert(!controls.CanApply(144), "clock correction revived cached capabilities");
            controls.Invalidate(); response.Info.CapturedAtUtc = now.AddSeconds(4).ToString("O"); controls.Observe(response);
            Assert(!controls.CanApply(144), "future timestamp enabled write");
        });
    }
}
