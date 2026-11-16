// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private const string GateFixturePrefix = "LegionTweaks-GateFixture-";
    private static void GateFixture(Action<string, Action> body)
    {
        string directory = Path.Combine(Path.GetTempPath(), GateFixturePrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Action check = () => {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Synthetic directory changed.");
        };
        try { body(directory, check); }
        finally {
            string file = Path.Combine(directory, FpsY3ExclusiveGate.Leaf);
            if (File.Exists(file)) File.Delete(file);
            string recovery = Path.Combine(directory, "synthetic-recovery.txt");
            if (File.Exists(recovery)) File.Delete(recovery);
            string journal = Path.Combine(directory, "fps-y3-trial.json");
            if (File.Exists(journal)) File.Delete(journal);
            string warning = Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf);
            if (File.Exists(warning)) File.Delete(warning);
            Directory.Delete(directory, false);
        }
    }
    private static void GateReject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; }
        Assert(rejected, "exclusive gate unexpectedly accepted an occupied fixture");
    }
    private static FpsY3InspectionResult CleanRestored() => new FpsY3InspectionResult {
        Classification = "restored_with_original", Phase = "Restored", OriginalPresent = true, ErrorPresent = false,
        ContentSha256 = new string('A', 64), FileIdentitySha256 = new string('B', 64) };

    private static void AbandonedJournalFixture(Action<string> body)
    {
        string name = @"Local\LegionTweaks.JournalFixture." + Guid.NewGuid().ToString("N");
        using (var keeper = new Mutex(false, name))
        using (var acquired = new ManualResetEvent(false)) {
            var owner = new Thread(() => {
                using (var owned = new Mutex(false, name))
                    if (owned.WaitOne(5000)) acquired.Set(); // Exit without ReleaseMutex.
            }) { IsBackground = true };
            owner.Start();
            Assert(acquired.WaitOne(5000) && owner.Join(5000), "synthetic mutex owner did not exit");
            body(name);
        }
    }

    // Only disposable synthetic child processes use this fixture entry.
    private static bool TryRunY3GateFixture(string[] args)
    {
        if (args == null || args.Length != 3 || args[0] != "--test-y3-admission") return false;
        string directory = Path.GetFullPath(args[2]); var info = new DirectoryInfo(directory); Guid id;
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(info.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), temp, StringComparison.OrdinalIgnoreCase)
            || !info.Name.StartsWith(GateFixturePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(info.Name.Substring(GateFixturePrefix.Length), "N", out id)
            || (args[1] != "hold" && args[1] != "crash")) throw new InvalidOperationException("Invalid test fixture.");
        Action check = () => { if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException(); };
        using (var gate = FpsY3ExclusiveGate.Acquire(directory, "maintenance", check)) {
            Console.WriteLine("READY"); Console.Out.Flush();
            if (args[1] == "crash") Environment.Exit(0); // bypass Dispose, as on owner loss
            Console.ReadLine(); gate.Complete();
        }
        return true;
    }
    private static ProcessStartInfo GateChildInfo(string directory, string mode)
    {
        string host = Process.GetCurrentProcess().MainModule.FileName;
        string args = "--test-y3-admission " + mode + " \"" + directory + "\"";
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            args = "\"" + Assembly.GetEntryAssembly().Location + "\" " + args;
        return new ProcessStartInfo(host, args) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    }
    private static void FpsY3ExclusiveGateTests()
    {
        Test("Y3 maintenance snapshot comparison rejects untrusted, changed and noncandidate results", () => {
            var absent = new FpsY3InspectionResult { Classification = "absent" };
            Assert(FpsY3MaintenanceSnapshot.SameCandidate(absent, new FpsY3InspectionResult { Classification = "absent" }), "stable absence rejected");
            var a = CleanRestored(); var b = CleanRestored();
            Assert(FpsY3MaintenanceSnapshot.SameCandidate(a, b) && !a.AuthorizesMaintenance, "stable original rejected or authorized");
            b.ContentSha256 = new string('C', 64); Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "changed bytes accepted");
            b = CleanRestored(); b.FileIdentitySha256 = new string('C', 64); Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "replacement accepted");
            b = CleanRestored(); b.AuthorizesMaintenance = true; Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "authorization-bearing output accepted");
            b = CleanRestored(); b.ErrorPresent = true; Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "recorded error accepted");
            b = CleanRestored(); b.OriginalPresent = false; Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "missing original accepted");
            b = CleanRestored(); b.Scope = "other"; Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "unknown identity scope accepted");
            b = CleanRestored(); b.Version = 2; Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "unknown protocol accepted");
            b = CleanRestored(); b.FileIdentitySha256 = new string('z', 64); Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "invalid digest accepted");
            foreach (string classification in new[] { "pending", "blocked", "restored_without_original", "unknown" }) {
                b = CleanRestored(); b.Classification = classification;
                Assert(!FpsY3MaintenanceSnapshot.SameCandidate(a, b), "noncandidate accepted: " + classification);
            }
            Assert(!FpsY3MaintenanceSnapshot.SameCandidate(absent, a), "absence to presence accepted");
            absent.Reason = "changed"; Assert(!FpsY3MaintenanceSnapshot.SameCandidate(absent, absent), "ambiguous absence accepted");
        });
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        Test("Y3 abandoned journal mutex records a persistent warning and still runs recovery", () => GateFixture((directory, check) => {
            AbandonedJournalFixture(name => {
                int recorded = 0, uncertain = 0, recovery = 0;
                Action<bool> protectedFixture = required => {
                    check(); if (required && !File.Exists(Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf))) throw new IOException();
                };
                FpsY3Admission.RequireNoAbandonment(directory, protectedFixture, false);
                FpsY3JournalMutex.Execute(name, () => { recorded++; FpsY3AbandonmentRecorder.Record(directory, protectedFixture); },
                    () => uncertain++, () => recovery++);
                Assert(recorded == 1 && uncertain == 0 && recovery == 1, "warning or recovery did not occur");
                AdmissionBlocked(() => FpsY3Admission.RequireNoAbandonment(directory, protectedFixture, false), "abandoned_mutex");
                byte[] original = File.ReadAllBytes(Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf));
                FpsY3AbandonmentRecorder.Record(directory, protectedFixture);
                Assert(Convert.ToBase64String(original) == Convert.ToBase64String(File.ReadAllBytes(Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf))), "warning overwritten");
            });
        }));
        Test("Y3 abandonment recording failure marks uncertainty without blocking recovery", () => {
            AbandonedJournalFixture(name => {
                int uncertain = 0, recovery = 0;
                FpsY3JournalMutex.Execute(name, () => { throw new IOException("synthetic failure"); },
                    () => uncertain++, () => recovery++);
                Assert(uncertain == 1 && recovery == 1, "record failure stopped recovery or hid uncertainty");
                AdmissionBlocked(() => FpsY3Admission.RequireNoAbandonment("unused", required => { throw new Exception("unexpected check"); }, true), "abandoned_mutex");
            });
        });

        Test("Y3 legacy instance fixture preserves createdNew exclusion across normal owner exit for two identities", () => {
            string sidOne = "S-1-5-21-101-102-103-1001", sidTwo = "S-1-5-21-101-102-103-1002";
            string prefix = @"Local\LegionTweaks.LegacyFixture." + Guid.NewGuid().ToString("N") + ".";
            Func<string, string> name = sid => prefix + (sid == sidOne ? "one" : "two");
            bool created; var old = new Mutex(true, name(sidOne), out created);
            Assert(created, "old fixture principal was not the first owner");
            try {
                using (var fence = FpsY3LegacyInstanceFence.AcquireVerifiedSids(new[] { sidOne, sidTwo }, sid => new Mutex(false, name(sid)))) {
                    Assert(fence.SidCount == 2, "fixture identity count not retained");
                    old.ReleaseMutex(); old.Dispose(); old = null;
                    foreach (string sid in new[] { sidOne, sidTwo }) {
                        using (var contender = new Mutex(true, name(sid), out created)) {
                            if (created) contender.ReleaseMutex();
                            Assert(!created, "legacy fixture principal started under retained handle");
                        }
                    }
                }
                using (var after = new Mutex(true, name(sidOne), out created)) {
                    if (created) after.ReleaseMutex();
                    Assert(created, "legacy fixture principal could not start after release");
                }
            }
            finally { if (old != null) { old.ReleaseMutex(); old.Dispose(); } }
        });
        Test("Y3 legacy instance fixture rejects duplicate SIDs and releases partial acquisitions", () => {
            string one = "S-1-5-21-101-102-103-1001", two = "S-1-5-21-101-102-103-1002";
            string name = @"Local\LegionTweaks.LegacyFixture." + Guid.NewGuid().ToString("N"); int calls = 0; bool rejected = false;
            try { FpsY3LegacyInstanceFence.AcquireVerifiedSids(new[] { one, one }, sid => { calls++; return new Mutex(false, name); }); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && calls == 0, "duplicate identity opened a mutex");
            rejected = false;
            try { FpsY3LegacyInstanceFence.AcquireVerifiedSids(new[] { one, two }, sid => {
                if (++calls == 2) throw new IOException("synthetic open failure"); return new Mutex(false, name);
            }); } catch (IOException) { rejected = true; }
            Assert(rejected && calls == 2, "partial acquisition failure was hidden");
            bool created; using (var after = new Mutex(true, name, out created)) {
                if (created) after.ReleaseMutex();
                Assert(created, "failed acquisition leaked a legacy fixture handle");
            }
        });

        Test("Y3 fixture gate allows exactly one concurrent activation or maintenance contender", () => GateFixture((directory, check) => {
            var start = new ManualResetEvent(false); var gates = new FpsY3ExclusiveGate[2]; var rejected = new bool[2];
            var tasks = new Task[2];
            for (int n = 0; n < 2; n++) { int i = n; tasks[i] = Task.Run(() => {
                start.WaitOne(); try { gates[i] = FpsY3ExclusiveGate.Acquire(directory, i == 0 ? "activation" : "maintenance", check); }
                catch (IOException) { rejected[i] = true; }
            }); }
            start.Set(); Task.WaitAll(tasks); start.Dispose();
            Assert((gates[0] != null ? 1 : 0) + (gates[1] != null ? 1 : 0) == 1 && rejected[0] != rejected[1], "race admitted multiple owners");
            var winner = gates[0] ?? gates[1];
            try { winner.Complete(); } finally { winner.Dispose(); }
            Assert(!File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "completed gate not released");
        }));
        Test("Y3 fixture gate stays durable when an owner exits without completion", () => GateFixture((directory, check) => {
            using (FpsY3ExclusiveGate.Acquire(directory, "maintenance", check)) { }
            string file = Path.Combine(directory, FpsY3ExclusiveGate.Leaf); byte[] before = File.ReadAllBytes(file);
            GateReject(() => FpsY3ExclusiveGate.Acquire(directory, "activation", check));
            Assert(Convert.ToBase64String(before) == Convert.ToBase64String(File.ReadAllBytes(file)), "stale marker was modified");
        }));
        Test("Y3 fixture gate fails closed on directory validation failure before and after creation", () => GateFixture((directory, check) => {
            bool failed = false;
            try { FpsY3ExclusiveGate.Acquire(directory, "activation", () => { throw new IOException(); }); } catch (IOException) { failed = true; }
            Assert(failed && !File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "failed initial check created a marker");
            int checks = 0; failed = false;
            try { FpsY3ExclusiveGate.Acquire(directory, "activation", () => { check(); if (++checks == 2) throw new IOException(); }); }
            catch (IOException) { failed = true; }
            Assert(failed && File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "post-create failure erased marker");
            GateReject(() => FpsY3ExclusiveGate.Acquire(directory, "maintenance", check));
        }));
        Test("Y3 fixture gate does not clear a marker if protection fails at completion", () => GateFixture((directory, check) => {
            bool protectedNow = true;
            Action current = () => { check(); if (!protectedNow) throw new IOException(); };
            using (var gate = FpsY3ExclusiveGate.Acquire(directory, "maintenance", current)) {
                protectedNow = false; GateReject(() => gate.Complete());
            }
            Assert(File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "failed completion cleared marker");
            GateReject(() => FpsY3ExclusiveGate.Acquire(directory, "activation", check));
        }));
        Test("Y3 fixture gate blocks deletion while owned and leaves unrelated recovery resource available", () => GateFixture((directory, check) => {
            using (var gate = FpsY3ExclusiveGate.Acquire(directory, "maintenance", check)) {
                GateReject(() => File.Delete(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)));
                string recovery = Path.Combine(directory, "synthetic-recovery.txt");
                File.WriteAllText(recovery, "recovery remains independent");
                Assert(File.ReadAllText(recovery) == "recovery remains independent", "unrelated fixture blocked");
                gate.Complete();
            }
        }));
        Test("Y3 fixture gate excludes another process and retains marker after abrupt owner exit", () => GateFixture((directory, check) => {
            using (var child = Process.Start(GateChildInfo(directory, "hold"))) {
                var ready = child.StandardOutput.ReadLineAsync(); Assert(ready.Wait(10000) && ready.Result == "READY", "child gate not ready");
                GateReject(() => FpsY3ExclusiveGate.Acquire(directory, "activation", check));
                child.StandardInput.WriteLine("complete"); child.StandardInput.Flush();
                Assert(child.WaitForExit(10000) && child.ExitCode == 0, "child did not complete");
            }
            using (var child = Process.Start(GateChildInfo(directory, "crash"))) {
                var ready = child.StandardOutput.ReadLineAsync(); Assert(ready.Wait(10000) && ready.Result == "READY", "crash child gate not ready");
                Assert(child.WaitForExit(10000) && child.ExitCode == 0, "crash child did not exit");
            }
            Assert(File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "owner loss removed durable marker");
            GateReject(() => FpsY3ExclusiveGate.Acquire(directory, "maintenance", check));
        }));
    }
}
