// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace LegionTweaks
{
    // Best-effort observation of this process only. No native input/device API is
    // invoked. File work is queued so recovery never waits for diagnostic disk I/O.
    internal sealed class ControllerPreparationTrace : IDisposable
    {
        internal const string FileName = "controller-preparation-last-trace.json";
        internal const string FailureFileName = "controller-preparation-last-error.json";
        private static readonly ControllerPreparationTrace disabled = new ControllerPreparationTrace();
        private static readonly ControllerPreparationTraceGeneration generations = new ControllerPreparationTraceGeneration();
        private static readonly object backgroundWriterGate = new object();
        private readonly object gate = new object();
        private readonly ControllerPreparationTraceBuffer buffer;
        private readonly Stopwatch clock;
        private readonly string directory;
        private readonly long generation;
        private bool disposed, writerQueued, capacityReported;
        private int revision;
        private ControllerPreparationTrace() { }
        private ControllerPreparationTrace(string directory, long generation)
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", sid);
            if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La traza requiere la carpeta protegida del usuario actual.");
            this.directory = expected; this.generation = generation;
            CheckDirectory();
            using (var self = Process.GetCurrentProcess()) buffer = new ControllerPreparationTraceBuffer(Guid.NewGuid().ToString("N"), self.Id, DateTime.UtcNow.Ticks);
            clock = Stopwatch.StartNew();
        }
        internal static ControllerPreparationTrace Start(string directory)
        {
            try { return new ControllerPreparationTrace(directory, generations.Begin()); }
            catch { return disabled; }
        }
        internal void Record(ControllerPreparationStage stage, bool? openRequested = null, bool? opened = null)
        {
            if (buffer == null || !generations.IsCurrent(generation) || !ControllerPreparationTraceRules.IsStage(stage)) return;
            try
            {
                lock (gate)
                {
                    if (disposed) return;
                    // No further module inspections after the event budget is exhausted.
                    if (!buffer.HasCapacity) {
                        if (!capacityReported) { capacityReported = true; buffer.Record(stage, DateTime.UtcNow.Ticks, clock.ElapsedMilliseconds, null); QueueWrite(); }
                        return;
                    }
                    var modules = Modules();
                    if (buffer.Record(stage, DateTime.UtcNow.Ticks, clock.ElapsedMilliseconds, modules, openRequested, opened)) QueueWrite();
                }
            }
            catch { } // Optional evidence must never change P1/recovery control flow.
        }
        internal void RecordFailure(Exception error)
        {
            if (buffer == null || error == null) return;
            try {
                var snapshot=buffer.Snapshot();
                byte[] bytes=System.Text.Encoding.UTF8.GetBytes(Protocol.Encode(ControllerPreparationFailure.Capture(error,snapshot.RunId,snapshot.ProcessId)));
                ThreadPool.QueueUserWorkItem(_=>{ try { lock(backgroundWriterGate) {
                    if(generations.IsCurrent(generation)) WriteAtomic(bytes,FailureFileName);
                } } catch { } });
            } catch { }
        }
        private static ControllerPreparationModuleFlags Modules()
        {
            try
            {
                using (var self = Process.GetCurrentProcess())
                {
                    var list = self.Modules;
                    if (list.Count > ControllerPreparationTraceRules.MaximumModules) return new ControllerPreparationModuleFlags();
                    var names = new List<string>(list.Count);
                    foreach (ProcessModule module in list) names.Add(module.ModuleName);
                    return ControllerPreparationTraceRules.Modules(names);
                }
            }
            catch { return new ControllerPreparationModuleFlags(); }
        }
        // Caller holds gate; at most one writer exists and new events coalesce.
        private void QueueWrite()
        {
            revision++;
            if (writerQueued) return;
            writerQueued = true;
            try { if (!ThreadPool.QueueUserWorkItem(_ => WritePending())) writerQueued = false; }
            catch { writerQueued = false; }
        }
        private void WritePending()
        {
            try
            {
                while (true)
                {
                    int writing; lock (gate) writing = revision;
                    buffer.TryWrite(bytes => { lock (backgroundWriterGate) {
                        if (generations.IsCurrent(generation)) WriteAtomic(bytes);
                    } });
                    lock (gate) { if (revision == writing) { writerQueued = false; return; } }
                }
            }
            catch { lock (gate) writerQueued = false; }
        }
        private void CheckDirectory()
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
            for (var item = new DirectoryInfo(directory); item != null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("La carpeta de traza no puede ser un enlace.");
            // Match Program.SecureDirectory: protected admins/SYSTEM full access;
            // other principals may only traverse, never list/read/create files.
            CheckAcl(new DirectoryInfo(directory).GetAccessControl(), true);
        }
        private static void CheckAcl(FileSystemSecurity acl, bool allowTraverse)
        {
            if (!acl.AreAccessRulesProtected) throw new InvalidDataException("La carpeta de traza no está protegida.");
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            bool adminsAllowed = false, systemAllowed = false;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference.Equals(administrators)) adminsAllowed |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                else if (rule.IdentityReference.Equals(system)) systemAllowed |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                else if ((rule.FileSystemRights & ~(allowTraverse ? FileSystemRights.Traverse | FileSystemRights.Synchronize : 0)) != 0)
                    throw new InvalidDataException("La carpeta de traza tiene permisos adicionales.");
            }
            if (!adminsAllowed || !systemAllowed) throw new InvalidDataException("La carpeta de traza no confirmó sus permisos protegidos.");
        }
        private static FileSecurity FileAcl()
        {
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            return acl;
        }
        private void WriteAtomic(byte[] bytes, string name = FileName)
        {
            if (bytes == null || bytes.Length > ControllerPreparationTraceRules.MaximumBytes) return;
            CheckDirectory();
            string destination = Path.Combine(directory, name);
            bool exists;
            try { if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) return;
                CheckAcl(new FileInfo(destination).GetAccessControl(), false); exists = true; }
            catch (FileNotFoundException) { exists = false; }
            // Unique create-new file with its final restricted ACL before any data.
            string temporary = Path.Combine(directory, "controller-preparation-trace-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool created = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, FileAcl()))
                {
                    created = true;
                    if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) return;
                    file.Write(bytes, 0, bytes.Length); file.Flush(); // Diagnostic only; no forced disk flush on the P1 path.
                }
                // A newer operation can begin while this background writer performs
                // disk I/O. Never publish the old operation over its newer evidence.
                if (!generations.IsCurrent(generation)) return;
                CheckDirectory();
                if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0) return;
                if (exists) {
                    if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) return;
                    CheckAcl(new FileInfo(destination).GetAccessControl(), false);
                }
                if (exists) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            }
            finally
            {
                // Never remove the last good destination after a failed snapshot.
                if (created) try { File.Delete(temporary); } catch { }
            }
        }
        public void Dispose()
        {
            if (buffer == null) return;
            try { lock (gate) { if (disposed) return; disposed = true; QueueWrite(); } }
            catch { }
            // Deliberately do not join the writer or delay recovery/shutdown.
        }
    }
}
