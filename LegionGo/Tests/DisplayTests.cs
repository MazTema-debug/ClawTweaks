// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using LegionTweaks;

internal static partial class Program
{
    private static T DisplayClone<T>(T value) => Protocol.Decode<T>(Protocol.Encode(value));
    private static readonly DisplayMode DisplayOriginalMode = new DisplayMode { Width = 2560, Height = 1600, Frequency = 60 };
    private static readonly DisplayMode DisplayTargetMode = new DisplayMode { Width = 1920, Height = 1200, Frequency = 120 };
    private static DisplaySnapshot DisplayInitial() => new DisplaySnapshot { Identity = "synthetic-monitor-identity", NativeMode = new byte[220],
        Info = new DisplayInfo { DeviceName = "synthetic-display", Orientation = 90, ActiveDisplays = 1, CanChange = true,
            Current = DisplayOriginalMode.Copy(), Modes = new[] { DisplayOriginalMode.Copy(), DisplayTargetMode.Copy() } } };
    private static DisplayPlan DisplayJob(long now) => new DisplayPlan { Token = Guid.NewGuid().ToString("N"), CreatedUtc = now,
        OwnerPid = 123, OwnerStarted = 1234, Original = DisplayInitial(), Target = DisplayTargetMode.Copy() };
    private static void DisplayTests()
    {
        Test("Display refresh choices are only 60 and 144 at the current pixel dimensions", () => {
            var info = DisplayInitial().Info;
            info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 1920, Height = 1200, Frequency = 144 } };
            var options = DisplayRefreshRate.Options(info);
            Assert(options.Select(x => x.Frequency).SequenceEqual(new[] { 60, 144 }) && options[0].IsAvailable
                && !options[1].IsAvailable && DisplayRefreshRate.Options(null).All(x => !x.IsAvailable), "invented mode or altered resolution");
            options[0].Mode.Height = 9999;
            Assert(info.Modes[0].Height == 1600, "selection retained a mutable backend mode");
        });
        Test("Display refresh switches both ways on all four resolutions without trial or power writes", () => WithRecordingDirectory(folder => {
            foreach (int height in new[] { 800, 1000, 1200, 1600 }) foreach (int frequency in new[] { 60, 144 }) {
                var backend = new DisplayBackendFake(); int launches = 0; var h = new Fake(); var j = new Journal();
                backend.Current.Info.Current = new DisplayMode { Width = height * 8 / 5, Height = height, Frequency = frequency == 60 ? 144 : 60 };
                backend.Current.Info.Modes = new[] { backend.Current.Info.Current.Copy(), new DisplayMode { Width = height * 8 / 5, Height = height, Frequency = frequency } };
                using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++))
                using (var session = new HelperSession(new ControlEngine(h, j), folder, false, display: display)) {
                    var query = session.Execute(new Request { Operation = "display_status" });
                    var response = session.Execute(new Request { Operation = "display_refresh_rate", Display = new DisplayRequest {
                        SnapshotId = query.Display.SnapshotId, RefreshRate = frequency } });
                    var result = response.Display;
                    Assert(response.Success && response.SupportsDisplayRefreshRate && result.Error == null && result.Phase == "Applied", "refresh IPC failed");
                    Assert(result.Info.Current.Frequency == frequency && result.Info.Current.Height == height && result.Info.Current.Width == height * 8 / 5
                        && result.Info.Orientation == 90, "refresh changed resolution or orientation");
                    Assert(!result.Pending && result.TrialToken == null && result.DeadlineUtc == null && result.RemainingSeconds == 0, "refresh armed a trial");
                    Assert(backend.Tests == 1 && backend.Applies == 1 && h.Writes.Count == 0 && j.Saves == 0, "refresh wrote twice or changed power");
                    var again = display.ApplyRefreshRate(new DisplayRequest { SnapshotId = result.SnapshotId, RefreshRate = frequency });
                    Assert(again.Error == null && backend.Applies == 1, "already active Hz wrote again");
                    display.CancelPending(); display.Recover();
                }
                Assert(backend.Restores == 0 && launches == 0 && !Directory.EnumerateFiles(folder).Any(), "refresh armed recovery or rollback");
            }
        }));
        Test("Display refresh preserves the freshly read resolution including non-preset dimensions", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var query = display.Inspect();
                backend.Current.Info.Current = new DisplayMode { Width = 1920, Height = 1080, Frequency = 60 };
                backend.Current.Info.Modes = new[] { backend.Current.Info.Current.Copy(), new DisplayMode { Width = 1920, Height = 1080, Frequency = 144 } };
                var result = display.ApplyRefreshRate(new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144 });
                Assert(result.Error == null && result.Info.Current.Width == 1920 && result.Info.Current.Height == 1080
                    && result.Info.Current.Frequency == 144 && backend.Applies == 1, "refresh reused stale dimensions");
            }
        }));
        Test("Display refresh refuses unsupported Hz at current resolution instead of changing resolution", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 1280, Height = 800, Frequency = 144 } };
            using (var display = new DisplayControlService(folder, true, backend)) {
                var query = display.Inspect();
                var result = display.ApplyRefreshRate(new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144 });
                Assert(result.Error != null && result.Info.Current.Same(DisplayOriginalMode)
                    && backend.Tests == 0 && backend.Applies == 0 && backend.Restores == 0, "refresh used a resolution fallback");
            }
        }));
        Test("Display refresh rejects arbitrary Hz, mixed selections and stale IPC tokens before driver access", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var query = display.Inspect(); int queries = backend.Queries;
                foreach (var request in new[] {
                    null, new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 120 },
                    new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144, ResolutionHeight = 800 },
                    new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144, Mode = DisplayTargetMode.Copy() },
                    new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144, TrialToken = "old" },
                    new DisplayRequest { RefreshRate = 144 }, new DisplayRequest { SnapshotId = "stale", RefreshRate = 144 }
                }) {
                    bool rejected = false; try { display.ApplyRefreshRate(request); } catch (ArgumentException) { rejected = true; }
                    Assert(rejected && backend.Queries == queries && backend.Applies == 0, "invalid refresh accessed driver");
                }
                bool mixed = false;
                try { display.ApplyResolution(new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800, RefreshRate = 144 }); } catch (ArgumentException) { mixed = true; }
                Assert(mixed && backend.Queries == queries, "resolution accepted a frequency side effect");
            }
            using (var display = new DisplayControlService(folder, false, backend)) {
                int queries = backend.Queries; bool rejected = false;
                try { display.ApplyRefreshRate(new DisplayRequest { RefreshRate = 144 }); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && backend.Queries == queries, "unsupported hardware accessed display");
            }
        }));
        Test("Display refresh failures expose readback and never restore partial writes", () => WithRecordingDirectory(folder => {
            foreach (int kind in new[] { 0, 1, 2, 3 }) {
                var backend = new DisplayBackendFake { TestFails = kind == 0, ApplyFailsAfterChange = kind == 1, ApplyNoEffect = kind == 2 };
                backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 2560, Height = 1600, Frequency = 144 } };
                if (kind == 3) backend.Queried = n => { if (n >= 4) throw new IOException("readback unavailable"); };
                int launches = 0;
                using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++))
                using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false, display: display)) {
                    var query = session.Execute(new Request { Operation = "display_status" });
                    var response = session.Execute(new Request { Operation = "display_refresh_rate", Display = new DisplayRequest {
                        SnapshotId = query.Display.SnapshotId, RefreshRate = 144 } });
                    Assert(!response.Success && response.Display.Error != null && !response.Display.Pending && response.Display.DeadlineUtc == null, "failed Hz reported success");
                    if (kind == 3) Assert(response.Display.Info == null && response.Display.SnapshotId == null, "stale Hz presented as current");
                    else Assert(response.Display.Info.Current.Frequency == (kind == 1 ? 144 : 60), "actual Hz after failure lost");
                }
                Assert(backend.Restores == 0 && launches == 0 && !File.Exists(Path.Combine(folder, DisplayStore.PendingName)), "Hz failure armed rollback");
            }
        }));
        Test("Display refresh revalidates identity, topology and mode after native test", () => WithRecordingDirectory(folder => {
            foreach (int kind in new[] { 0, 1, 2, 3 }) {
                var backend = new DisplayBackendFake();
                backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 2560, Height = 1600, Frequency = 144 } };
                backend.Queried = n => { if (n == 3) {
                    if (kind == 0) backend.Current.Identity = "different-monitor";
                    if (kind == 1) backend.Current.Info.Current.Height = 1200;
                    if (kind == 2) backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy() };
                    if (kind == 3) backend.Current.Info.CanChange = false;
                } };
                using (var display = new DisplayControlService(folder, true, backend)) {
                    var query = display.Inspect();
                    var result = display.ApplyRefreshRate(new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144 });
                    Assert(result.Error != null && backend.Tests == 1 && backend.Applies == 0 && backend.Restores == 0, "Hz wrote after concurrent screen change");
                }
            }
        }));
        Test("Display refresh preserves old recovery records and cannot start a new change", () => WithRecordingDirectory(folder => {
            var store = new DisplayStore(folder); store.Arm(DisplayJob(DateTime.UtcNow.Ticks));
            string before = File.ReadAllText(Path.Combine(folder, DisplayStore.PendingName));
            var backend = new DisplayBackendFake(); int launches = 0;
            using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++)) {
                var query = display.Inspect(); bool rejected = false;
                try { display.ApplyRefreshRate(new DisplayRequest { SnapshotId = query.SnapshotId, RefreshRate = 144 }); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && backend.Tests == 0 && backend.Applies == 0 && backend.Restores == 0 && launches == 0
                    && File.ReadAllText(Path.Combine(folder, DisplayStore.PendingName)) == before, "Hz replaced a pending original");
            }
        }));
        Test("Display resolution list is exactly four 16:10 modes at current Hz", () => {
            var info = DisplayInitial().Info;
            info.Modes = new[] { DisplayOriginalMode.Copy(), DisplayTargetMode.Copy(),
                new DisplayMode { Width = 1280, Height = 800, Frequency = 60 },
                new DisplayMode { Width = 1600, Height = 1000, Frequency = 144 },
                new DisplayMode { Width = 1920, Height = 1080, Frequency = 60 } };
            var choices = DisplayResolution.Options(info);
            Assert(choices.Select(x => x.Height).SequenceEqual(new[] { 800, 1000, 1200, 1600 })
                && choices.Select(x => x.Width).SequenceEqual(new[] { 1280, 1600, 1920, 2560 }), "unexpected resolution");
            Assert(choices[0].IsAvailable && choices[3].IsAvailable && !choices[1].IsAvailable && !choices[2].IsAvailable, "invented timing or changed Hz");
            choices[0].Mode.Width = 9999;
            Assert(info.Modes[2].Width == 1280 && DisplayResolution.Options(null).All(x => !x.IsAvailable), "mutable or unqueried mode");
        });
        Test("Display direct resolution applies once without deadline, worker, journal or rollback", () => WithRecordingDirectory(folder => {
            foreach (int hz in new[] { 60, 144 }) foreach (int height in new[] { 800, 1000, 1200, 1600 }) {
                var backend = new DisplayBackendFake(); int launches = 0; var h = new Fake(); var j = new Journal();
                backend.Current.Info.Current.Frequency = hz;
                backend.Current.Info.Modes = new[] { 800, 1000, 1200, 1600 }
                    .Select(optionHeight => new DisplayMode { Width = optionHeight * 8 / 5, Height = optionHeight, Frequency = hz }).ToArray();
                using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++))
                using (var session = new HelperSession(new ControlEngine(h, j), folder, false, display: display)) {
                    var query = session.Execute(new Request { Operation = "display_status" });
                    var response = session.Execute(new Request { Operation = "display_resolution", Display = new DisplayRequest {
                        SnapshotId = query.Display.SnapshotId, ResolutionHeight = height } });
                    var status = response.Display;
                    Assert(response.Success && response.SupportsDisplayResolution && h.Writes.Count == 0 && j.Saves == 0, "direct IPC failed or changed power");
                    Assert(status.Phase == "Applied" && status.Error == null && !status.Pending && status.TrialToken == null
                        && status.DeadlineUtc == null && status.RemainingSeconds == 0 && status.SnapshotId != null, "trial or unverified result");
                    Assert(status.Info.Current.Width == height * 8 / 5 && status.Info.Current.Height == height && status.Info.Current.Frequency == hz, "wrong mode or Hz");
                    Assert(backend.Applies == (height == 1600 ? 0 : 1) && backend.Tests == backend.Applies, "repeated or absent apply");
                    display.CancelPending(); display.Recover();
                }
                Assert(backend.Restores == 0 && launches == 0 && !Directory.EnumerateFiles(folder).Any(), "direct change armed recovery");
            }
        }));
        Test("Display direct selection re-reads actual Hz rather than reusing stale modes", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var query = display.Inspect();
                backend.Current.Info.Current.Frequency = 144;
                backend.Current.Info.Modes = new[] { new DisplayMode { Width = 1280, Height = 800, Frequency = 144 } };
                var status = display.ApplyResolution(new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800 });
                Assert(status.Error == null && status.Info.Current.Frequency == 144 && backend.Applies == 1 && backend.Restores == 0, "stale Hz applied");
            }
        }));
        Test("Display direct selection refuses unavailable resolution without a refresh fallback", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var query = display.Inspect();
                var status = display.ApplyResolution(new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 1200 });
                Assert(status.Error != null && status.Info.Current.Same(DisplayOriginalMode)
                    && backend.Tests == 0 && backend.Applies == 0 && backend.Restores == 0, "silently changed frequency");
            }
        }));
        Test("Display direct selection rejects changed screen, orientation and restricted topology", () => WithRecordingDirectory(folder => {
            foreach (int kind in new[] { 0, 1, 2 }) {
                var backend = new DisplayBackendFake();
                backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 1280, Height = 800, Frequency = 60 } };
                using (var display = new DisplayControlService(folder, true, backend)) {
                    var query = display.Inspect();
                    if (kind == 0) backend.Current.Identity = "another-monitor";
                    if (kind == 1) backend.Current.Info.Orientation = 0;
                    if (kind == 2) backend.Current.Info.CanChange = false;
                    var status = display.ApplyResolution(new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800 });
                    Assert(status.Error != null && backend.Tests == 0 && backend.Applies == 0 && backend.Restores == 0, "changed screen written");
                }
            }
        }));
        Test("Display direct selection validates request before accessing driver", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var query = display.Inspect(); int queries = backend.Queries;
                foreach (var request in new[] {
                    null, new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 1080 },
                    new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800, Mode = DisplayOriginalMode.Copy() },
                    new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800, TrialToken = "old" },
                    new DisplayRequest { ResolutionHeight = 800 }, new DisplayRequest { SnapshotId = "other", ResolutionHeight = 800 }
                }) {
                    bool rejected = false; try { display.ApplyResolution(request); } catch (ArgumentException) { rejected = true; }
                    Assert(rejected && backend.Queries == queries && backend.Applies == 0 && backend.Restores == 0, "invalid request accessed driver");
                }
            }
            using (var display = new DisplayControlService(folder, false, backend)) {
                int queries = backend.Queries; bool rejected = false;
                try { display.ApplyResolution(new DisplayRequest { ResolutionHeight = 800 }); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && backend.Queries == queries, "unsupported device accessed driver");
            }
        }));
        Test("Display direct errors report actual state and never roll back even partial writes", () => WithRecordingDirectory(folder => {
            foreach (int kind in new[] { 0, 1, 2, 3 }) {
                var backend = new DisplayBackendFake { TestFails = kind == 0, ApplyFailsAfterChange = kind == 1, ApplyNoEffect = kind == 2 };
                backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 1280, Height = 800, Frequency = 60 } };
                if (kind == 3) backend.Queried = n => { if (n >= 4) throw new IOException("readback unavailable"); };
                int launches = 0; var h = new Fake(); var j = new Journal();
                using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++))
                using (var session = new HelperSession(new ControlEngine(h, j), folder, false, display: display)) {
                    var query = session.Execute(new Request { Operation = "display_status" });
                    var response = session.Execute(new Request { Operation = "display_resolution", Display = new DisplayRequest {
                        SnapshotId = query.Display.SnapshotId, ResolutionHeight = 800 } });
                    Assert(!response.Success && response.Display.Phase == "Failed" && response.Display.Error != null
                        && !response.Display.Pending && response.Display.DeadlineUtc == null, "failure confirmed or trial armed");
                    if (kind == 3) Assert(response.Display.Info == null && response.Display.SnapshotId == null, "stale readback presented as new");
                    else Assert(response.Display.Info.Current.Height == (kind == 1 ? 800 : 1600), "failure hid actual mode");
                    Assert(h.Writes.Count == 0 && j.Saves == 0, "display operation changed power");
                }
                Assert(backend.Restores == 0 && launches == 0 && !File.Exists(Path.Combine(folder, DisplayStore.PendingName)), "failure armed rollback");
            }
        }));
        Test("Display direct change revalidates screen after native test", () => WithRecordingDirectory(folder => {
            foreach (int kind in new[] { 0, 1, 2 }) {
                var backend = new DisplayBackendFake();
                backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy(), new DisplayMode { Width = 1280, Height = 800, Frequency = 60 } };
                backend.Queried = n => { if (n == 3) {
                    if (kind == 0) backend.Current.Identity = "different-screen";
                    if (kind == 1) backend.Current.Info.Current.Frequency = 144;
                    if (kind == 2) backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy() };
                } };
                using (var display = new DisplayControlService(folder, true, backend)) {
                    var query = display.Inspect();
                    var status = display.ApplyResolution(new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800 });
                    Assert(status.Error != null && backend.Tests == 1 && backend.Applies == 0 && backend.Restores == 0, "wrote after concurrent change");
                }
            }
        }));
        Test("Display direct selection preserves a previous version's recovery journal", () => WithRecordingDirectory(folder => {
            var store = new DisplayStore(folder); var plan = DisplayJob(DateTime.UtcNow.Ticks); store.Arm(plan);
            string before = File.ReadAllText(Path.Combine(folder, DisplayStore.PendingName));
            var backend = new DisplayBackendFake(); int launches = 0;
            using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++)) {
                var query = display.Inspect(); bool rejected = false;
                try { display.ApplyResolution(new DisplayRequest { SnapshotId = query.SnapshotId, ResolutionHeight = 800 }); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && launches == 0 && backend.Tests == 0 && backend.Applies == 0 && backend.Restores == 0
                    && File.ReadAllText(Path.Combine(folder, DisplayStore.PendingName)) == before, "old recovery overwritten");
            }
        }));
        Test("Display no confirmation reverts at 15 seconds and verifies original", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake();
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && !result.Pending && backend.Tests == 1 && backend.Applies == 1 && backend.Restores == 1, "trial did not restore");
            Assert(port.Elapsed == 15000 && backend.Current.Info.Current.Same(DisplayOriginalMode), "wrong deadline/original");
            Assert(port.Phases.IndexOf("Applying") < port.Phases.IndexOf("AwaitingConfirmation") && port.Phases.Contains("Reverting"), "missing durable phases");
        });
        Test("Display valid confirmation keeps only the exact target", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { DecisionAt = 100, Choice = "confirm" };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Confirmed" && !result.Pending && backend.Restores == 0 && backend.Current.Info.Current.Same(DisplayTargetMode), "confirmation failed");
        });
        Test("Display explicit revert releases target before deadline", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { DecisionAt = 200, Choice = "revert" };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && backend.Restores == 1 && port.Elapsed == 200, "cancel did not restore promptly");
        });
        Test("Display owner exit triggers recovery without UI or heartbeat", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { OwnerExitAt = 300 };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && port.Elapsed == 300 && backend.Restores == 1, "owner loss did not restore");
        });
        Test("Display canceled or locked trial before apply never writes", () => {
            foreach (int kind in new[] { 0, 1, 2 }) {
                var backend = new DisplayBackendFake(); var port = new DisplayPortFake();
                if (kind == 0) port.OwnerExitAt = 0;
                if (kind == 1) port.LockAt = 0;
                if (kind == 2) { port.Choice = "revert"; port.DecisionAt = 0; }
                var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
                Assert(!result.Pending && backend.Applies == 0 && backend.Tests == 0, "unattended apply");
            }
        });
        Test("Display expired and future-dated preparation never apply", () => {
            foreach (int age in new[] { 31000, -100 }) {
                var backend = new DisplayBackendFake(); var port = new DisplayPortFake(); var plan = DisplayJob(port.UtcTicks - age * TimeSpan.TicksPerMillisecond);
                var result = DisplayTrialRules.Run(plan, backend, port);
                Assert(result.Phase == "Canceled" && backend.Applies == 0, "stale plan applied");
            }
        });
        Test("Display topology and orientation changes invalidate preview", () => {
            foreach (int kind in new[] { 0, 1, 2, 3 }) {
                var backend = new DisplayBackendFake(); var port = new DisplayPortFake(); var plan = DisplayJob(port.UtcTicks);
                if (kind == 0) backend.Current.Identity = "other-monitor";
                if (kind == 1) backend.Current.Info.Orientation = 0;
                if (kind == 2) backend.Current.Info.CanChange = false;
                if (kind == 3) backend.Current.Info.Modes = new[] { DisplayOriginalMode.Copy() };
                var result = DisplayTrialRules.Run(plan, backend, port);
                Assert(result.Phase == "Failed" && backend.Applies == 0 && backend.Restores == 0, "changed screen written");
            }
        });
        Test("Display native test refusal performs no apply or guessed rollback", () => {
            var backend = new DisplayBackendFake { TestFails = true }; var port = new DisplayPortFake();
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Failed" && !result.Pending && backend.Applies == 0 && backend.Restores == 0 && result.Error != null, "test error hidden");
        });
        Test("Display partial apply failure attempts verified rollback", () => {
            var backend = new DisplayBackendFake { ApplyFailsAfterChange = true }; var port = new DisplayPortFake();
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && backend.Restores == 1 && result.Error == "apply failed", "partial failure not restored");
        });
        Test("Display failed restore retains recovery and causal failure", () => {
            var backend = new DisplayBackendFake { ApplyFailsAfterChange = true, RestoreFails = true }; var port = new DisplayPortFake();
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Pending && result.Phase == "RecoveryPending" && result.Error.Contains("apply failed") && result.Error.Contains("restore failed"), "lost recovery data");
        });
        Test("Display successful API return without matching readback never confirms", () => {
            var backend = new DisplayBackendFake { ApplyNoEffect = true }; var port = new DisplayPortFake { DecisionAt = 100, Choice = "confirm" };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && result.Error != null && backend.Restores == 0, "unverified mode confirmed");
        });
        Test("Display slow apply cannot extend the confirmation deadline", () => {
            var port = new DisplayPortFake { DecisionAt = 100, Choice = "confirm" }; var backend = new DisplayBackendFake { Applied = () => port.Elapsed = 16000 };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && backend.Restores == 1, "slow driver renewed timeout");
        });
        Test("Display slow confirmation read crossing deadline rolls back", () => {
            var port = new DisplayPortFake { DecisionAt = 100, Choice = "confirm" }; var backend = new DisplayBackendFake();
            backend.Queried = n => { if (n == 3) port.Elapsed = 16000; };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && backend.Restores == 1, "late read confirmed");
        });
        Test("Display foreign mode is preserved with a pending backup", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { DecisionAt = 100, Choice = "confirm" };
            port.Waited = () => backend.Current.Info.Current = new DisplayMode { Width = 1280, Height = 800, Frequency = 60 };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Pending && backend.Restores == 0 && backend.Current.Info.Current.Width == 1280, "overwrote external state");
        });
        Test("Display restore success without original readback stays pending", () => {
            var backend = new DisplayBackendFake { RestoreNoEffect = true }; var port = new DisplayPortFake();
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Pending && result.Phase == "RecoveryPending" && backend.Restores == 1, "unverified rollback reported success");
        });
        Test("Display failed durable confirmation rolls back instead of reporting success", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { DecisionAt = 100, Choice = "confirm", FailSavePhase = "Confirmed" };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Phase == "Reverted" && !result.Pending && backend.Restores == 1 && result.Error != null, "failed confirmation kept the new mode");
        });
        Test("Display system clock changes cannot renew the monotonic deadline", () => {
            foreach (int skew in new[] { -3600000, 3600000 }) {
                var backend = new DisplayBackendFake(); var port = new DisplayPortFake(); port.Waited = () => port.ClockSkew = skew;
                var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
                Assert(result.Phase == "Reverted" && backend.Restores == 1 && port.Elapsed <= 15000, "wall clock renewed trial");
            }
        });
        Test("Display disconnected identity is not replaced by another screen", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { OwnerExitAt = 100 };
            port.Waited = () => backend.Current.Identity = "different-physical-screen";
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Pending && backend.Restores == 0, "restored unrelated monitor");
        });
        Test("Display lock during trial preserves recovery without secure-desktop writes", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake { LockAt = 100 };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port);
            Assert(result.Pending && backend.Restores == 0 && port.Elapsed == 100, "wrote locked desktop");
        });
        Test("Display recovery only restores saved target and never reapplies a new one", () => {
            var backend = new DisplayBackendFake(); backend.Current.Info.Current = DisplayTargetMode.Copy(); var port = new DisplayPortFake { OwnerExitAt = 0 };
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port, true);
            Assert(result.Phase == "Reverted" && backend.Applies == 0 && backend.Tests == 0 && backend.Restores == 1, "recovery performed a new apply");
        });
        Test("Display already original recovery retires without native writes", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake();
            var result = DisplayTrialRules.Run(DisplayJob(port.UtcTicks), backend, port, true);
            Assert(!result.Pending && backend.Applies == 0 && backend.Restores == 0, "idempotent recovery wrote");
        });
        Test("Display corrupt plan is rejected before any driver access", () => {
            var backend = new DisplayBackendFake(); var port = new DisplayPortFake(); var plan = DisplayJob(port.UtcTicks); plan.Original.NativeMode = new byte[219];
            bool rejected = false; try { DisplayTrialRules.Run(plan, backend, port); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && backend.Queries == 0 && backend.Applies == 0, "invalid native backup accessed driver");
        });
        Test("Display native ABI matches Unicode Windows headers without invoking APIs", () => {
            Assert(Marshal.SizeOf(typeof(NativeDisplayBackend.Mode)) == 220 && Marshal.SizeOf(typeof(NativeDisplayBackend.Device)) == 840, "Unicode structure sizes");
            Assert(Marshal.OffsetOf(typeof(NativeDisplayBackend.Mode), "Size").ToInt32() == 68
                && Marshal.OffsetOf(typeof(NativeDisplayBackend.Mode), "Fields").ToInt32() == 72
                && Marshal.OffsetOf(typeof(NativeDisplayBackend.Mode), "Orientation").ToInt32() == 84
                && Marshal.OffsetOf(typeof(NativeDisplayBackend.Mode), "Width").ToInt32() == 172
                && Marshal.OffsetOf(typeof(NativeDisplayBackend.Mode), "Frequency").ToInt32() == 184, "DEVMODE offsets");
            Assert(NativeDisplayBackend.TestFlag == 2 && (NativeDisplayBackend.ChangeFields & 0x80000) != 0, "SDK flags");
            var mode = new NativeDisplayBackend.Mode { Size = 220, Width = 2560, Height = 1600, Bits = 32, Frequency = 60, Orientation = 1 };
            var copy = NativeDisplayBackend.FromBytes(NativeDisplayBackend.Bytes(mode));
            Assert(copy.Width == 2560 && copy.Height == 1600 && copy.Orientation == 1 && copy.DriverExtra == 0, "native backup roundtrip");
        });
        Test("Display service construction and ordinary status never enumerate or apply", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake(); int launches = 0;
            using (var display = new DisplayControlService(folder, true, backend, (t, r) => launches++))
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false, display: display)) {
                var response = session.Execute(new Request { Operation = "status" });
                display.Recover();
                Assert(response.SupportsDisplay && response.Display == null && backend.Queries == 0 && launches == 0, "implicit display access");
            }
        }));
        Test("Display explicit query returns read-only diagnostic and modes", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend, (t, r) => { throw new Exception("unexpected worker"); })) {
                var status = display.Inspect();
                Assert(status.Info.Current.Same(DisplayOriginalMode) && status.Info.Modes.Length == 2 && status.SnapshotId != null && !status.Pending, "missing diagnostic");
                Assert(backend.Applies == 0 && backend.Tests == 0 && !File.Exists(Path.Combine(folder, DisplayStore.PendingName)), "query wrote state");
            }
        }));
        Test("Display preview saves original before launching and copies selection", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake(); int launches = 0; var store = new DisplayStore(folder);
            using (var display = new DisplayControlService(folder, true, backend, (t, r) => {
                var saved = store.Plan(); Assert(saved.Token == t && !r && saved.Original.Info.Current.Same(DisplayOriginalMode), "worker started before durable original"); launches++;
            })) {
                var status = display.Inspect(); var chosen = DisplayTargetMode.Copy();
                var preview = display.Preview(new DisplayRequest { SnapshotId = status.SnapshotId, Mode = chosen }); chosen.Width = 9999;
                Assert(launches == 1 && preview.Pending && backend.Applies == 0 && store.Plan().Target.Width == 1920, "preview wrote in main or retained mutable mode");
            }
        }));
        Test("Display stale snapshot, unsupported mode and unchanged selection never arm", () => WithRecordingDirectory(folder => {
            foreach (int kind in new[] { 0, 1, 2 }) {
                long now = DateTime.UtcNow.Ticks; int launches = 0;
                using (var display = new DisplayControlService(folder, true, new DisplayBackendFake(), (t, r) => launches++, () => now)) {
                    var status = display.Inspect(); var mode = kind == 2 ? DisplayOriginalMode.Copy() : DisplayTargetMode.Copy();
                    if (kind == 0) now += TimeSpan.FromSeconds(31).Ticks;
                    if (kind == 1) mode.Frequency = 999;
                    bool rejected = false; try { display.Preview(new DisplayRequest { SnapshotId = status.SnapshotId, Mode = mode }); } catch (ArgumentException) { rejected = true; }
                    Assert(rejected && launches == 0 && !File.Exists(Path.Combine(folder, DisplayStore.PendingName)), "invalid selection armed");
                }
            }
        }));
        Test("Display worker failure keeps journal and allows only recovery launch", () => WithRecordingDirectory(folder => {
            int recovery = 0; var store = new DisplayStore(folder);
            using (var display = new DisplayControlService(folder, true, new DisplayBackendFake(), (t, r) => { if (!r) throw new IOException("cannot launch"); recovery++; })) {
                var query = display.Inspect(); var status = display.Preview(new DisplayRequest { SnapshotId = query.SnapshotId, Mode = DisplayTargetMode.Copy() });
                Assert(status.RecoveryPending && store.Plan() != null, "failed spawn lost original");
                display.Recover(); Assert(recovery == 1, "did not request recovery-only");
            }
        }));
        Test("Display confirmation belongs to exact live unexpired trial and is worker-acknowledged", () => WithRecordingDirectory(folder => {
            var store = new DisplayStore(folder); long now = DateTime.UtcNow.Ticks;
            using (var display = new DisplayControlService(folder, true, new DisplayBackendFake(), (t, r) => { }, () => now)) {
                var q = display.Inspect(); display.Preview(new DisplayRequest { SnapshotId = q.SnapshotId, Mode = DisplayTargetMode.Copy() }); var plan = store.Plan();
                using (var worker = Process.GetCurrentProcess()) store.Save(new DisplayTrialResult { Token = plan.Token, Pending = true, Phase = "AwaitingConfirmation", DeadlineUtc = now + TimeSpan.FromSeconds(15).Ticks,
                    WorkerPid = worker.Id, WorkerStarted = worker.StartTime.ToUniversalTime().Ticks });
                bool rejected = false; try { display.Decide(Guid.NewGuid().ToString("N"), true); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && store.Decision(plan.Token) == null, "foreign token accepted");
                var status = display.Decide(plan.Token, true);
                Assert(status.Phase == "AwaitingConfirmation" && status.Pending && store.Decision(plan.Token) == "confirm", "parent prematurely confirmed");
                now += TimeSpan.FromSeconds(16).Ticks; rejected = false;
                try { display.Decide(plan.Token, true); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "expired confirmation accepted");
            }
        }));
        Test("Display pending original cannot be overwritten or prematurely retired", () => WithRecordingDirectory(folder => {
            var store = new DisplayStore(folder); var plan = DisplayJob(DateTime.UtcNow.Ticks); store.Arm(plan);
            bool rejected = false; try { store.Arm(DisplayJob(DateTime.UtcNow.Ticks)); } catch (IOException) { rejected = true; }
            Assert(rejected && store.Plan().Token == plan.Token, "original overwritten"); rejected = false;
            store.Save(new DisplayTrialResult { Token = plan.Token, Pending = true, Phase = "RecoveryPending" });
            try { store.Retire(plan.Token); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected && store.Plan() != null, "pending journal removed");
            store.Save(new DisplayTrialResult { Token = plan.Token, Pending = false, Phase = "Reverted" }); store.Retire(plan.Token);
            Assert(store.Plan() == null && store.Result().Phase == "Reverted", "verified result lost");
        }));
        Test("Display malformed recovery data is retained without worker launch", () => WithRecordingDirectory(folder => {
            string path = Path.Combine(folder, DisplayStore.PendingName); File.WriteAllText(path, "broken"); int launched = 0;
            using (var display = new DisplayControlService(folder, true, new DisplayBackendFake(), (t, r) => launched++)) {
                bool rejected = false; try { display.Recover(); } catch { rejected = true; }
                Assert(rejected && launched == 0 && File.ReadAllText(path) == "broken", "corrupt backup overwritten");
                // Dispose intentionally propagates storage failures to the owning session's existing cleanup guard.
                File.Delete(path);
            }
        }));
        Test("Display result readers allow complete atomic replacements", () => WithRecordingDirectory(folder => {
            var store = new DisplayStore(folder); string token = Guid.NewGuid().ToString("N");
            store.Save(new DisplayTrialResult { Token = token, Phase = "Preparing", Pending = true });
            var writer = System.Threading.Tasks.Task.Run(() => {
                for (int i = 0; i < 100; i++) store.Save(new DisplayTrialResult { Token = token, Phase = i.ToString(), Pending = true });
            });
            while (!writer.IsCompleted) { var result = store.Result(); Assert(result != null && result.Token == token && result.Pending, "torn result"); }
            writer.GetAwaiter().GetResult(); Assert(store.Result().Phase == "99", "latest result missing");
        }));
        Test("Display session payloads cannot smuggle power, profiles or unrelated operations", () => WithRecordingDirectory(folder => {
            var h = new Fake(); var j = new Journal(); var display = new DisplayControlFake();
            using (var session = new HelperSession(new ControlEngine(h, j), folder, false, display: display)) {
                var q = session.Execute(new Request { Operation = "display_status" });
                Assert(q.Success && q.Display != null && display.Queries == 1 && h.Writes.Count == 0 && j.Saves == 0, "query touched hardware");
                foreach (var request in new[] {
                    new Request { Operation = "display_status", Values = new[] { 1 } },
                    new Request { Operation = "display_preview", Profile = new ProfileDefinition() },
                    new Request { Operation = "display_resolution", Values = new[] { 1 } },
                    new Request { Operation = "display_resolution", Profile = new ProfileDefinition() },
                    new Request { Operation = "display_resolution", Ac = true },
                    new Request { Operation = "display_refresh_rate", Values = new[] { 144 } },
                    new Request { Operation = "display_refresh_rate", Profile = new ProfileDefinition() },
                    new Request { Operation = "display_refresh_rate", Ac = true },
                    new Request { Operation = "display_confirm", Display = new DisplayRequest { TrialToken = "token", Mode = DisplayTargetMode.Copy() } },
                    new Request { Operation = "display_confirm", Display = new DisplayRequest { TrialToken = "token", RefreshRate = 144 } },
                    new Request { Operation = "display_status", Display = new DisplayRequest() },
                    new Request { Operation = "set", Key = "epp", Values = new[] { 50 }, Display = new DisplayRequest() }
                }) Assert(!session.Execute(request).Success, "foreign payload accepted");
                Assert(display.Queries == 1 && display.Previews == 0 && display.Resolutions == 0 && display.RefreshRates == 0
                    && h.Writes.Count == 0 && j.Saves == 0, "rejected payload had effects");
            }
        }));
        Test("Display optional protocol fields retain compatibility and omit native backup", () => {
            var old = Protocol.Decode<Response>("{\"Success\":true,\"Version\":1}"); Assert(!old.SupportsDisplay && old.Display == null, "old helper advertised support");
            var previous = Protocol.Decode<Response>("{\"Success\":true,\"Version\":1,\"SupportsDisplay\":true}");
            Assert(!previous.SupportsDisplayResolution, "old helper advertised direct resolution support");
            var resolutionOnly = Protocol.Decode<Response>("{\"Success\":true,\"Version\":1,\"SupportsDisplayResolution\":true}");
            Assert(!resolutionOnly.SupportsDisplayRefreshRate, "old helper advertised refresh support");
            var refresh = Protocol.Decode<Request>(Protocol.Encode(new Request { Operation = "display_refresh_rate",
                Display = new DisplayRequest { SnapshotId = "id", RefreshRate = 144 } }));
            Assert(refresh.Display.RefreshRate == 144 && refresh.Display.ResolutionHeight == 0 && refresh.Display.Mode == null, "Hz protocol roundtrip");
            var direct = Protocol.Decode<Request>(Protocol.Encode(new Request { Operation = "display_resolution",
                Display = new DisplayRequest { SnapshotId = "id", ResolutionHeight = 1000 } }));
            Assert(direct.Display.ResolutionHeight == 1000 && direct.Display.Mode == null && direct.Display.TrialToken == null, "resolution roundtrip");
            var request = new Request { Operation = "display_preview", Display = new DisplayRequest { SnapshotId = "id", Mode = DisplayTargetMode.Copy() } };
            var copy = Protocol.Decode<Request>(Protocol.Encode(request)); Assert(copy.Display.Mode.Same(DisplayTargetMode), "mode roundtrip");
            Assert(!Protocol.Encode(new DisplayStatus { Info = DisplayInitial().Info }).Contains("NativeMode"), "native journal leaked through status");
        });
    }
    private sealed class DisplayBackendFake : IDisplayBackend
    {
        internal DisplaySnapshot Current = DisplayInitial();
        internal int Queries, Tests, Applies, Restores;
        internal bool TestFails, ApplyFailsAfterChange, ApplyNoEffect, RestoreFails, RestoreNoEffect;
        internal Action Applied;
        internal Action<int> Queried;
        public DisplaySnapshot Query() { Queries++; Queried?.Invoke(Queries); return DisplayClone(Current); }
        public void Test(DisplaySnapshot original, DisplayMode target) { Tests++; if (TestFails) throw new InvalidOperationException("test failed"); }
        public void Apply(DisplaySnapshot original, DisplayMode target) {
            Applies++; if (!ApplyNoEffect) Current.Info.Current = target.Copy(); Applied?.Invoke();
            if (ApplyFailsAfterChange) throw new InvalidOperationException("apply failed");
        }
        public void Restore(DisplaySnapshot original, DisplayMode target) {
            Restores++; if (RestoreFails) throw new InvalidOperationException("restore failed"); if (!RestoreNoEffect) Current = DisplayClone(original);
        }
    }
    private sealed class DisplayPortFake : IDisplayTrialPort
    {
        internal long Elapsed, OwnerExitAt = long.MaxValue, LockAt = long.MaxValue, DecisionAt = long.MaxValue;
        internal string Choice;
        internal string FailSavePhase;
        internal long ClockSkew;
        internal Action Waited;
        internal readonly long BaseUtc = DateTime.UtcNow.Ticks;
        internal readonly List<string> Phases = new List<string>();
        public long ElapsedMilliseconds => Elapsed;
        public long UtcTicks => BaseUtc + (Elapsed + ClockSkew) * TimeSpan.TicksPerMillisecond;
        public bool OwnerAlive => Elapsed < OwnerExitAt;
        public bool Interactive => Elapsed < LockAt;
        public string Decision => Elapsed >= DecisionAt ? Choice : null;
        public void Wait() { Elapsed += 100; Waited?.Invoke(); }
        public void Save(DisplayTrialResult result) {
            Phases.Add(result.Phase);
            if (result.Phase == FailSavePhase) { FailSavePhase = null; throw new IOException("result could not be saved"); }
        }
    }
    private sealed class DisplayControlFake : IDisplayControl
    {
        public bool Supported => true;
        internal int Queries, Previews, Resolutions, RefreshRates;
        public DisplayStatus Inspect() { Queries++; return new DisplayStatus { Supported = true, Info = DisplayInitial().Info }; }
        public DisplayStatus Preview(DisplayRequest request) { Previews++; return new DisplayStatus(); }
        public DisplayStatus ApplyResolution(DisplayRequest request) { Resolutions++; return new DisplayStatus(); }
        public DisplayStatus ApplyRefreshRate(DisplayRequest request) { RefreshRates++; return new DisplayStatus(); }
        public DisplayStatus Decide(string token, bool confirm) => new DisplayStatus();
        public DisplayStatus Recover() => new DisplayStatus();
        public void CancelPending() { }
        public void Dispose() { }
    }
}
