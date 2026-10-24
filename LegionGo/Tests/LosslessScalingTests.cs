// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using LegionTweaks;

internal static partial class Program
{
    private static void LosslessScalingTests()
    {
        Test("Lossless Scaling is inspected only on demand and unsupported models perform no probe", () => {
            var probe = new ScalingProbe(); var detector = new LosslessScalingDetector(true, probe);
            Assert(probe.Reads == 0, "constructing the detector read or launched an application");
            var value = new LosslessScalingDetector(false, probe).Inspect();
            Assert(!value.Supported && !value.Installed && probe.Reads == 0, "unsupported model was inspected");
        });
        Test("Lossless Scaling requires a registered existing executable and preserves unknown versions", () => {
            var probe = new ScalingProbe { Exists = false }; var detector = new LosslessScalingDetector(true, probe);
            Assert(!detector.Inspect().Installed && probe.ProcessReads == 0, "missing installation was accepted or processes unnecessarily scanned");
            probe.Exists = true; probe.Version = null;
            var value = detector.Inspect();
            Assert(value.Installed && value.Running == false && value.FileVersion == null && value.CapturedAtUtc != null, "missing version became invented data");
        });
        Test("Lossless Scaling process names alone never confirm the registered application is running", () => {
            var probe = new ScalingProbe { Images = new[] { Path.Combine(Path.GetTempPath(), "other", "LosslessScaling.exe") } };
            Assert(new LosslessScalingDetector(true, probe).Inspect().Running == false, "same-name process elsewhere was accepted");
            probe.Images = new[] { probe.Executable };
            var value = new LosslessScalingDetector(true, probe).Inspect();
            Assert(value.Running == true && value.FileVersion == "3.2.0.0", "registered running image was not recognized");
        });
        Test("Lossless Scaling reports inaccessible matching processes as unknown rather than stopped", () => {
            var probe = new ScalingProbe { Images = new string[] { null } };
            Assert(new LosslessScalingDetector(true, probe).Inspect().Running == null, "inaccessible process became stopped");
            probe.Images = new[] { (string)null, probe.Executable };
            Assert(new LosslessScalingDetector(true, probe).Inspect().Running == true, "positive image evidence was lost");
        });
        Test("Lossless Scaling detection failures cannot produce an actionable installation", () => {
            var probe = new ScalingProbe { ThrowRead = true };
            var value = new LosslessScalingDetector(true, probe).Inspect();
            Assert(!value.Installed && value.Running == null && value.Message.Contains("No se pudo"), "failed scan claimed an installation");
            probe.ThrowRead = false; probe.Directories = new string[] { null };
            Assert(!new LosslessScalingDetector(true, probe).Inspect().Installed, "invalid registry directory was accepted");
            probe.Directories = new[] { "relative" };
            Assert(!new LosslessScalingDetector(true, probe).Inspect().Installed, "relative registry directory was accepted");
            probe.Directories = new[] { @"\\server\share" };
            Assert(!new LosslessScalingDetector(true, probe).Inspect().Installed, "network path was scanned");
        });
        Test("Lossless Scaling bounds registry and process samples and does not invent file versions", () => {
            var probe = new ScalingProbe { Directories = new string[9] };
            Assert(!new LosslessScalingDetector(true, probe).Inspect().Installed, "oversized registry sample accepted");
            probe = new ScalingProbe { Images = new string[65] };
            var uncertain = new LosslessScalingDetector(true, probe).Inspect();
            Assert(uncertain.Installed && uncertain.Running == null, "process uncertainty erased installation evidence or became stopped");
            foreach (string version in new[] { "not a version", new string('1', 65), "3.2\n0" }) {
                probe = new ScalingProbe { Version = version };
                var value = new LosslessScalingDetector(true, probe).Inspect();
                Assert(value.Installed && value.FileVersion == null, "untrusted metadata entered the version label");
            }
        });
        Test("Multiple Lossless Scaling installations use version only for an identified running copy", () => {
            string first = Path.Combine(Path.GetTempPath(), "scaling-one"), second = Path.Combine(Path.GetTempPath(), "scaling-two");
            var probe = new ScalingProbe { Directories = new[] { first, second } };
            var detector = new LosslessScalingDetector(true, probe);
            Assert(detector.Inspect().FileVersion == null, "multiple installations picked an arbitrary version");
            probe.Images = new[] { Path.Combine(second, "LosslessScaling.exe") };
            Assert(detector.Inspect().FileVersion != null && probe.VersionPath == probe.Images[0], "version did not follow the running copy");
            probe.Images = new[] { Path.Combine(first, "LosslessScaling.exe"), Path.Combine(second, "LosslessScaling.exe") };
            Assert(detector.Inspect().FileVersion == null, "multiple running copies claimed a single version");
        });
        Test("Lossless Scaling process inspection failure preserves independently verified installation", () => {
            var probe = new ScalingProbe { ThrowProcesses = true };
            var value = new LosslessScalingDetector(true, probe).Inspect();
            Assert(value.Installed && value.Running == null && value.FileVersion == "3.2.0.0", "process failure erased independent file evidence");
        });
        Test("Lossless Scaling status is explicit read-only IPC with a fixed launch URI", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var probe = new ScalingProbe();
            using (var session = new HelperSession(new ControlEngine(hardware, journal), folder, false,
                losslessScaling: new LosslessScalingDetector(true, probe))) {
                for (int i = 0; i < 3; i++) session.Execute(new Request { Operation = "recording_status" });
                Assert(probe.Reads == 0, "background status polled the registry/process list");
                var value = session.Execute(new Request { Operation = "lossless_status" });
                Assert(value.Success && value.SupportsLosslessScaling && value.LosslessScaling.Installed
                    && hardware.Reads.Count == 0 && hardware.Writes.Count == 0 && journal.Saves == 0, "application detection touched hardware/power");
                Assert(LosslessScalingAccess.LaunchUri == "steam://run/993090", "launch URI became configurable");
            }
        }));
        Test("Lossless Scaling rejects foreign payloads protocol mismatch and unavailable providers before inspection", () => WithRecordingDirectory(folder => {
            var probe = new ScalingProbe();
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false,
                losslessScaling: new LosslessScalingDetector(true, probe))) {
                foreach (Action<Request> corrupt in new Action<Request>[] {
                    r => r.Version = 99, r => r.Key = "file.exe", r => r.Values = new[] { 1 }, r => r.Ac = true,
                    r => r.Profile = FullProfile(), r => r.RtssAppearance = new RtssAppearance(), r => r.Display = new DisplayRequest() }) {
                    var request = new Request { Operation = "lossless_status" }; corrupt(request);
                    Assert(!session.Execute(request).Success && probe.Reads == 0, "payload reached the detection adapter");
                }
            }
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
                Assert(!session.Execute(new Request { Operation = "lossless_status" }).Success, "missing provider claimed integration");
            var old = Protocol.Decode<Response>("{}");
            Assert(!old.SupportsLosslessScaling && old.LosslessScaling == null, "old helper acquired a capability");
        }));
    }
    private sealed class ScalingProbe : ILosslessScalingProbe
    {
        internal string[] Directories = new[] { Path.Combine(Path.GetTempPath(), "Lossless Scaling") };
        internal string[] Images = new string[0];
        internal bool Exists = true, ThrowRead, ThrowProcesses;
        internal int Reads, ProcessReads;
        internal string Version = "3.2.0.0", VersionPath;
        internal string Executable => Path.Combine(Directories[0], "LosslessScaling.exe");
        public string[] InstallationDirectories() { Reads++; if (ThrowRead) throw new IOException(); return Directories; }
        public bool ExecutableExists(string path) => Exists;
        public string FileVersion(string path) { VersionPath = path; return Version; }
        public string[] RunningImages() { ProcessReads++; if (ThrowProcesses) throw new IOException(); return Images; }
    }
}
