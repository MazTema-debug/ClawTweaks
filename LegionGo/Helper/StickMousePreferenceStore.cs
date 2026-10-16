// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;

namespace LegionTweaks
{
    internal interface IStickMousePreferenceFile
    {
        byte[] Read(); // Null means absent, access errors are never absence.
        void WriteAtomic(byte[] bytes, bool backupInvalid);
    }

    internal sealed class StickMousePreferenceStore
    {
        internal const string FileName = "stick-mouse-preferences.json";
        private readonly IStickMousePreferenceFile file;
        internal StickMousePreferenceStore(string directory) : this(new ProtectedPreferenceFile(directory)) { }
        internal StickMousePreferenceStore(IStickMousePreferenceFile file)
        { this.file = file ?? throw new ArgumentNullException(nameof(file)); }

        internal StickMousePreferences Load(out string error)
        {
            error = null;
            try { var bytes = file.Read(); return bytes == null ? new StickMousePreferences() : StickMousePreferences.Decode(bytes); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException
                || ex is System.Security.SecurityException || ex is SerializationException || ex is ArgumentException)
            { error = "No se cargaron las preferencias del mouse; se conservó el archivo. " + ex.Message; return new StickMousePreferences(); }
        }

        internal void Save(int sensitivity, int deadzone)
        {
            // Serialize and validate before touching storage. Re-read at each
            // explicit save; never silently replace a newly damaged file.
            var bytes = new StickMousePreferences { Sensitivity = sensitivity, Deadzone = deadzone }.Encode();
            bool backupInvalid = false;
            try { var previous = file.Read(); if (previous != null) StickMousePreferences.Decode(previous); }
            catch (InvalidDataException) { backupInvalid = true; }
            file.WriteAtomic(bytes, backupInvalid);
            var saved = StickMousePreferences.Decode(file.Read());
            if (saved.Sensitivity != sensitivity || saved.Deadzone != deadzone)
                throw new IOException("No se confirmó la escritura de las preferencias del mouse.");
        }

        // This private adapter is used only by the Windows net48 helper. The
        // cross-platform test target exercises the store through its file seam.
#pragma warning disable CA1416
        private sealed class ProtectedPreferenceFile : IStickMousePreferenceFile
        {
            private readonly string directory, path;
            internal ProtectedPreferenceFile(string directory)
            {
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", WindowsIdentity.GetCurrent().User.Value);
                if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Las preferencias requieren la carpeta protegida del usuario actual.");
                this.directory = expected; path = Path.Combine(expected, FileName);
            }
            private void CheckDirectory()
            {
                if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("No existe la carpeta protegida de preferencias.");
                for (var item = new DirectoryInfo(directory); item != null; item = item.Parent)
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("La carpeta de preferencias no puede ser un enlace.");
                CheckAcl(new DirectoryInfo(directory).GetAccessControl(), true);
            }
            private static void CheckAcl(FileSystemSecurity acl, bool folder)
            {
                if (folder && !acl.AreAccessRulesProtected) throw new InvalidDataException("La carpeta de preferencias no está protegida.");
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                bool hasAdmins = false, hasSystem = false;
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow) continue;
                    if (rule.IdentityReference.Equals(admins)) hasAdmins |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                    else if (rule.IdentityReference.Equals(system)) hasSystem |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                    else if ((rule.FileSystemRights & ~(folder ? FileSystemRights.Traverse | FileSystemRights.Synchronize : 0)) != 0)
                        throw new InvalidDataException("Las preferencias tienen permisos adicionales.");
                }
                if (!hasAdmins || !hasSystem) throw new InvalidDataException("No se confirmaron los permisos protegidos de preferencias.");
            }
            private static bool CheckFile(string candidate)
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(candidate); }
                catch (FileNotFoundException) { return false; }
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    throw new InvalidDataException("El archivo de preferencias no puede ser un enlace ni una carpeta.");
                CheckAcl(new FileInfo(candidate).GetAccessControl(), false); return true;
            }
            public byte[] Read()
            {
                CheckDirectory(); if (!CheckFile(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > StickMousePreferences.MaximumBytes) throw new InvalidDataException("Preferencias del mouse demasiado grandes.");
                    var bytes = new byte[stream.Length]; int offset = 0, read;
                    while (offset < bytes.Length && (read = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                    if (offset != bytes.Length || stream.ReadByte() != -1) throw new InvalidDataException("Las preferencias cambiaron durante la lectura.");
                    return bytes;
                }
            }
            public void WriteAtomic(byte[] bytes, bool backupInvalid)
            {
                if (bytes == null || bytes.Length > StickMousePreferences.MaximumBytes) throw new InvalidDataException("Preferencias del mouse demasiado grandes.");
                CheckDirectory(); bool exists = CheckFile(path);
                string temporary = Path.Combine(directory, "stick-mouse-" + Guid.NewGuid().ToString("N") + ".tmp");
                // The protected parent grants inheritable full access only to
                // administrators/SYSTEM, so the new file is restricted from creation.
                bool created = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        created = true; CheckFile(temporary);
                        stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                    }
                    CheckDirectory(); CheckFile(temporary);
                    if (exists)
                    {
                        if (!CheckFile(path)) throw new IOException("El archivo de preferencias cambió antes de guardar.");
                        string backup = backupInvalid ? Path.Combine(directory, "stick-mouse-preferences-invalid-" + Guid.NewGuid().ToString("N") + ".json") : null;
                        File.Replace(temporary, path, backup);
                    }
                    else File.Move(temporary, path);
                }
                finally { if (created) try { File.Delete(temporary); } catch (IOException) { } }
            }
        }
#pragma warning restore CA1416
    }
}
