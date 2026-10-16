// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.Text;

namespace LegionTweaks
{
    internal sealed class DiagnosticsProvider
    {
        private readonly string directory;
        private readonly string sid;
        private readonly IControllerService controller;
        internal DiagnosticsProvider(string directory, IControllerService controller)
        {
            sid = WindowsIdentity.GetCurrent().User.Value;
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", sid);
            if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("El diagnóstico solo admite la carpeta protegida del usuario actual.");
            this.directory = expected; this.controller = controller;
        }

        // No power/ADL/WMI/SDL/ViGEm/XInput reads or setters. GetStatus is the
        // controller's existing cached snapshot. Files are fixed, never from IPC.
        internal DiagnosticReport GetReport()
        {
            ControllerStatus cached = null;
            try { cached = controller?.GetStatus(); } catch { }
            string pnpState, hideState, resultState;
            var pnp = Read<PhysicalRecoveryLease>("controller-device-recovery.json", out pnpState);
            if (pnp != null) try { PhysicalRecoveryRules.Validate(pnp, sid); } catch { pnpState = "invalid"; pnp = null; }
            var hide = Read<HideLease>("controller-lease.json", out hideState);
            if (hide != null && (!PhysicalRecoveryRules.IsEndpoint(hide.Device) || string.IsNullOrEmpty(hide.Application) || hide.OwnerPid <= 0 || hide.OwnerStarted <= 0)) hideState = "invalid";
            var outcome = Read<PhysicalRecoveryOutcome>("controller-device-last-result.json", out resultState);
            if (outcome != null) try { outcome = Diagnostics.ParseOutcome(Protocol.Encode(outcome), sid); }
                catch { outcome = null; resultState = "invalid"; }
            var report = new DiagnosticReport { CapturedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                HelperVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(), Controller = Diagnostics.Controller(cached),
                Recovery = Diagnostics.Recovery(outcome, resultState, pnpState, pnp?.Phase, hideState),
                HidHide = new DiagnosticHidHide { State = cached?.PreparingPlayerOne == true ? "deferred" : "unreadable" } };
            if (cached?.PreparingPlayerOne != true)
            {
                try
                {
                    using (var configuration = new HidHideConfiguration())
                    {
                        // GET only; never invoke the self-registering HidHide CLI.
                        report.HidHide.Active = configuration.Active;
                        report.HidHide.Inverted = configuration.Inverted;
                        report.HidHide.ApplicationCount = configuration.Applications.Length;
                        report.HidHide.HiddenDeviceCount = configuration.Devices.Length;
                        report.HidHide.State = "present";
                    }
                }
                catch { report.HidHide = new DiagnosticHidHide { State = "unreadable" }; }
            }
            return Diagnostics.SafeCopy(report, null);
        }

        private T Read<T>(string fileName, out string state) where T : class
        {
            state = "unreadable";
            try
            {
                for (var item = new DirectoryInfo(directory); item != null && item.Exists; item = item.Parent)
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) { state = "invalid"; return null; }
                string path = Path.Combine(directory, fileName);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16384) { state = "invalid"; return null; }
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file, Encoding.UTF8))
                {
                    // Bound reading even when another process replaces/grows the file.
                    var buffer = new char[16385]; int count = 0, next;
                    while (count < buffer.Length && (next = reader.Read(buffer, count, buffer.Length - count)) != 0) count += next;
                    if (count == buffer.Length) { state = "invalid"; return null; }
                    T value = Protocol.Decode<T>(new string(buffer, 0, count));
                    state = value == null ? "invalid" : "present"; return value;
                }
            }
            catch (FileNotFoundException) { state = "absent"; }
            catch (DirectoryNotFoundException) { state = "absent"; }
            catch (SerializationException) { state = "invalid"; }
            catch (InvalidDataException) { state = "invalid"; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            return null;
        }
    }
}
