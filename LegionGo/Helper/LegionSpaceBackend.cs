// SPDX-License-Identifier: AGPL-3.0-only
// Only DAService is addressable. SCM APIs preserve all unrelated service configuration.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    internal sealed class LegionSpaceJournal : ILegionSpaceJournal
    {
        private readonly string path;
        internal LegionSpaceJournal(string directory) { path = Path.Combine(directory, "legion-space-original.json"); }
        public LegionSpaceOriginal Load()
        {
            if (!File.Exists(path)) return null;
            var original = Protocol.Decode<LegionSpaceOriginal>(File.ReadAllText(path));
            if (original == null) throw new InvalidDataException("Respaldo de Legion Space vacío.");
            original.Validate(); return original;
        }
        public void Save(LegionSpaceOriginal original)
        {
            original.Validate();
            // A restart or repeated action must never replace the first saved state.
            if (File.Exists(path)) throw new InvalidOperationException("Ya existe un respaldo de Legion Space; vuelve a consultar el estado.");
            string temporary = path + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Encode(original));
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            File.Move(temporary, path);
        }
        public void Clear() { File.Delete(path); }
    }
    internal sealed class LegionSpaceBackend : ILegionSpaceBackend
    {
        private const string ServiceName = "DAService";
        private const uint QueryConfig = 1, ChangeConfig = 2, QueryStatus = 4, NoChange = 0xffffffff;
        public LegionSpaceState Read()
        {
            using (var manager = OpenManager())
            using (var service = Open(manager, QueryConfig | QueryStatus)) return Read(service);
        }
        public void Configure(int startMode, bool delayedAutoStart)
        {
            new LegionSpaceState { StartMode = startMode }.Validate();
            using (var manager = OpenManager())
            using (var service = Open(manager, QueryConfig | QueryStatus | ChangeConfig))
            {
                var before = Read(service); // Validate identity again using this handle.
                // Windows only accepts delayed-start configuration for auto-start services.
                if (before.DelayedAutoStart != delayedAutoStart)
                {
                    SetMode(service, 2);
                    var delay = new DelayedStart { Enabled = delayedAutoStart ? 1 : 0 };
                    Check(ChangeServiceConfig2W(service, 3, ref delay));
                }
                SetMode(service, startMode);
            }
        }
        public void SetRunning(bool running)
        {
            using (var manager = OpenManager())
            using (var service = Open(manager, QueryConfig | QueryStatus | (running ? 0x10u : 0x20u)))
            {
                if (Read(service).Running == running) return;
                if (running) Check(StartServiceW(service, 0, IntPtr.Zero));
                else
                {
                    var stopped = new BasicStatus();
                    // ControlService refuses active dependents; never stop unrelated services.
                    Check(ControlService(service, 1, ref stopped));
                }
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 8)
                {
                    uint required; var status = new ServiceStatus();
                    Check(QueryServiceStatusEx(service, 0, ref status, (uint)Marshal.SizeOf(typeof(ServiceStatus)), out required));
                    if (status.State == (running ? 4u : 1u)) return;
                    Thread.Sleep(100);
                }
                throw new TimeoutException("Windows no terminó la transición de Legion Space en ocho segundos.");
            }
        }
        private static void SetMode(ServiceHandle service, int mode)
        { Check(ChangeServiceConfigW(service, NoChange, (uint)mode, NoChange, null, null, IntPtr.Zero, null, null, null, null)); }
        private static LegionSpaceState Read(ServiceHandle service)
        {
            uint required;
            if (QueryServiceConfigW(service, IntPtr.Zero, 0, out required)) throw new InvalidDataException("Tamaño de servicio inesperado.");
            int error = Marshal.GetLastWin32Error();
            if (error != 122 || required < Marshal.SizeOf(typeof(ServiceConfig)) || required > 8192) throw new Win32Exception(error);
            IntPtr buffer = Marshal.AllocHGlobal((int)required);
            ServiceConfig config;
            try
            {
                Check(QueryServiceConfigW(service, buffer, required, out required));
                config = (ServiceConfig)Marshal.PtrToStructure(buffer, typeof(ServiceConfig));
                ValidateBinary(Marshal.PtrToStringUni(config.BinaryPath));
            }
            finally { Marshal.FreeHGlobal(buffer); }
            var delay = new DelayedStart();
            Check(QueryServiceConfig2W(service, 3, ref delay, 4, out required));
            var status = new ServiceStatus();
            Check(QueryServiceStatusEx(service, 0, ref status, (uint)Marshal.SizeOf(typeof(ServiceStatus)), out required));
            if (status.State != 1 && status.State != 4)
                throw new InvalidOperationException("Legion Space está en transición o pausado. Vuelve a consultar su estado.");
            var result = new LegionSpaceState { StartMode = (int)config.StartType, DelayedAutoStart = delay.Enabled != 0, Running = status.State == 4 };
            result.Validate(); return result;
        }
        private static void ValidateBinary(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Ruta de DAService sin confirmar.");
            string value = command.Trim(), executable;
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"', 1);
                if (end < 0) throw new InvalidOperationException("Ruta de DAService inválida.");
                executable = value.Substring(1, end - 1);
            }
            else
            {
                // Unquoted paths containing spaces are ambiguous to Windows. Do not guess.
                if (value.IndexOf(' ') >= 0) throw new InvalidOperationException("La ruta de DAService no está delimitada correctamente.");
                executable = value;
            }
            string full = Path.GetFullPath(executable);
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "LegionSpace") + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(full), "LSDaemon.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidOperationException("DAService no corresponde a la instalación esperada de Legion Space. No se modificó.");
        }
        private static ServiceHandle OpenManager()
        { var handle = OpenSCManagerW(null, null, 1); if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); } return handle; }
        private static ServiceHandle Open(ServiceHandle manager, uint rights)
        { var handle = OpenServiceW(manager, ServiceName, rights); if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); } return handle; }
        private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        { public ServiceHandle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
        [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
        { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPath, LoadGroup; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
        [StructLayout(LayoutKind.Sequential)] private struct DelayedStart { public int Enabled; }
        [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
        { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicStatus
        { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern ServiceHandle OpenSCManagerW(string machine, string database, uint rights);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint rights);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr service);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr config, uint size, out uint required);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password, string display);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool QueryServiceConfig2W(ServiceHandle service, uint level, ref DelayedStart config, uint size, out uint required);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref DelayedStart config);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, ref ServiceStatus status, uint size, out uint required);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr arguments);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(ServiceHandle service, uint control, ref BasicStatus status);
    }
}
