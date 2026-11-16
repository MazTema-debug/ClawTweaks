// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    public sealed class FpsY3MaintenanceBlockedException : InvalidOperationException
    {
        public string Reason { get; private set; }
        internal FpsY3MaintenanceBlockedException(string reason) : base("La inspección Y3 no permite continuar la preparación.")
        { Reason = reason; }
    }

    internal interface IFpsY3MaintenanceEnvironment
    {
        FpsY3ExclusiveGate EnterMaintenance();
        FpsY3InspectionResult Inspect();
        string ProtectionHash();
    }

    internal sealed class FpsY3MaintenanceEnvironment : IFpsY3MaintenanceEnvironment
    {
        private const string Journal = "fps-y3-trial.json";
        private readonly string sid, directory;
        private readonly FpsY3Admission admission;
        internal FpsY3MaintenanceEnvironment(string sid, string directory)
        {
            this.sid = sid; this.directory = directory;
            admission = new FpsY3Admission(directory);
        }
        private void RequireIdentity()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (identity.User == null || identity.User.Value != sid
                    || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new FpsY3MaintenanceBlockedException("identity_changed");
        }
        public FpsY3ExclusiveGate EnterMaintenance() { RequireIdentity(); return admission.EnterMaintenance(); }
        public FpsY3InspectionResult Inspect() { RequireIdentity(); return FpsY3JournalInspector.InspectCurrentUser(directory); }
        public string ProtectionHash()
        {
            RequireIdentity();
            return CaptureProtection(directory, () => {
                admission.RequireNoAbandonment();
                admission.RequireProtectedLeaf(FpsY3ExclusiveGate.Leaf, true);
                admission.RequireProtectedLeaf(Journal, false);
            });
        }

        // The real caller supplies the existing ACL/reparse gate above. Tests
        // can exercise the ACL-byte comparison on a disposable directory.
        internal static string CaptureProtection(string directory, Action requireProtected)
        {
            requireProtected();
            var sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
            string directoryIdentity = DirectoryIdentity(directory);
            byte[] dirAcl = new DirectoryInfo(directory).GetAccessControl(sections).GetSecurityDescriptorBinaryForm();
            var ancestors = new List<byte[]>();
            for (var parent = new DirectoryInfo(directory).Parent; parent != null; parent = parent.Parent)
                ancestors.Add(parent.GetAccessControl(sections).GetSecurityDescriptorBinaryForm());
            byte[] gateAcl = new FileInfo(Path.Combine(directory, FpsY3ExclusiveGate.Leaf)).GetAccessControl(sections).GetSecurityDescriptorBinaryForm();
            byte[] journalAcl = null; string journal = Path.Combine(directory, Journal);
            try { File.GetAttributes(journal); journalAcl = new FileInfo(journal).GetAccessControl(sections).GetSecurityDescriptorBinaryForm(); }
            catch (FileNotFoundException) { }
            requireProtected();
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            using (var sha = SHA256.Create()) {
                writer.Write(directoryIdentity); WriteAcl(writer, dirAcl);
                writer.Write(ancestors.Count); foreach (var acl in ancestors) WriteAcl(writer, acl);
                WriteAcl(writer, gateAcl); WriteAcl(writer, journalAcl); writer.Flush();
                return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "");
            }
        }
        private static void WriteAcl(BinaryWriter writer, byte[] acl)
        { if (acl == null) writer.Write(-1); else { writer.Write(acl.Length); writer.Write(acl); } }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInfo
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInfo info);
        private static string DirectoryIdentity(string directory)
        {
            const uint readAttributes = 0x80, shareAll = 7, openExisting = 3;
            const uint backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
            using (var handle = OpenDirectory(directory, readAttributes, shareAll, IntPtr.Zero, openExisting, backupSemantics | openReparsePoint, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                NativeFileInfo info;
                if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if ((info.Attributes & (uint)FileAttributes.Directory) == 0 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                    || info.IndexHigh == 0 && info.IndexLow == 0) throw new InvalidDataException("Identidad de carpeta Y3 inválida.");
                return info.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture)
                    + info.IndexHigh.ToString("X8", CultureInfo.InvariantCulture)
                    + info.IndexLow.ToString("X8", CultureInfo.InvariantCulture);
            }
        }
    }

    // Per-identity reservation for a verified current Windows identity. It
    // blocks cooperative new Y3 begins while retaining recovery's journal/I/O
    // path. It is not proof of global SID coverage, legacy quiescence or a safe
    // installation; AuthorizesMaintenance is always false.
    public sealed class FpsY3MaintenanceSession : IDisposable
    {
        private sealed class Capture { internal FpsY3InspectionResult Inspection; internal string Protection; }
        private readonly object sync = new object();
        private readonly IFpsY3MaintenanceEnvironment environment;
        private readonly Capture original;
        private FpsY3ExclusiveGate gate;
        private bool invalidated, abandoned;

        private FpsY3MaintenanceSession(IFpsY3MaintenanceEnvironment environment, FpsY3ExclusiveGate gate, Capture original)
        { this.environment = environment; this.gate = gate; this.original = original; }

        public string Classification => original.Inspection.Classification;
        public string ContentSha256 => original.Inspection.ContentSha256;
        public string FileIdentitySha256 => original.Inspection.FileIdentitySha256;
        public string ProtectionSha256 => original.Protection;
        public bool AuthorizesMaintenance => false;

        public static FpsY3MaintenanceSession BeginCurrentIdentity(string expectedSid)
        {
            using (var identity = WindowsIdentity.GetCurrent()) {
                if (identity.User == null || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new FpsY3MaintenanceBlockedException("not_elevated");
                if (string.IsNullOrEmpty(expectedSid) || identity.User.Value != expectedSid)
                    throw new FpsY3MaintenanceBlockedException("identity_mismatch");
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", identity.User.Value);
                try { return Begin(new FpsY3MaintenanceEnvironment(identity.User.Value, directory)); }
                catch (FpsY3MaintenanceBlockedException) { throw; }
                catch { throw new FpsY3MaintenanceBlockedException("unavailable"); }
            }
        }

        internal static FpsY3MaintenanceSession Begin(IFpsY3MaintenanceEnvironment environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            var gate = environment.EnterMaintenance();
            try { return new FpsY3MaintenanceSession(environment, gate, StableCapture(environment)); }
            catch (Exception failure) {
                bool abandoned = failure is FpsY3MaintenanceBlockedException blocked && blocked.Reason == "abandoned_mutex";
                try { if (!abandoned) gate.Complete(); }
                catch (Exception release) { gate.Dispose(); throw new AggregateException("No se confirmó la liberación de la reserva Y3.", failure, release); }
                gate.Dispose(); throw;
            }
        }

        // A true value only means the validated snapshot and ACL fingerprints
        // remain the same under this per-SID reservation. It is never permission.
        public bool Revalidate()
        {
            lock (sync) {
                if (gate == null || invalidated) throw new FpsY3MaintenanceBlockedException("session_invalidated");
                try {
                    var current = StableCapture(environment);
                    if (!FpsY3MaintenanceSnapshot.SameCandidate(original.Inspection, current.Inspection)
                        || original.Protection != current.Protection) {
                        invalidated = true; return false;
                    }
                    return true;
                }
                catch (FpsY3MaintenanceBlockedException ex) {
                    invalidated = true; abandoned = ex.Reason == "abandoned_mutex"; throw;
                }
                catch { invalidated = true; throw new FpsY3MaintenanceBlockedException("protection_uncertain"); }
            }
        }

        // Explicit release belongs to the transaction coordinator: it must first
        // ensure its own changes are in a safe state. An abandoned journal mutex
        // deliberately leaves the marker so a retry cannot erase that evidence.
        public void Release()
        {
            lock (sync) {
                if (gate == null) return;
                if (abandoned) throw new FpsY3MaintenanceBlockedException("abandoned_mutex");
                try { gate.Complete(); gate.Dispose(); gate = null; }
                catch (FpsY3MaintenanceBlockedException ex) when (ex.Reason == "abandoned_mutex") {
                    invalidated = abandoned = true; throw;
                }
                catch { invalidated = true; throw new FpsY3MaintenanceBlockedException("release_uncertain"); }
            }
        }
        public void Dispose() { lock (sync) { var prior = gate; gate = null; if (prior != null) prior.Dispose(); } }

        private static Capture StableCapture(IFpsY3MaintenanceEnvironment environment)
        {
            var first = environment.Inspect(); RequireCandidate(first);
            string acl = environment.ProtectionHash();
            var second = environment.Inspect(); RequireCandidate(second);
            string aclAgain = environment.ProtectionHash();
            if (!FpsY3MaintenanceSnapshot.SameCandidate(first, second) || !FpsY3MaintenanceSnapshot.Sha256(acl) || acl != aclAgain)
                throw new FpsY3MaintenanceBlockedException("snapshot_changed");
            return new Capture { Inspection = second, Protection = aclAgain };
        }
        internal static void RequireCandidate(FpsY3InspectionResult result)
        {
            if (FpsY3MaintenanceSnapshot.SameCandidate(result, result)) return;
            string reason = result == null ? null : result.Classification == "blocked" ? result.Reason : result.Classification;
            switch (reason) {
                case "abandoned_mutex": case "busy": case "pending": case "restored_without_original":
                case "recorded_error": case "invalid_data": case "unavailable": case "changed":
                    throw new FpsY3MaintenanceBlockedException(reason);
                default: throw new FpsY3MaintenanceBlockedException("inspection_uncertain");
            }
        }
    }
}
