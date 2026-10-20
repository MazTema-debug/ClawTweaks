// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class ControllerStartupStore : IControllerStartupStorage
    {
        internal const string PreferencesFile = "controller-startup-preferences.json";
        internal const string AttemptFile = "controller-startup-attempt.json";
        internal const string SuppressionFile = "controller-startup-suppress-once.json";
        private readonly string directory, sid;
        internal ControllerStartupStore(string directory)
        {
            sid = WindowsIdentity.GetCurrent().User.Value;
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", sid);
            if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El inicio requiere la carpeta protegida del usuario actual.");
            this.directory = expected;
        }
        public ControllerStartupPreferences LoadPreferences() => Locked(() => {
            var value = Read<ControllerStartupPreferences>(PreferencesFile) ?? new ControllerStartupPreferences();
            value.Validate(); return value;
        });
        public void SavePreferences(ControllerStartupPreferences preferences) => Locked(() => {
            preferences.Validate();
            bool invalid = false;
            try { Read<ControllerStartupPreferences>(PreferencesFile)?.Validate(); }
            catch (SerializationException) { invalid = true; }
            catch (InvalidDataException) { invalid = true; }
            catch (InvalidOperationException) { invalid = true; }
            Write(PreferencesFile, preferences, invalid);
            var saved = Read<ControllerStartupPreferences>(PreferencesFile); saved.Validate();
            if (saved.Enabled != preferences.Enabled) throw new IOException("No se confirmó el inicio guardado.");
            return true;
        });
        public ControllerStartupAttempt LoadAttempt() => Locked(() => {
            var value = Read<ControllerStartupAttempt>(AttemptFile); value?.Validate(); return value;
        });
        public void SaveAttempt(ControllerStartupAttempt attempt) => Locked(() => {
            attempt.Validate();
            var current = Read<ControllerStartupAttempt>(AttemptFile); current?.Validate();
            if (attempt.State != "starting" && (current == null || current.Id != attempt.Id || current.StartedUtc != attempt.StartedUtc))
                throw new InvalidDataException("Cambió el propietario del registro de inicio.");
            if (attempt.State == "starting" && current != null && (current.State == "starting" || current.State == "failed"))
                throw new InvalidDataException("El inicio anterior debe rearmarse explícitamente antes de otro intento.");
            Write(AttemptFile, attempt);
            var saved = Read<ControllerStartupAttempt>(AttemptFile); saved.Validate();
            if (saved.Id != attempt.Id || saved.StartedUtc != attempt.StartedUtc || saved.State != attempt.State)
                throw new IOException("No se confirmó el registro del inicio.");
            return true;
        });
        public void ClearAttempt() => Locked(() => {
            if (!Exists(AttemptFile)) return true;
            bool invalid = false;
            try { Read<ControllerStartupAttempt>(AttemptFile)?.Validate(); }
            catch (SerializationException) { invalid = true; }
            catch (InvalidOperationException) { invalid = true; }
            catch (InvalidDataException) { invalid = true; }
            string path = Path.Combine(directory, AttemptFile);
            if (invalid) File.Move(path, path + ".invalid-" + Guid.NewGuid().ToString("N"));
            else File.Delete(path);
            return true;
        });
        public bool ConsumeInstallationSuppression() => Locked(() => {
            var value = Read<Suppression>(SuppressionFile);
            if (value == null) return false;
            if (value.Version != 1 || !value.Skip) throw new InvalidDataException("No se confirmó la omisión del inicio durante la actualización.");
            File.Delete(Path.Combine(directory, SuppressionFile)); return true;
        });
        internal void SuppressNextStartup() => Locked(() => { Write(SuppressionFile, new Suppression()); return true; });

        [DataContract]
        private sealed class Suppression
        {
            [DataMember(IsRequired = true)] public int Version = 1;
            [DataMember(IsRequired = true)] public bool Skip = true;
        }
        private bool Exists(string name)
        {
            string path = Path.Combine(directory, name); FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("El archivo de inicio no puede ser un enlace ni una carpeta.");
            CheckAcl(new FileInfo(path).GetAccessControl()); return true;
        }
        private T Read<T>(string name) where T : class
        {
            if (!Exists(name)) return null;
            using (var file = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (file.Length > 16384) throw new InvalidDataException("Archivo de inicio demasiado grande.");
                using (var reader = new StreamReader(file, Encoding.UTF8)) {
                    var chars = new char[16385]; int count = reader.ReadBlock(chars, 0, chars.Length);
                    if (count >= chars.Length) throw new InvalidDataException("El archivo de inicio excedió su límite.");
                    return Protocol.Decode<T>(new string(chars, 0, count));
                }
            }
        }
        private void Write<T>(string name, T value, bool keepInvalid = false)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Encode(value));
            if (bytes.Length > 16384) throw new InvalidDataException("Estado de inicio demasiado grande.");
            string target = Path.Combine(directory, name), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool exists = Exists(name);
            try {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (exists) File.Replace(temporary, target, keepInvalid ? target + ".invalid-" + Guid.NewGuid().ToString("N") : null);
                else File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private T Locked<T>(Func<T> action)
        {
            using (var gate = new Mutex(false, @"Global\LegionTweaks.ControllerStartupStore." + sid)) {
                bool owns = false;
                try {
                    try { owns = gate.WaitOne(5000); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new TimeoutException("Las preferencias de inicio están ocupadas.");
                    for (var item = new DirectoryInfo(directory); item != null; item = item.Parent)
                        if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("La carpeta de inicio no está disponible o es un enlace.");
                    var acl = new DirectoryInfo(directory).GetAccessControl();
                    if (!acl.AreAccessRulesProtected) throw new InvalidDataException("La carpeta de inicio no está protegida.");
                    CheckAcl(acl); return action();
                }
                finally { if (owns) gate.ReleaseMutex(); }
            }
        }
        private static void CheckAcl(FileSystemSecurity acl)
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            bool hasAdmins = false, hasSystem = false;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))) {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference.Equals(admins)) hasAdmins |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                else if (rule.IdentityReference.Equals(system)) hasSystem |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
                else if ((rule.FileSystemRights & ~(FileSystemRights.Traverse | FileSystemRights.Synchronize)) != 0)
                    throw new InvalidDataException("El inicio tiene permisos adicionales.");
            }
            if (!hasAdmins || !hasSystem) throw new InvalidDataException("No se confirmaron los permisos del inicio.");
        }
    }
}
