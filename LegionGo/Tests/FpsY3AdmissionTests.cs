// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private sealed class FixtureAdmission : IFpsY3Admission
    {
        private readonly string directory;
        private readonly Action protection;
        internal FpsY3InspectionResult ActivationInspection = CleanRestored();
        internal int ActivationInspections;
        internal FixtureAdmission(string directory, Action protection) { this.directory = directory; this.protection = protection; }
        public bool IsBlocked { get { try { protection(); return File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf))
            || File.Exists(Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf)); } catch { return true; } } }
        public IFpsY3AdmissionLease EnterActivation() => FpsY3Admission.EnterActivationCore(
            () => FpsY3ExclusiveGate.Acquire(directory, "activation", protection),
            () => { ActivationInspections++; return ActivationInspection; });
        internal FpsY3ExclusiveGate EnterMaintenance() => FpsY3ExclusiveGate.Acquire(directory, "maintenance", protection);
    }
    private sealed class FixtureMaintenanceEnvironment : IFpsY3MaintenanceEnvironment
    {
        internal readonly FixtureAdmission Admission;
        internal FpsY3InspectionResult Current = CleanRestored();
        internal string Acl = new string('A', 64);
        internal int Inspections;
        internal bool IdentityChanged;
        internal Func<FpsY3InspectionResult> InspectOverride;
        internal FixtureMaintenanceEnvironment(FixtureAdmission admission) { Admission = admission; }
        public FpsY3ExclusiveGate EnterMaintenance() => Admission.EnterMaintenance();
        public FpsY3InspectionResult Inspect()
        {
            Inspections++; if (IdentityChanged) throw new FpsY3MaintenanceBlockedException("identity_changed"); var r = InspectOverride == null ? Current : InspectOverride();
            return new FpsY3InspectionResult { Version = r.Version, Scope = r.Scope, Classification = r.Classification,
                Reason = r.Reason, Phase = r.Phase, OriginalPresent = r.OriginalPresent, ErrorPresent = r.ErrorPresent,
                ContentSha256 = r.ContentSha256, FileIdentitySha256 = r.FileIdentitySha256, AuthorizesMaintenance = r.AuthorizesMaintenance };
        }
        public string ProtectionHash() => Acl;
    }
    private sealed class FixturePreparingLease : IFpsY3Lease
    {
        private readonly string path;
        internal readonly ManualResetEvent Preparing = new ManualResetEvent(false), Continue = new ManualResetEvent(false);
        internal int Starts, Restores;
        internal FixturePreparingLease(string path) { this.path = path; File.WriteAllText(path, "Restored"); }
        public bool Pending => File.ReadAllText(path) != "Restored";
        public bool PhysicalRecoveryPending => Pending;
        public bool Muted => File.ReadAllText(path) == "Muted";
        public string DevicePath { get; private set; }
        public long VerifierWarmupMs => 0;
        public long LastNativeVerifyMs => 0;
        public void Begin(string devicePath) {
            Starts++; DevicePath = devicePath; File.WriteAllText(path, "Preparing"); Preparing.Set();
            if (!Continue.WaitOne(5000)) throw new TimeoutException("Synthetic Preparing fixture timed out.");
            File.WriteAllText(path, "Muted");
        }
        public void Verify() { }
        public void Restore() { Restores++; File.WriteAllText(path, "Restored"); }
    }
    private static void AdmissionBlocked(Action action, string reason)
    {
        try { action(); } catch (FpsY3MaintenanceBlockedException ex) { Assert(ex.Reason == reason, "unexpected maintenance reason"); return; }
        throw new Exception("maintenance did not block: " + reason);
    }
    private static void FpsY3AdmissionTests()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        Test("Y3 production admission path blocks trial begin under a maintenance reservation", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            var source = new TrialSource(); var native = new TrialLease(); var output = new TrialOutput();
            using (var session = FpsY3MaintenanceSession.Begin(environment))
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                Assert(session.Classification == "restored_with_original" && !session.AuthorizesMaintenance && session.Revalidate(), "reservation authorized or lost snapshot");
                GateReject(() => trial.Start(TrialProfile()));
                Assert(native.Starts == 0 && source.Starts == 0, "blocked start reached the native lease or passive reader");
                Assert(trial.GetStatus().RecoveryPending, "held admission was hidden from status");
                session.Release(); Assert(!trial.GetStatus().RecoveryPending, "released admission remained blocked"); trial.Start(TrialProfile());
                Assert(native.Starts == 1 && native.IsPending, "start did not work after explicit release"); trial.Stop();
            }
        }));
        Test("Y3 activation rejects pending lease after admission without starting recovery", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var source = new TrialSource();
            var native = new TrialLease { IsPending = true }; var output = new TrialOutput();
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                bool failed = false; try { trial.Start(TrialProfile()); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && native.Restores == 0 && native.Starts == 0 && source.Starts == 0, "pending precondition started a recovery or reader");
                Assert(!File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "precondition left a reservation");
                native.IsPending = false;
            }
        }));
        Test("Y3 activation preserves an abandoned journal warning before opening its reader", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check) { ActivationInspection = FpsY3JournalInspector.Blocked("abandoned_mutex") };
            var source = new TrialSource(); var native = new TrialLease(); var output = new TrialOutput();
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                AdmissionBlocked(() => trial.Start(TrialProfile()), "abandoned_mutex");
                Assert(admission.ActivationInspections == 1 && native.Starts == 0 && source.Starts == 0, "abandoned signal was retried or reader started");
                Assert(trial.GetStatus().RecoveryPending, "abandoned marker was hidden from status");
                GateReject(() => admission.EnterMaintenance());
            }
        }));
        Test("Y3 activation wins atomically, pending blocks maintenance, and Stop bypasses the reservation", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            var source = new TrialSource(); var native = new TrialLease(); var output = new TrialOutput();
            var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            native.OnBegin = () => { entered.Set(); release.WaitOne(); };
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                var start = Task.Run(() => trial.Start(TrialProfile()));
                Assert(entered.WaitOne(5000), "synthetic native begin did not start");
                try { GateReject(() => FpsY3MaintenanceSession.Begin(environment)); }
                finally { release.Set(); }
                Assert(start.Wait(5000) && native.IsPending, "activation did not finish");
                environment.Current = new FpsY3InspectionResult { Classification = "pending", Phase = "Muted", OriginalPresent = true, ErrorPresent = false };
                AdmissionBlocked(() => FpsY3MaintenanceSession.Begin(environment), "pending");
                using (var reservation = admission.EnterMaintenance()) {
                    trial.Stop(); Assert(!native.IsPending && native.Restores == 1, "Stop waited for the maintenance gate");
                    reservation.Complete();
                }
            }
            entered.Dispose(); release.Dispose();
        }));
        Test("Y3 Preparing fixture cannot cross a reservation and Stop retains the independent recovery path", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            var source = new TrialSource(); var native = new FixturePreparingLease(Path.Combine(directory, "synthetic-recovery.txt")); var output = new TrialOutput();
            environment.InspectOverride = () => native.Pending
                ? new FpsY3InspectionResult { Classification = "pending", Phase = "Preparing", OriginalPresent = false, ErrorPresent = false }
                : CleanRestored();
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                using (var session = FpsY3MaintenanceSession.Begin(environment)) {
                    GateReject(() => trial.Start(TrialProfile()));
                    Assert(native.Starts == 0 && !native.Pending, "Preparing was written under a reservation"); session.Release();
                }
                var start = Task.Run(() => trial.Start(TrialProfile()));
                Assert(native.Preparing.WaitOne(5000) && native.Pending, "synthetic Preparing not reached");
                try { GateReject(() => FpsY3MaintenanceSession.Begin(environment)); }
                finally { native.Continue.Set(); }
                Assert(start.Wait(5000) && native.Muted, "synthetic activation did not finish");
                AdmissionBlocked(() => FpsY3MaintenanceSession.Begin(environment), "pending");
                using (var guard = admission.EnterMaintenance()) {
                    trial.Stop(); Assert(!native.Pending && native.Restores == 1, "synthetic recovery was blocked"); guard.Complete();
                }
            }
            native.Preparing.Dispose(); native.Continue.Dispose();
        }));
        Test("Y3 failed native begin retains admission until a later successful recovery", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var source = new TrialSource();
            var native = new TrialLease { FailBegin = true, FailRestore = true }; var output = new TrialOutput();
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                bool failed = false; try { trial.Start(TrialProfile()); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && native.IsPending, "failed begin did not preserve pending recovery");
                GateReject(() => admission.EnterMaintenance());
                native.FailRestore = false; native.FailBegin = false; trial.Stop();
                Assert(!native.IsPending, "retry did not restore the synthetic lease");
                using (var guard = admission.EnterMaintenance()) guard.Complete();
                trial.Start(TrialProfile()); trial.Stop();
            }
        }));
        Test("Y3 automatic native return retains a failed admission until an explicit Stop", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var source = new TrialSource();
            var native = new TrialLease { FailBegin = true, FailRestore = true }; var output = new TrialOutput();
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                try { trial.Start(TrialProfile()); } catch (InvalidOperationException) { }
                native.FailRestore = false; native.FailBegin = false; native.IsPending = false; native.IsMuted = false;
                trial.Tick(); Assert(trial.GetStatus().RecoveryPending, "retained admission was hidden after synthetic watcher return");
                GateReject(() => admission.EnterMaintenance()); trial.Stop();
                Assert(!trial.GetStatus().RecoveryPending, "explicit Stop did not release a safe admission");
                using (var guard = admission.EnterMaintenance()) guard.Complete();
            }
        }));
        Test("Y3 Stop closes its reader after recovery while an abandonment warning still blocks admission", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var source = new TrialSource();
            var native = new TrialLease(); var output = new TrialOutput();
            using (var trial = new FpsY3TrialService(true, source, native, output, admission, false)) {
                trial.Start(TrialProfile());
                File.WriteAllText(Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf), "synthetic warning");
                native.AbandonmentObserved = true;
                trial.Stop();
                Assert(native.Restores == 1 && !native.PhysicalRecoveryPending && native.Pending, "physical return or warning was lost");
                Assert(source.Stops == 1 && trial.GetStatus().RecoveryPending, "reader remained open or warning was hidden");
            }
        }));
        Test("Y3 reservation invalidates changed journal identity or protection without authorizing maintenance", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            using (var session = FpsY3MaintenanceSession.Begin(environment)) {
                environment.Current.FileIdentitySha256 = new string('C', 64);
                Assert(!session.Revalidate() && !session.AuthorizesMaintenance, "replacement was accepted");
                AdmissionBlocked(() => session.Revalidate(), "session_invalidated");
                GateReject(() => admission.EnterActivation()); session.Release();
            }
            environment.Current = CleanRestored();
            using (var session = FpsY3MaintenanceSession.Begin(environment)) {
                environment.Acl = new string('B', 64); Assert(!session.Revalidate(), "changed ACL fingerprint was accepted"); session.Release();
            }
        }));
        Test("Y3 reservation rejects an invalid protection fingerprint before returning a session", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission) { Acl = "not-a-hash" };
            AdmissionBlocked(() => FpsY3MaintenanceSession.Begin(environment), "snapshot_changed");
            Assert(!File.Exists(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)), "invalid preflight left an ordinary reservation");
        }));
        Test("Y3 reservation invalidates an identity change without retrying", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            using (var session = FpsY3MaintenanceSession.Begin(environment)) {
                environment.IdentityChanged = true; AdmissionBlocked(() => session.Revalidate(), "identity_changed");
                AdmissionBlocked(() => session.Revalidate(), "session_invalidated"); session.Release();
            }
        }));
        Test("Y3 abandoned journal mutex invalidates reservation and leaves a durable marker", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            environment.Current = FpsY3JournalInspector.Blocked("abandoned_mutex");
            AdmissionBlocked(() => FpsY3MaintenanceSession.Begin(environment), "abandoned_mutex");
            Assert(environment.Inspections == 1, "abandoned mutex was retried");
            GateReject(() => admission.EnterActivation());
        }));
        Test("Y3 reservation refuses to clear an abandonment detected during revalidation", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check); var environment = new FixtureMaintenanceEnvironment(admission);
            using (var session = FpsY3MaintenanceSession.Begin(environment)) {
                environment.Current = FpsY3JournalInspector.Blocked("abandoned_mutex");
                AdmissionBlocked(() => session.Revalidate(), "abandoned_mutex");
                AdmissionBlocked(() => session.Release(), "abandoned_mutex");
            }
            GateReject(() => admission.EnterMaintenance());
        }));
        Test("Y3 reservation release fails closed and sanitizes protection errors", () => GateFixture((directory, check) => {
            bool valid = true; Action guarded = () => { check(); if (!valid) throw new IOException("private fixture path"); };
            var admission = new FixtureAdmission(directory, guarded); var environment = new FixtureMaintenanceEnvironment(admission);
            using (var session = FpsY3MaintenanceSession.Begin(environment)) {
                valid = false; AdmissionBlocked(() => session.Release(), "release_uncertain");
                AdmissionBlocked(() => session.Revalidate(), "session_invalidated");
            }
            valid = true; GateReject(() => admission.EnterMaintenance());
        }));
        Test("Y3 real ACL fingerprint observes fixture file presence while the gate handle is open", () => GateFixture((directory, check) => {
            var admission = new FixtureAdmission(directory, check);
            using (var gate = admission.EnterMaintenance()) {
                string before = FpsY3MaintenanceEnvironment.CaptureProtection(directory, check);
                Assert(before.Length == 64 && before == FpsY3MaintenanceEnvironment.CaptureProtection(directory, check), "ACL fingerprint unstable");
                File.WriteAllText(Path.Combine(directory, "fps-y3-trial.json"), "fixture");
                string after = FpsY3MaintenanceEnvironment.CaptureProtection(directory, check);
                Assert(after.Length == 64 && before != after, "new fixture file protection was not detected"); gate.Complete();
            }
        }));
        Test("Y3 existing ACL validator rejects an additional synthetic write principal", () => {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            acl.SetOwner(admins);
            foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            RemapProfileStorage.CheckAcl(acl, true); FpsY3Admission.RequirePrivilegedOwner(acl); FpsY3Admission.RequireSafeAncestor(acl);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.WriteData, AccessControlType.Allow));
            bool rejected = false; try { RemapProfileStorage.CheckAcl(acl, true); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "ACL validator accepted an additional write principal");
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
            rejected = false; try { FpsY3Admission.RequireSafeAncestor(acl); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "ancestor deletion permission was accepted");
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
            rejected = false; try { FpsY3Admission.RequirePrivilegedOwner(acl); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "unprivileged fixture owner was accepted");
        });
    }
}
