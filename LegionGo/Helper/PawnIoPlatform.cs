// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace LegionTweaks
{
    internal sealed class PawnIoPlatform : IPawnIoPlatform
    {
        public PawnIoStatus Inspect()
        {
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"))
            {
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
                string folder = key?.GetValue("InstallLocation") as string;
                bool registered = key != null;
                bool driverPresent = false, running = false;
                using (var search = new ManagementObjectSearcher("SELECT State FROM Win32_SystemDriver WHERE Name='PawnIO'"))
                using (var rows = ReadDrivers(search)) foreach (ManagementObject row in rows) using (row) {
                    driverPresent = true; running = string.Equals(row["State"] as string, "Running", StringComparison.OrdinalIgnoreCase);
                }
                var result = new PawnIoStatus { Installed = registered || driverPresent, DriverRunning = running,
                    Version = key?.GetValue("DisplayVersion") as string };
                if (folder == null) folder = expected;
                if (!Path.IsPathRooted(folder) || !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), expected, StringComparison.OrdinalIgnoreCase))
                    return result;
                if (!Directory.Exists(folder)) return result;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return result;
                string library = Path.Combine(folder, "PawnIOLib.dll");
                if (!File.Exists(library) || (File.GetAttributes(library) & FileAttributes.ReparsePoint) != 0) return result;
                result.Installed = true;
                string fileVersion = FileVersionInfo.GetVersionInfo(library).FileVersion;
                Version registeredVersion, libraryVersion;
                bool knownLibrary = Version.TryParse(fileVersion, out libraryVersion);
                bool knownRegistry = Version.TryParse(result.Version, out registeredVersion);
                // An inconsistent install cannot authorize a downgrade. Keep the
                // higher known version and check the library independently.
                result.Version = !knownRegistry || (knownLibrary && libraryVersion > registeredVersion) ? fileVersion : result.Version;
                result.Usable = driverPresent && running && knownLibrary && libraryVersion >= PawnIoPackage.MinimumVersion
                    && PawnIoTrust.IsTrustedPublisher(library);
                return result;
            }
        }

        private static ManagementObjectCollection ReadDrivers(ManagementObjectSearcher search)
        {
            search.Options.Timeout = TimeSpan.FromSeconds(2);
            return search.Get();
        }

        public void Install(Action<string> phase)
        {
            // A second view/helper must not open another vendor installer.
            using (var mutex = new Mutex(false, @"Global\LegionTweaks.PawnIo.Install.v1"))
            {
                bool acquired = false;
                try {
                    try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("Otro instalador ya está abierto.");
                    var current = Inspect();
                    Version version;
                    if (current.Installed && (!Version.TryParse(current.Version, out version) || version >= PawnIoPackage.MinimumVersion)) return;
                    string folder = CreatePrivateFolder();
                    string file = Path.Combine(folder, "PawnIO_setup.exe");
                    Download(file);
                    // Keep a non-writable/non-deletable handle across verification
                    // and launch so the pinned installer cannot be swapped.
                    using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var sha = SHA256.Create())
                    {
                        string digest = BitConverter.ToString(sha.ComputeHash(held)).Replace("-", "");
                        if (!PawnIoPackage.IsAcceptedInstaller(held.Length, digest, PawnIoTrust.IsTrustedPublisher(file)))
                            throw new InvalidDataException("No se confirmó el instalador oficial.");
                        phase("installer-open");
                        using (var process = Process.Start(new ProcessStartInfo {
                            FileName = file, Arguments = "", WorkingDirectory = folder, UseShellExecute = true, Verb = "runas"
                        }))
                        {
                            if (process == null) throw new InvalidOperationException("No se confirmó la apertura del instalador.");
                            // Worker only: IPC and the helper's control workers stay responsive.
                            // No silent switches and no forced termination of the vendor UI.
                            process.WaitForExit();
                        }
                    }
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }
        private static string CreatePrivateFolder()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "LegionTweaks-PawnIO-" + Guid.NewGuid().ToString("N"));
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(folder, acl);
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Carpeta no válida.");
            return folder;
        }
        private static void Download(string file)
        {
            var request = (HttpWebRequest)WebRequest.Create(PawnIoPackage.InstallerUrl);
            request.Timeout = request.ReadWriteTimeout = 30000;
            request.MaximumAutomaticRedirections = 5;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                string host = response.ResponseUri.Host;
                if (response.StatusCode != HttpStatusCode.OK || response.ResponseUri.Scheme != "https"
                    || (host != "github.com" && host != "release-assets.githubusercontent.com" && host != "objects.githubusercontent.com")
                    || (response.ContentLength >= 0 && response.ContentLength != PawnIoPackage.InstallerBytes))
                    throw new InvalidDataException("Descarga no válida.");
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[32768]; int count; long total = 0;
                    var elapsed = Stopwatch.StartNew();
                    while ((count = input.Read(buffer, 0, buffer.Length)) != 0) {
                        total += count;
                        if (total > PawnIoPackage.InstallerBytes || elapsed.Elapsed > TimeSpan.FromSeconds(60))
                            throw new InvalidDataException("Descarga incompleta o fuera de límite.");
                        output.Write(buffer, 0, count);
                    }
                    if (total != PawnIoPackage.InstallerBytes) throw new InvalidDataException("Descarga incompleta.");
                }
            }
        }
    }
}
