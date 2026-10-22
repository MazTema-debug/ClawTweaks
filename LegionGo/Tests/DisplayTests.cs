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
                    new Request { Operation = "display_confirm", Display = new DisplayRequest { TrialToken = "token", Mode = DisplayTargetMode.Copy() } },
                    new Request { Operation = "display_status", Display = new DisplayRequest() },
                    new Request { Operation = "set", Key = "epp", Values = new[] { 50 }, Display = new DisplayRequest() }
                }) Assert(!session.Execute(request).Success, "foreign payload accepted");
                Assert(display.Queries == 1 && display.Previews == 0 && h.Writes.Count == 0 && j.Saves == 0, "rejected payload had effects");
            }
        }));
        Test("Display optional protocol fields retain compatibility and omit native backup", () => {
            var old = Protocol.Decode<Response>("{\"Success\":true,\"Version\":1}"); Assert(!old.SupportsDisplay && old.Display == null, "old helper advertised support");
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
        internal int Queries, Previews;
        public DisplayStatus Inspect() { Queries++; return new DisplayStatus { Supported = true, Info = DisplayInitial().Info }; }
        public DisplayStatus Preview(DisplayRequest request) { Previews++; return new DisplayStatus(); }
        public DisplayStatus Decide(string token, bool confirm) => new DisplayStatus();
        public DisplayStatus Recover() => new DisplayStatus();
        public void CancelPending() { }
        public void Dispose() { }
    }
}
