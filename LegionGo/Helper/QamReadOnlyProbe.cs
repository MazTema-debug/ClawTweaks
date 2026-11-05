// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    // Temporary elevated reader. Entered before normal service/controller/recovery startup.
    internal static class QamReadOnlyProbe
    {
        internal static int Run(IHardware hardware, SecurityIdentifier user, int ownerPid, long ownerStarted, string pipeName = null)
        {
            if (!hardware.Identity.IsSupported) throw new InvalidOperationException("La prueba requiere Legion Go original 83E1 / Z1 Extreme.");
            if (ownerPid <= 0 || ownerStarted <= 0) throw new ArgumentException("Propietario QAM inválido.");
            using (var owner = Process.GetProcessById(ownerPid)) {
                if (!string.Equals(owner.ProcessName, "LegionTweaks.QamProbe", StringComparison.OrdinalIgnoreCase)
                    || owner.StartTime.ToUniversalTime().Ticks != ownerStarted || !IsOwner(ownerPid, user))
                    throw new InvalidOperationException("El propietario no es el QAM de este usuario.");
                using (var instance = new Mutex(true, @"Local\LegionTweaks.QamReadOnly." + user.Value, out bool first)) {
                    if (!first) throw new InvalidOperationException("Ya hay un lector QAM temporal iniciado.");
                    var engine = new ControlEngine(hardware, new NoWriteJournal());
                    using (var server = new ReadOnlyPipeServer(user, new ReadOnlyStatusService(true,
                        () => engine.Execute(new Request { Operation = "telemetry" }),
                        () => new NativeDisplayBackend().Query().Info,
                        () => new LosslessScalingDetector(true).Inspect()), pipeName)) {
                        while (!owner.WaitForExit(1000)) { }
                    }
                }
            }
            return 0;
        }
        private static bool IsOwner(int pid, SecurityIdentifier expected)
        {
            using (var process = OpenProcess(0x1000, false, (uint)pid)) {
                if (process.IsInvalid || !OpenProcessToken(process, 8, out IntPtr token)) return false;
                try { using (var identity = new WindowsIdentity(token)) return expected.Equals(identity.User); }
                finally { CloseHandle(token); }
            }
        }
        private sealed class NoWriteJournal : IJournal
        {
            public IList<OriginalValue> Load() => new List<OriginalValue>();
            public void Save(IList<OriginalValue> originals) => throw new InvalidOperationException("Prueba QAM de solo lectura.");
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
