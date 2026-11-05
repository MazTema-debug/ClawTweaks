// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionTweaks
{
    // Same protected per-user directory as other helper preferences. Read never
    // creates files, repairs damaged data or activates a route. Mutations only
    // occur after an explicit save/delete request through the authenticated IPC.
#pragma warning disable CA1416
    internal sealed class RemapProfileStorage : IRemapProfileStorage
    {
        internal const string FileName = "controller-remap-profiles.json";
        private readonly string directory, path;
        internal RemapProfileStorage(string directory)
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", WindowsIdentity.GetCurrent().User.Value);
            if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Las distribuciones requieren la carpeta protegida del usuario actual.");
            this.directory = expected; path = Path.Combine(expected, FileName);
        }
        private void CheckDirectory()
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("No existe la carpeta protegida de distribuciones.");
            for (var item = new DirectoryInfo(directory); item != null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("La carpeta no puede ser un enlace.");
            CheckAcl(new DirectoryInfo(directory).GetAccessControl(), true);
        }
        // Reuse the directory/file ACL gate for the separate native recovery journal.
        // It never reads or decodes the profile catalog.
        internal void RequireProtectedLeaf(string leaf)
        {
            if (string.IsNullOrEmpty(leaf) || Path.GetFileName(leaf) != leaf || leaf.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new ArgumentException("Nombre protegido inválido.");
            CheckDirectory(); CheckFile(Path.Combine(directory, leaf));
        }
        private static void CheckAcl(FileSystemSecurity acl, bool folder)
        {
            if (folder && !acl.AreAccessRulesProtected) throw new InvalidDataException("La carpeta no está protegida.");
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            bool hasAdmins = false, hasSystem = false;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference.Equals(admins)) hasAdmins |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                else if (rule.IdentityReference.Equals(system)) hasSystem |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                else if ((rule.FileSystemRights & ~(folder ? FileSystemRights.Traverse | FileSystemRights.Synchronize : 0)) != 0)
                    throw new InvalidDataException("El archivo tiene permisos adicionales.");
            }
            if (!hasAdmins || !hasSystem) throw new InvalidDataException("No se confirmaron los permisos de distribuciones.");
        }
        private static bool CheckFile(string candidate)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(candidate); }
            catch (FileNotFoundException) { return false; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("El archivo no puede ser un enlace ni una carpeta.");
            CheckAcl(new FileInfo(candidate).GetAccessControl(), false); return true;
        }
        public byte[] Read()
        {
            CheckDirectory(); if (!CheckFile(path)) return null;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length <= 0 || stream.Length > RemapProfiles.MaximumBytes) throw new InvalidDataException("Catálogo vacío o demasiado grande.");
                var bytes = new byte[stream.Length]; int offset = 0, read;
                while (offset < bytes.Length && (read = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                if (offset != bytes.Length || stream.ReadByte() != -1) throw new IOException("El catálogo cambió durante la lectura.");
                return bytes;
            }
        }
        public void WriteAtomic(byte[] bytes, byte[] expected)
        {
            RemapProfiles.Decode(bytes); // Validate before opening any write handle.
            CheckDirectory(); string lockPath = Path.Combine(directory, "controller-remap-profiles.lock");
            CheckFile(lockPath);
            using (var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                CheckFile(lockPath); CheckDirectory();
                var current = Read();
                if (!(current == null && expected == null)
                    && (current == null || expected == null || !current.SequenceEqual(expected)))
                    throw new InvalidOperationException("Otro editor cambió el catálogo antes de guardar.");
                string temporary = Path.Combine(directory, "controller-remap-" + Guid.NewGuid().ToString("N") + ".tmp");
                bool created = false;
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true; CheckFile(temporary); output.Write(bytes, 0, bytes.Length); output.Flush(true);
                    }
                    CheckDirectory(); CheckFile(temporary);
                    if (current != null)
                    {
                        if (!CheckFile(path)) throw new IOException("El catálogo desapareció antes de guardar.");
                        // Keep one previous valid catalog, under the protected ACL.
                        string backup = path + ".previous"; CheckFile(backup);
                        File.Replace(temporary, path, backup);
                    }
                    else File.Move(temporary, path);
                    var actual = Read();
                    if (actual == null || !actual.SequenceEqual(bytes)) throw new IOException("No se confirmó el catálogo escrito.");
                }
                finally { if (created) try { File.Delete(temporary); } catch (IOException) { } }
            }
        }
    }
#pragma warning restore CA1416
}
