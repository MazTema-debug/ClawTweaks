// SPDX-License-Identifier: AGPL-3.0-only
// HidHide public IOCTL API. Constants and ABI checked against Nefarius.Drivers.HidHide (MIT).
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    internal sealed class HidHideConfiguration : IHideConfiguration, IDisposable
    {
        private readonly SafeFileHandle handle;
        internal HidHideConfiguration()
        {
            handle = CreateFile(@"\\.\HidHide", 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("HidHide no está disponible. Instálalo o cierra su configurador y vuelve a intentar."); }
        }
        private static uint Code(int function) => (0x8001u << 16) | (1u << 14) | ((uint)function << 2);
        private byte[] Get(int function, int size)
        {
            var buffer = new byte[size]; uint written;
            if (!DeviceIoControl(handle, Code(function), null, 0, buffer, (uint)buffer.Length, out written, IntPtr.Zero)) throw new Win32Exception();
            if (written > size) throw new InvalidDataException("Respuesta HidHide demasiado grande.");
            return buffer.Take((int)written).ToArray();
        }
        private void Put(int function, byte[] input)
        {
            uint written;
            if (!DeviceIoControl(handle, Code(function), input, (uint)input.Length, null, 0, out written, IntPtr.Zero)) throw new Win32Exception();
        }
        private string[] List(int function)
        {
            uint size;
            if (!DeviceIoControl(handle, Code(function), null, 0, null, 0, out size, IntPtr.Zero)) throw new Win32Exception();
            if (size < 2 || size > 65536 || (size & 1) != 0) throw new InvalidDataException("Lista HidHide inválida.");
            return Encoding.Unicode.GetString(Get(function, (int)size)).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
        }
        private void SetList(int function, string[] values)
        {
            var data = Encoding.Unicode.GetBytes(string.Join("\0", values) + "\0\0");
            if (data.Length > 65536) throw new InvalidDataException("Lista HidHide demasiado grande.");
            Put(function, data);
        }
        public bool Active { get => Get(2052, 1).Single() != 0; set => Put(2053, new[] { (byte)(value ? 1 : 0) }); }
        public bool Inverted => Get(2054, 1).Single() != 0;
        public string[] Applications { get => List(2048); set => SetList(2049, value); }
        public string[] Devices { get => List(2050); set => SetList(2051, value); }
        internal static string ApplicationPath()
        {
            string file = Process.GetCurrentProcess().MainModule.FileName;
            var buffer = new StringBuilder(1024);
            if (QueryDosDevice(file.Substring(0, 2), buffer, buffer.Capacity) == 0) throw new Win32Exception();
            return buffer.ToString() + file.Substring(2);
        }
        public void Dispose() { handle.Dispose(); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[] input, uint inputSize, byte[] output, uint outputSize, out uint written, IntPtr overlapped);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, StringBuilder path, int size);
    }
    internal sealed class HidHideLease
    {
        private readonly string path;
        internal HidHideLease(string directory) { path = Path.Combine(directory, "controller-lease.json"); }
        internal void Activate(string device)
        {
            WithLock(() => {
                if (File.Exists(path)) throw new InvalidOperationException("Hay una recuperación del mando pendiente. Pulsa Devolver mando físico.");
                if (!ControllerDevices.IsLegionInstance(device)) throw new InvalidOperationException("No se puede ocultar este dispositivo.");
                using (var config = new HidHideConfiguration())
                {
                    HideLease.Check(config, device);
                    var app = HidHideConfiguration.ApplicationPath();
                    var process = Process.GetCurrentProcess();
                    var lease = new HideLease { Device = device, Application = app, ActiveBefore = config.Active,
                        AddedApplication = !HideLease.Contains(config.Applications, app), OwnerPid = process.Id, OwnerStarted = process.StartTime.ToUniversalTime().Ticks };
                    var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(lease));
                    using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                    lease.Activate(config);
                }
            });
        }
        internal void Restore(int? owner = null, long started = 0)
        {
            WithLock(() => {
                if (!File.Exists(path)) return;
                var lease = Protocol.Decode<HideLease>(File.ReadAllText(path));
                if (owner.HasValue && (lease.OwnerPid != owner || lease.OwnerStarted != started)) return;
                if (!ControllerDevices.IsLegionInstance(lease.Device) || lease.Application != HidHideConfiguration.ApplicationPath())
                    throw new InvalidDataException("Respaldo del mando inválido. Se conserva para revisión.");
                using (var config = new HidHideConfiguration()) lease.Restore(config);
                File.Delete(path);
            });
        }
        internal void VerifyActive(string device)
        {
            WithLock(() => {
                if (!File.Exists(path)) throw new InvalidOperationException("Falta el respaldo de ocultación del mando.");
                var saved = Protocol.Decode<HideLease>(File.ReadAllText(path));
                if (saved == null || !ControllerDevices.IsLegionInstance(device))
                    throw new InvalidDataException("Respaldo de ocultación del mando inválido.");
                using (var self = Process.GetCurrentProcess()) {
                    using (var config = new HidHideConfiguration())
                        saved.VerifyActive(config, device, HidHideConfiguration.ApplicationPath(), self.Id, self.StartTime.ToUniversalTime().Ticks);
                }
            });
        }
        private static void WithLock(Action action)
        {
            using (var gate = new Mutex(false, @"Global\LegionTweaks.HidHideRecovery"))
            {
                bool owns = false;
                try { try { owns = gate.WaitOne(5000); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new TimeoutException("La recuperación del mando está ocupada."); action(); }
                finally { if (owns) gate.ReleaseMutex(); }
            }
        }
        internal static int Watch(string directory, int pid, long started, string readyName)
        {
            if (!readyName.StartsWith(@"Local\LegionTweaks.ControllerWatchdog.", StringComparison.Ordinal)) return 1;
            using (var parent = Process.GetProcessById(pid))
            {
                if (parent.StartTime.ToUniversalTime().Ticks != started || parent.MainModule.FileName != Process.GetCurrentProcess().MainModule.FileName) return 1;
                using (var ready = EventWaitHandle.OpenExisting(readyName)) ready.Set();
                parent.WaitForExit();
            }
            // Give a competing helper startup/driver transition a bounded opportunity to finish.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try { new HidHideLease(directory).Restore(pid, started); return 0; }
                catch { Thread.Sleep(500); }
            }
            return 1;
        }
    }
}
