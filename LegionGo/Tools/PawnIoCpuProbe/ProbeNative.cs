// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace LegionTweaks.CpuProbe
{
    internal static class CpuProbeNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Affinity { internal UIntPtr Mask; internal ushort Group, Reserved0, Reserved1, Reserved2; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorNumber { internal ushort Group; internal byte Number, Reserved; }
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetThreadGroupAffinity(IntPtr thread, ref Affinity affinity, out Affinity previous);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out Affinity affinity);
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber processor);

        internal static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        internal static CpuProbeDevice Device()
        {
            var result = new CpuProbeDevice();
            using (var search = new ManagementObjectSearcher("SELECT Vendor,Name,Version FROM Win32_ComputerSystemProduct")) {
                search.Options.Timeout = TimeSpan.FromSeconds(2);
                using (var rows = search.Get()) {
                    if (rows.Count != 1) throw new IOException("Identidad del equipo ambigua.");
                    foreach (ManagementObject row in rows) using (row) {
                        result.Manufacturer = row["Vendor"] as string; result.Model = row["Name"] as string; result.Version = row["Version"] as string;
                    }
                }
            }
            using (var search = new ManagementObjectSearcher("SELECT Name,Manufacturer,ProcessorId,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor")) {
                search.Options.Timeout = TimeSpan.FromSeconds(2);
                using (var rows = search.Get()) {
                    if (rows.Count != 1) throw new IOException("Se requiere una sola CPU identificada.");
                    foreach (ManagementObject row in rows) using (row) {
                        result.CpuName = (row["Name"] as string)?.Trim(); result.CpuVendor = row["Manufacturer"] as string;
                        result.PhysicalCores = Convert.ToInt32(row["NumberOfCores"]); result.LogicalProcessors = Convert.ToInt32(row["NumberOfLogicalProcessors"]);
                        string id = row["ProcessorId"] as string;
                        if (id == null || id.Length != 16) throw new IOException("Firma CPUID de WMI no reconocida.");
                        result.Signature = Convert.ToUInt32(id.Substring(8), 16);
                    }
                }
            }
            return result;
        }
        internal static CpuTarget[] Topology()
        {
            uint length = 0;
            if (GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length) || Marshal.GetLastWin32Error() != 122 || length == 0 || length > 65536)
                throw new IOException("No se pudo dimensionar la topología de Windows.");
            IntPtr memory = Marshal.AllocHGlobal((int)length);
            try {
                uint count = length;
                if (!GetLogicalProcessorInformationEx(0, memory, ref count) || count > length) throw new Win32Exception(Marshal.GetLastWin32Error());
                var bytes = new byte[count]; Marshal.Copy(memory, bytes, 0, bytes.Length);
                var targets = CpuProbeTopology.Parse(bytes);
                ulong allowed = unchecked((ulong)Process.GetCurrentProcess().ProcessorAffinity.ToInt64());
                foreach (var target in targets) if ((allowed & (1UL << target.Logical)) == 0)
                    throw new IOException("La afinidad actual del proceso excluye un hilo requerido.");
                return targets;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        internal static string CurrentAffinity()
        {
            Affinity current;
            if (!GetThreadGroupAffinity(GetCurrentThread(), out current)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return current.Group + ":" + current.Mask.ToUInt64().ToString("X16");
        }
        internal static void OnProcessor(CpuTarget target, Action read)
        {
            Thread.BeginThreadAffinity();
            bool entered = false; var previous = new Affinity(); IntPtr thread = GetCurrentThread();
            try {
                var next = new Affinity { Group = target.Group, Mask = new UIntPtr(1UL << target.Logical) };
                if (!SetThreadGroupAffinity(thread, ref next, out previous)) throw new Win32Exception(Marshal.GetLastWin32Error());
                entered = true;
                VerifyProcessor(target); read(); VerifyProcessor(target);
            }
            finally {
                try {
                    if (entered) {
                        Affinity discarded, restored;
                        if (!SetThreadGroupAffinity(thread, ref previous, out discarded)
                            || !GetThreadGroupAffinity(thread, out restored) || restored.Group != previous.Group || restored.Mask != previous.Mask)
                            throw new IOException("No se confirmó la devolución de afinidad; se detiene la sonda.");
                    }
                }
                finally { Thread.EndThreadAffinity(); }
            }
        }
        private static void VerifyProcessor(CpuTarget target)
        {
            ProcessorNumber current; GetCurrentProcessorNumberEx(out current);
            if (current.Group != target.Group || current.Number != target.Logical) throw new IOException("La lectura no está fijada al hilo solicitado.");
        }
    }

    internal sealed class CpuPawnNative : ICpuPawnApi, IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool FreeLibrary(IntPtr module);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int VersionCall(out uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenCall(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LoadCall(IntPtr handle, [In] byte[] blob, UIntPtr bytes);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        private delegate int ExecuteCall(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string operation,
            [In] ulong[] input, UIntPtr inputCount, [Out] ulong[] output, UIntPtr outputCount, out UIntPtr returnedCount);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CloseCall(IntPtr handle);
        private IntPtr library;
        private FileStream held;
        private OpenCall open;
        private LoadCall load;
        private ExecuteCall execute;
        private CloseCall close;
        internal string LibraryVersion { get; private set; }
        internal string LibrarySha256 { get; private set; }
        internal string ApiVersion { get; private set; }
        internal CpuPawnNative()
        {
            try {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
                string path = Path.Combine(folder, "PawnIOLib.dll");
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Ruta de PawnIO no admitida.");
                held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!PawnIoTrust.IsTrustedPublisher(path)) throw new IOException("Firma de PawnIOLib sin confirmar.");
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    LibrarySha256 = BitConverter.ToString(sha.ComputeHash(held)).Replace("-", "");
                LibraryVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                Version fileVersion;
                if (!Version.TryParse(LibraryVersion, out fileVersion) || fileVersion < new Version(2, 2, 0))
                    throw new IOException("Se requiere instalación PawnIO 2.2.0 o posterior.");
                library = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x800); // DLL's protected directory + System32 only.
                if (library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                var versionCall = Resolve<VersionCall>("pawnio_version"); uint version;
                int versionResult = versionCall(out version); ApiVersion = "0x" + version.ToString("X8");
                if (versionResult < 0 || !CpuProbeAbi.Supported(version)) throw new IOException("API PawnIO no compatible: " + ApiVersion + ".");
                open = Resolve<OpenCall>("pawnio_open"); load = Resolve<LoadCall>("pawnio_load");
                execute = Resolve<ExecuteCall>("pawnio_execute"); close = Resolve<CloseCall>("pawnio_close");
            }
            catch { Dispose(); throw; }
        }
        private T Resolve<T>(string name) where T : class
        {
            IntPtr function = GetProcAddress(library, name);
            if (function == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(function, typeof(T));
        }
        public int Open(out IntPtr handle) => open(out handle);
        public int Load(IntPtr handle, byte[] module)
        {
            if (!CpuProbeModule.Accepts(module)) throw new InvalidDataException("Solo se admite el módulo oficial AMD fijado.");
            return load(handle, module, new UIntPtr((uint)module.Length));
        }
        public int Read(IntPtr handle, uint index, out ulong value, out ulong count)
        {
            if (!CpuProbeMsr.Allowed(index)) throw new ArgumentException("MSR no admitido.");
            var input = new[] { (ulong)index }; var output = new ulong[1]; UIntPtr returned;
            // No dynamic function names, MSR writes, SMN/PCI operations or async IO.
            int result = execute(handle, "ioctl_read_msr", input, new UIntPtr(1), output, new UIntPtr(1), out returned);
            value = output[0]; count = returned.ToUInt64(); return result;
        }
        public int Close(IntPtr handle) => close(handle);
        public void Dispose()
        {
            if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
            held?.Dispose(); held = null;
        }
    }
}
