// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using LegionTweaks;

internal static partial class Program
{
    private static PawnIoStatus PawnReady(string version = "2.2.0.0") => new PawnIoStatus {
        Installed = true, Usable = true, DriverRunning = true, Version = version
    };
    private static void PawnIoDependencyTests()
    {
        Test("PawnIO construction and cached polling never inspect or launch", () => {
            var platform = new PawnPlatform(); var queue = new List<Action>();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            for (int i = 0; i < 10; i++) dependency.GetStatus();
            Assert(platform.Reads == 0 && platform.Installs == 0 && queue.Count == 0, "implicit dependency action");
            var copy = dependency.GetStatus(); copy.CanInstall = true;
            Assert(!dependency.GetStatus().CanInstall, "caller mutated cached authorization");
        });
        Test("PawnIO unsupported hardware never probes or schedules installation", () => {
            var platform = new PawnPlatform(); int scheduled = 0;
            var dependency = new PawnIoDependency(false, platform, work => scheduled++);
            Assert(!dependency.Refresh().Supported && !dependency.BeginInstall().CanInstall
                && platform.Reads == 0 && scheduled == 0, "unsupported machine performed dependency work");
        });
        Test("PawnIO missing detection permits only an explicit installer action", () => {
            var platform = new PawnPlatform(); int scheduled = 0;
            var dependency = new PawnIoDependency(true, platform, work => scheduled++);
            var status = dependency.Refresh();
            Assert(!status.Installed && !status.Usable && status.CanInstall && !status.Busy
                && status.CapturedAtUtc != null && scheduled == 0 && platform.Installs == 0, "probe installed or invented availability");
        });
        Test("PawnIO compatible and newer installations are preserved without reinstall or downgrade", () => {
            foreach (string version in new[] { "2.2.0", "2.2.0.0", "3.0.0" }) {
                var platform = new PawnPlatform { Current = PawnReady(version) }; int scheduled = 0;
                var dependency = new PawnIoDependency(true, platform, work => scheduled++);
                Assert(dependency.Refresh().Usable && !dependency.BeginInstall().CanInstall
                    && scheduled == 0 && platform.Installs == 0, "existing dependency replaced");
                Assert(dependency.GetStatus().Message.Contains("aún no está disponible"), "dependency advertised CPU frequencies");
            }
        });
        Test("PawnIO older known versions allow updating but do not claim readiness", () => {
            var platform = new PawnPlatform { Current = PawnReady("2.1.0") };
            var status = new PawnIoDependency(true, platform).Refresh();
            Assert(status.Installed && !status.Usable && status.CanInstall, "old version accepted or update blocked");
        });
        Test("PawnIO ambiguous or unhealthy existing installations require review without replacement", () => {
            foreach (string version in new[] { null, "invalid", "2.2.0", "3.0.0" }) {
                var platform = new PawnPlatform { Current = PawnReady(version) };
                platform.Current.Usable = false; int scheduled = 0;
                var dependency = new PawnIoDependency(true, platform, work => scheduled++);
                Assert(dependency.BeginInstall().InstallState == "review" && !dependency.GetStatus().CanInstall
                    && scheduled == 0, "ambiguous installation overwritten");
            }
        });
        Test("PawnIO failed inventory blocks installation until a fresh successful probe", () => {
            var platform = new PawnPlatform { ReadError = true }; int scheduled = 0;
            var dependency = new PawnIoDependency(true, platform, work => scheduled++);
            var status = dependency.BeginInstall();
            Assert(status.InstallState == "error" && !status.CanInstall && scheduled == 0, "failed inventory authorized installation");
            platform.ReadError = false;
            Assert(dependency.Refresh().CanInstall && scheduled == 0, "read recovery launched installer");
        });
        Test("PawnIO duplicate requests and refresh during a queued install stay single flight", () => {
            var platform = new PawnPlatform(); var queue = new List<Action>();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            Assert(dependency.BeginInstall().Busy && queue.Count == 1, "explicit install did not queue");
            int reads = platform.Reads;
            Assert(dependency.BeginInstall().Busy && dependency.Refresh().Busy && !dependency.GetStatus().CanInstall
                && queue.Count == 1 && platform.Reads == reads, "duplicate installation or blocking re-probe");
            queue[0]();
            Assert(platform.Installs == 1 && !dependency.GetStatus().Busy, "worker never completed");
        });
        Test("PawnIO queued install rechecks external installations before opening vendor UI", () => {
            var platform = new PawnPlatform(); var queue = new List<Action>();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            dependency.BeginInstall(); platform.Current = PawnReady("3.0.0"); queue[0]();
            Assert(platform.Installs == 0 && dependency.GetStatus().Usable && !dependency.GetStatus().Busy, "queued installer replaced new external install");
        });
        Test("PawnIO inventory failure at worker start does not open installer", () => {
            var platform = new PawnPlatform(); var queue = new List<Action>();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            dependency.BeginInstall(); platform.ReadError = true; queue[0]();
            Assert(platform.Installs == 0 && !dependency.GetStatus().Busy && !dependency.GetStatus().CanInstall
                && dependency.GetStatus().InstallState == "failed", "failed worker inventory launched installer");
        });
        Test("PawnIO successful installation requires a fresh dependency check after vendor exit", () => {
            var platform = new PawnPlatform(); var queue = new List<Action>();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            platform.DuringInstall = phase => {
                phase("installer-open");
                Assert(dependency.GetStatus().Busy && dependency.GetStatus().InstallState == "installer-open"
                    && !dependency.GetStatus().Usable, "open installer claimed completion");
                platform.Current = PawnReady();
            };
            dependency.BeginInstall(); queue[0]();
            Assert(dependency.GetStatus().Usable && !dependency.GetStatus().Busy && platform.Reads == 3, "completion was not re-probed");
        });
        Test("PawnIO vendor exit alone never claims a successful install", () => {
            var platform = new PawnPlatform(); var queue = new List<Action>();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            dependency.BeginInstall(); queue[0]();
            Assert(!dependency.GetStatus().Usable && !dependency.GetStatus().Busy
                && dependency.GetStatus().InstallState == "not-confirmed", "vendor exit became installed");
        });
        Test("PawnIO cancellation and download or validation failures remain explicit and retryable", () => {
            foreach (Exception error in new Exception[] { new Win32Exception(1223), new InvalidOperationException("synthetic validation failure") }) {
                var platform = new PawnPlatform { DuringInstall = phase => { throw error; } }; var queue = new List<Action>();
                var dependency = new PawnIoDependency(true, platform, queue.Add);
                dependency.BeginInstall(); queue[0]();
                var result = dependency.GetStatus();
                Assert(!result.Busy && !result.CanInstall && !result.Usable
                    && result.InstallState == (error is Win32Exception ? "cancelled" : "failed"), "failure claimed success or stayed busy");
                Assert(dependency.Refresh().CanInstall && queue.Count == 1, "fresh read could not retry or auto-installed");
            }
        });
        Test("PawnIO failed scheduling releases the busy state without launch", () => {
            var platform = new PawnPlatform();
            var result = new PawnIoDependency(true, platform, work => { throw new InvalidOperationException(); }).BeginInstall();
            Assert(!result.Busy && !result.CanInstall && result.InstallState == "failed" && platform.Installs == 0, "scheduler failure stuck busy");
        });
        Test("PawnIO installer acceptance requires the fixed release size hash and trusted publisher", () => {
            Assert(PawnIoPackage.IsAcceptedInstaller(PawnIoPackage.InstallerBytes, PawnIoPackage.InstallerSha256.ToUpperInvariant(), true), "fixed artifact rejected");
            Assert(!PawnIoPackage.IsAcceptedInstaller(PawnIoPackage.InstallerBytes - 1, PawnIoPackage.InstallerSha256, true)
                && !PawnIoPackage.IsAcceptedInstaller(PawnIoPackage.InstallerBytes, new string('0', 64), true)
                && !PawnIoPackage.IsAcceptedInstaller(PawnIoPackage.InstallerBytes, PawnIoPackage.InstallerSha256, false)
                && !PawnIoPackage.IsAcceptedInstaller(PawnIoPackage.InstallerBytes, null, true), "unverified artifact authorized");
        });
        Test("PawnIO IPC polling uses cache and explicit checks leave power controller and RTSS untouched", () => WithRecordingDirectory(folder => {
            var platform = new PawnPlatform(); var queue = new List<Action>(); var h = new Fake(); var journal = new Journal(); var rtss = new SessionRtss();
            var dependency = new PawnIoDependency(true, platform, queue.Add);
            using (var session = new HelperSession(new ControlEngine(h, journal), folder, false, rtss: rtss, pawnIo: dependency)) {
                var cached = session.Execute(new Request { Operation = "recording_status" });
                Assert(cached.Success && cached.SupportsPawnIo && cached.PawnIo.InstallState == "unknown" && platform.Reads == 0, "poll inspected dependency");
                Assert(session.Execute(new Request { Operation = "pawnio_status" }).PawnIo.CanInstall && platform.Reads == 1
                    && queue.Count == 0, "status installed dependency");
                Assert(session.Execute(new Request { Operation = "pawnio_install" }).PawnIo.Busy && queue.Count == 1, "explicit installer IPC lost");
                Assert(session.Execute(new Request { Operation = "recording_status" }).PawnIo.Busy && platform.Reads == 2, "busy polling performed inventory");
                Assert(h.Reads.Count == 0 && h.Writes.Count == 0 && journal.Saves == 0 && rtss.Sets == 0 && rtss.Configures == 0, "dependency touched unrelated hardware");
            }
        }));
        Test("PawnIO IPC rejects mixed payloads and mismatched protocols before inventory or launch", () => WithRecordingDirectory(folder => {
            var platform = new PawnPlatform(); int scheduled = 0; var h = new Fake();
            using (var session = new HelperSession(new ControlEngine(h, new Journal()), folder, false,
                pawnIo: new PawnIoDependency(true, platform, work => scheduled++))) {
                foreach (string op in new[] { "pawnio_status", "pawnio_install" }) foreach (Action<Request> mutate in new Action<Request>[] {
                    r => r.Key = "https://arbitrary.example/installer", r => r.Values = new int[0], r => r.Ac = true,
                    r => r.Profile = new ProfileDefinition(), r => r.Display = new DisplayRequest(),
                    r => r.RtssAppearance = new RtssAppearance(), r => r.GameBarInput = new GameBarInputContext(),
                    r => r.Remap = new RemapEditingRequest(), r => r.AmdGraphics = new AmdGraphicsRequest(), r => r.Version = 999
                }) {
                    var request = new Request { Operation = op }; mutate(request);
                    Assert(!session.Execute(request).Success, "mixed dependency payload accepted");
                }
                Assert(platform.Reads == 0 && scheduled == 0 && h.Writes.Count == 0, "invalid request reached hardware or installer");
            }
        }));
        Test("PawnIO wire fields are optional and old helpers never advertise installation capability", () => WithRecordingDirectory(folder => {
            var old = Protocol.Decode<Response>("{\"Success\":true,\"Version\":1}");
            Assert(!old.SupportsPawnIo && old.PawnIo == null, "old helper invented dependency capability");
            var decoded = Protocol.Decode<Response>(Protocol.Encode(new Response { SupportsPawnIo = true, PawnIo = PawnReady() }));
            Assert(decoded.SupportsPawnIo && decoded.PawnIo.Installed && decoded.PawnIo.Version == "2.2.0.0", "dependency wire data lost");
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
                Assert(!session.Execute(new Request { Operation = "pawnio_install" }).Success, "missing provider accepted installer request");
        }));
    }
    private sealed class PawnPlatform : IPawnIoPlatform
    {
        internal PawnIoStatus Current = new PawnIoStatus();
        internal int Reads, Installs;
        internal bool ReadError;
        internal Action<Action<string>> DuringInstall;
        public PawnIoStatus Inspect() { Reads++; if (ReadError) throw new InvalidOperationException("synthetic inventory failure"); return Current; }
        public void Install(Action<string> phase) { Installs++; DuringInstall?.Invoke(phase); }
    }
}
