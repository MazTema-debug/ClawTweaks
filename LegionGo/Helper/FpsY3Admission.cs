// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LegionTweaks
{
    internal interface IFpsY3Admission
    {
        bool IsBlocked { get; }
        IFpsY3AdmissionLease EnterActivation();
    }
    internal interface IFpsY3AdmissionLease : IDisposable
    {
        void Complete();
    }

    // The production directory is constrained by RemapProfileStorage to the
    // current Windows identity's protected ProgramData directory. No arbitrary
    // path or SID is accepted by the public maintenance entry point.
    internal sealed class FpsY3Admission : IFpsY3Admission
    {
        private static int unrecordedAbandonment;
        private readonly string directory;
        private readonly string sid;
        private readonly RemapProfileStorage protection;
        internal FpsY3Admission(string directory)
        {
            this.directory = directory;
            using (var identity = WindowsIdentity.GetCurrent()) {
                if (identity.User == null || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("La admisión Y3 requiere una identidad elevada.");
                sid = identity.User.Value;
            }
            protection = new RemapProfileStorage(directory);
        }
        internal static void RequirePrivilegedOwner(FileSystemSecurity security)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            if (owner == null || !owner.Equals(admins) && !owner.Equals(system) && !owner.Equals(trustedInstaller))
                throw new InvalidDataException("La propiedad del directorio o archivo Y3 no es privilegiada.");
        }
        internal static void RequireSafeAncestor(DirectorySecurity security)
        {
            RequirePrivilegedOwner(security);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var trustedInstaller = new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
            const FileSystemRights dangerous = FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue;
                var principal = rule.IdentityReference as SecurityIdentifier;
                if ((rule.FileSystemRights & dangerous) != 0 && (principal == null
                    || !principal.Equals(admins) && !principal.Equals(system) && !principal.Equals(trustedInstaller)))
                    throw new InvalidDataException("Un directorio padre permite eliminar o alterar la reserva Y3.");
            }
        }
        internal void RequireProtectedLeaf(string leaf, bool required)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != sid
                    || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Cambió la identidad de la admisión Y3.");
            protection.RequireProtectedLeaf(leaf);
            var drive = new DriveInfo(Path.GetPathRoot(directory));
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("La reserva Y3 requiere un volumen NTFS local.");
            for (var ancestor = new DirectoryInfo(directory).Parent; ancestor != null; ancestor = ancestor.Parent)
                RequireSafeAncestor(ancestor.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));
            RequirePrivilegedOwner(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner));
            string path = Path.Combine(directory, leaf);
            try {
                File.GetAttributes(path);
                RequirePrivilegedOwner(new FileInfo(path).GetAccessControl(AccessControlSections.Owner));
            }
            catch (FileNotFoundException) { if (required) throw; }
        }
        private void RequireProtectedGate(bool required)
        {
            RequireNoAbandonment();
            RequireProtectedLeaf(FpsY3ExclusiveGate.Leaf, required);
            RequireProtectedLeaf("fps-y3-trial.json", false);
        }
        internal void RequireNoAbandonment() => RequireNoAbandonment(directory,
            required => RequireProtectedLeaf(FpsY3AbandonmentRecorder.Leaf, required),
            Volatile.Read(ref unrecordedAbandonment) != 0);
        internal static void RequireNoAbandonment(string directory, Action<bool> requireProtected, bool uncertain)
        {
            if (uncertain)
                throw new FpsY3MaintenanceBlockedException("abandoned_mutex");
            requireProtected(false);
            try {
                File.GetAttributes(Path.Combine(directory, FpsY3AbandonmentRecorder.Leaf));
                throw new FpsY3MaintenanceBlockedException("abandoned_mutex");
            }
            catch (FileNotFoundException) { }
        }
        internal static void RememberUnrecordedAbandonment() { Interlocked.Exchange(ref unrecordedAbandonment, 1); }
        internal static void RecordJournalAbandoned(string directory)
        {
            var admission = new FpsY3Admission(directory);
            FpsY3AbandonmentRecorder.Record(directory, required => admission.RequireProtectedLeaf(FpsY3AbandonmentRecorder.Leaf, required));
        }
        public bool IsBlocked {
            get {
                try {
                    RequireProtectedGate(false);
                    try { File.GetAttributes(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)); return true; }
                    catch (FileNotFoundException) { return false; }
                }
                catch { return true; }
            }
        }
        public IFpsY3AdmissionLease EnterActivation() => EnterActivationCore(
            () => FpsY3ExclusiveGate.Acquire(directory, "activation", (Action<bool>)RequireProtectedGate),
            () => FpsY3JournalInspector.InspectCurrentUser(directory));
        internal static IFpsY3AdmissionLease EnterActivationCore(Func<FpsY3ExclusiveGate> enter, Func<FpsY3InspectionResult> inspect)
        {
            var gate = enter();
            try { FpsY3MaintenanceSession.RequireCandidate(inspect()); return gate; }
            catch (Exception failure) {
                bool abandoned = failure is FpsY3MaintenanceBlockedException blocked && blocked.Reason == "abandoned_mutex";
                try { if (!abandoned) gate.Complete(); }
                catch (Exception release) { gate.Dispose(); throw new AggregateException("No se confirmó la liberación de la admisión Y3.", failure, release); }
                gate.Dispose(); throw;
            }
        }
        internal FpsY3ExclusiveGate EnterMaintenance() => FpsY3ExclusiveGate.Acquire(directory, "maintenance", (Action<bool>)RequireProtectedGate);
    }
}
