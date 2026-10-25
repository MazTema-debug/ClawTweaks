// SPDX-License-Identifier: AGPL-3.0-only
// Fixed gamepad-mode protocol facts from the attributed MIT GoTweaks reference.
// Separate from the passive button reader; no initializer or PnP setter is used.
using System;
using System.Collections.Generic;
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
    internal sealed class NativeControllerModeDevice : IControllerModeDevice
    {
        private readonly Action<int> authorize;
        internal NativeControllerModeDevice(Action<int> authorize = null) { this.authorize = authorize; }

        public ControllerModeState Inspect(string originalReceiver, string location)
        {
            using (var transport = ModeTransport.Open(originalReceiver, location))
            {
                int mode = transport.Query();
                var state = transport.InspectRoot(); state.Mode = mode;
                if ((mode == 1 && !ControllerModeProtocol.IsOriginalReceiver(state.Receiver))
                    || (mode == 2 && !ControllerModeProtocol.IsTemporaryReceiver(state.Receiver)))
                    throw new InvalidDataException("El modo consultado no coincide con el receptor USB.");
                return state;
            }
        }

        public void ChangeMode(string originalReceiver, string location, int mode)
        {
            if (authorize == null || (mode != 1 && mode != 2) || !ControllerModeProtocol.IsOriginalReceiver(originalReceiver) || !ControllerModeProtocol.IsLocationPath(location))
                throw new InvalidOperationException("La escritura del modo no está autorizada.");
            Exception transportFailure = null;
            using (var transport = ModeTransport.Open(originalReceiver, location))
            {
                int current = transport.Query();
                var state = transport.InspectRoot();
                if (current == 1 ? !string.Equals(state.Receiver, originalReceiver, StringComparison.OrdinalIgnoreCase)
                    : !ControllerModeProtocol.IsTemporaryReceiver(state.Receiver))
                    throw new InvalidDataException("Cambió el receptor antes de escribir el modo.");
                authorize(mode); // Fresh journal, worker identity and deadline, after all queries.
                if (mode == 1 && current == 1) return;
                if (mode == 2 && current != 1) throw new InvalidOperationException("El cambio temporal requiere XInput confirmado.");
                try { transport.Set(mode); }
                catch (Win32Exception ex) { transportFailure = ex; }
                catch (TimeoutException ex) { transportFailure = ex; }
                // Re-enumeration can complete before Windows finishes the write.
                // Never retry the setter; use a new handle and fresh readback below.
            }
            var elapsed = Stopwatch.StartNew();
            do
            {
                try { if (Inspect(originalReceiver, location).Mode == mode) return; }
                catch (Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (TimeoutException) { }
                Thread.Sleep(100);
            } while (elapsed.ElapsedMilliseconds < 4000);
            throw new InvalidOperationException("No se confirmó el modo solicitado; se conserva su recuperación.", transportFailure);
        }

        internal static string RootForEndpoint(string instance)
        {
            uint node;
            ModeTransport.Check(ModeTransport.CM_Locate_DevNodeW(out node, instance, 0));
            ModeTransport.RequireStarted(node);
            return ModeTransport.ReceiverRoot(node);
        }

        private sealed class ModeTransport : IDisposable
        {
            private SafeFileHandle handle;
            private readonly EventWaitHandle completed = new EventWaitHandle(false, EventResetMode.ManualReset);
            private IntPtr data, overlapped;
            private readonly string receiver;
            private readonly string location;
            private ModeTransport(string path, string receiver, string location)
            {
                this.receiver = receiver; this.location = location;
                try {
                    handle = CreateFile(path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                    if (handle.IsInvalid) throw NativeReadFailure.Create("abrir el lector de modo", Marshal.GetLastWin32Error());
                    data = Marshal.AllocHGlobal(64); overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlapped)));
                    InspectRoot();
                }
                catch { Dispose(); throw; }
            }

            internal static ModeTransport Open(string original, string location)
            {
                if ((original == null) != (location == null) || (original != null &&
                    (!ControllerModeProtocol.IsOriginalReceiver(original) || !ControllerModeProtocol.IsLocationPath(location))))
                    throw new InvalidDataException("Identidad o ubicación original inválidas.");
                Guid guid; HidD_GetHidGuid(out guid);
                var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
                if (set == new IntPtr(-1)) throw new Win32Exception();
                var paths = new List<string>(); var roots = new List<string>();
                try {
                    for (uint index = 0; ; index++) {
                        var face = new DeviceInterface { Size = Marshal.SizeOf(typeof(DeviceInterface)) };
                        if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref face)) {
                            if (Marshal.GetLastWin32Error() == 259) break; throw new Win32Exception();
                        }
                        uint required; SetupDiGetDeviceInterfaceDetail(set, ref face, IntPtr.Zero, 0, out required, IntPtr.Zero);
                        if (required < 8 || required > 65536) throw new InvalidDataException("Descriptor de ruta inválido.");
                        IntPtr detail = Marshal.AllocHGlobal((int)required);
                        try {
                            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                            var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                            if (!SetupDiGetDeviceInterfaceDetailInfo(set, ref face, detail, required, out required, ref info)) throw new Win32Exception();
                            string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                            if (!LegionHidReader.IsLenovoPath(path)) continue;
                            using (var probe = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                                if (probe.IsInvalid) throw new Win32Exception();
                                var attributes = new Attributes { Size = Marshal.SizeOf(typeof(Attributes)) };
                                if (!HidD_GetAttributes(probe, ref attributes)) throw NativeReadFailure.Create("leer los atributos HID", Marshal.GetLastWin32Error());
                                IntPtr preparsed;
                                if (!HidD_GetPreparsedData(probe, out preparsed)) throw NativeReadFailure.Create("leer el descriptor HID", Marshal.GetLastWin32Error());
                                try {
                                    Caps caps;
                                    if (HidP_GetCaps(preparsed, out caps) != 0x110000) throw new InvalidDataException("Descriptor HID inválido.");
                                    if (!LegionHidReader.IsButtonCollection(attributes.Vendor, attributes.Product, caps.UsagePage, caps.Usage, caps.InputLength)) continue;
                                    if (caps.OutputLength != 64 || (attributes.Product != 0x61eb && attributes.Product != 0x61ec))
                                        throw new InvalidOperationException("La transición solo admite el par de receptores 61EB/61EC revisado.");
                                    paths.Add(path); roots.Add(ReceiverRoot(info.DevInst));
                                }
                                finally { HidD_FreePreparsedData(preparsed); }
                            }
                        }
                        finally { Marshal.FreeHGlobal(detail); }
                    }
                }
                finally { SetupDiDestroyDeviceInfoList(set); }
                if (paths.Count != 1) throw new InvalidOperationException("Se requiere un único receptor Legion compatible para consultar su modo.");
                if (original != null && ControllerModeProtocol.IsOriginalReceiver(roots[0]) && !string.Equals(roots[0], original, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El receptor presente no coincide con el original guardado.");
                return new ModeTransport(paths[0], roots[0], location);
            }

            internal static string ReceiverRoot(uint node)
            {
                for (int level = 0; level < 8; level++) {
                    var id = new StringBuilder(1024); Check(CM_Get_Device_IDW(node, id, id.Capacity, 0));
                    string value = id.ToString();
                    if (ControllerModeProtocol.IsOriginalReceiver(value) || ControllerModeProtocol.IsTemporaryReceiver(value)) return value;
                    uint parent; Check(CM_Get_Parent(out parent, node, 0)); node = parent;
                }
                throw new InvalidDataException("No se confirmó el padre exacto de la colección HID.");
            }

            internal ControllerModeState InspectRoot()
            {
                uint node; Check(CM_Locate_DevNodeW(out node, receiver, 0));
                var id = new StringBuilder(1024); Check(CM_Get_Device_IDW(node, id, id.Capacity, 0));
                if (!string.Equals(id.ToString(), receiver, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cambió la identidad USB.");
                uint status, problem; Check(CM_Get_DevNode_Status(out status, out problem, node, 0));
                uint type, size = 256; var service = new byte[size];
                Check(CM_Get_DevNode_Registry_PropertyW(node, 5, out type, service, ref size, 0));
                if (type != 1 || size < 2 || size > service.Length || (size & 1) != 0) throw new InvalidDataException("Servicio del receptor inválido.");
                string name = Encoding.Unicode.GetString(service, 0, (int)size).TrimEnd('\0');
                if ((status & 8) == 0 || (status & 0x400) != 0 || problem != 0 || !string.Equals(name, "usbccgp", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("El receptor USB no está iniciado y sin problemas.");
                string currentLocation = ReadLocation(node);
                if (location != null && !string.Equals(location, currentLocation, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("El receptor no está en la ubicación física guardada.");
                return new ControllerModeState { Receiver = id.ToString(), Location = currentLocation, Service = name, Present = true, Problem = problem };
            }

            private static string ReadLocation(uint node)
            {
                var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 37 };
                uint type, size = 8192; var bytes = new byte[size];
                Check(CM_Get_DevNode_PropertyW(node, ref key, out type, bytes, ref size, 0));
                if (type != 0x2012 || size < 4 || size > bytes.Length || (size & 1) != 0)
                    throw new InvalidDataException("La ubicación física del receptor no tiene un formato admitido.");
                var paths = Encoding.Unicode.GetString(bytes, 0, (int)size).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                return ControllerModeProtocol.SelectLocation(paths);
            }

            internal static void RequireStarted(uint node)
            {
                uint status, problem; Check(CM_Get_DevNode_Status(out status, out problem, node, 0));
                if ((status & 8) == 0 || (status & 0x400) != 0 || problem != 0)
                    throw new InvalidOperationException("La función XInput original no está iniciada y sin problemas.");
            }

            private static void Signal(object state) { try { ((EventWaitHandle)state).Set(); } catch (ObjectDisposedException) { } }
            internal int Query()
            {
                using (var stop = new ManualResetEvent(false))
                using (var timer = new Timer(Signal, stop, 200, Timeout.Infinite)) {
                    try { for (int n = 0; !stop.WaitOne(0); n++) { if (n > 4096) throw new InvalidDataException("Flujo HID inesperado."); Read(stop); } }
                    catch (OperationCanceledException) { }
                }
                using (var stop = new ManualResetEvent(false))
                using (var timer = new Timer(Signal, stop, 3000, Timeout.Infinite)) {
                    Send(stop, null);
                    for (int n = 0; !stop.WaitOne(0); n++) {
                        if (n > 16384) throw new InvalidDataException("Flujo HID inesperado.");
                        int mode; if (ControllerModeProtocol.TryReadMode(Read(stop), out mode)) return mode;
                    }
                }
                throw new TimeoutException("No se recibió un modo válido del firmware.");
            }
            internal void Set(int mode)
            {
                if (mode != 1 && mode != 2) throw new InvalidDataException("Modo de escritura no admitido.");
                using (var stop = new ManualResetEvent(false))
                using (var timer = new Timer(Signal, stop, 3000, Timeout.Infinite)) Send(stop, mode);
            }
            private void Prepare()
            {
                completed.Reset();
                Marshal.StructureToPtr(new Overlapped { Event = completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            }
            private uint Finish(bool immediate, WaitHandle stop)
            {
                if (!immediate) { int error = Marshal.GetLastWin32Error(); if (error != 997) throw NativeReadFailure.Create("iniciar la lectura o escritura del modo", error); }
                uint count;
                if (!immediate && WaitHandle.WaitAny(new[] { stop, completed }, 1000) != 1) {
                    CancelIoEx(handle, overlapped); GetOverlappedResult(handle, overlapped, out count, true);
                    if (stop.WaitOne(0)) throw new OperationCanceledException();
                    return 0;
                }
                if (!GetOverlappedResult(handle, overlapped, out count, true)) throw NativeReadFailure.Create("completar la lectura o escritura del modo", Marshal.GetLastWin32Error());
                return count;
            }
            private byte[] Read(WaitHandle stop)
            {
                Prepare(); uint read;
                uint count = Finish(ReadFile(handle, data, 64, out read, overlapped), stop);
                if (count != 64) return null;
                var value = new byte[64]; Marshal.Copy(data, value, 0, 64); return value;
            }
            private void Send(WaitHandle stop, int? mode)
            {
                var frame = new byte[64]; frame[0] = 5; frame[2] = mode.HasValue ? (byte)4 : (byte)3;
                frame[3] = 14; frame[4] = 3; if (mode.HasValue) frame[5] = (byte)mode.Value;
                Marshal.Copy(frame, 0, data, 64); Prepare(); uint written;
                if (Finish(WriteFile(handle, data, 64, out written, overlapped), stop) != 64)
                    throw new TimeoutException("La petición de modo no terminó con longitud completa.");
            }
            internal static void Check(uint result) { if (result != 0) throw new Win32Exception("No se pudo consultar el receptor (CM 0x" + result.ToString("X") + ")."); }
            public void Dispose()
            {
                handle?.Dispose(); handle = null;
                if (data != IntPtr.Zero) { Marshal.FreeHGlobal(data); data = IntPtr.Zero; }
                if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
                completed.Dispose();
            }
            [StructLayout(LayoutKind.Sequential)] private struct DeviceInterface { internal int Size; internal Guid Guid; internal uint Flags; internal IntPtr Reserved; }
            [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { internal int Size; internal Guid ClassGuid; internal uint DevInst; internal IntPtr Reserved; }
            [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { internal Guid Format; internal uint Id; }
            [StructLayout(LayoutKind.Sequential)] private struct Attributes { internal int Size; internal ushort Vendor, Product, Version; }
            [StructLayout(LayoutKind.Sequential)] private struct Overlapped { internal IntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
            [StructLayout(LayoutKind.Sequential)] private struct Caps {
                internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
                internal ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
            }
            [DllImport("hid.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern void HidD_GetHidGuid(out Guid guid);
            [DllImport("hid.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attributes);
            [DllImport("hid.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
            [DllImport("hid.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
            [DllImport("hid.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
            [DllImport("setupapi.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref DeviceInterface face);
            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface face, IntPtr detail, uint size, out uint required, IntPtr info);
            [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool SetupDiGetDeviceInterfaceDetailInfo(IntPtr set, ref DeviceInterface face, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
            [DllImport("setupapi.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool ReadFile(SafeFileHandle file, IntPtr data, uint length, out uint read, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool WriteFile(SafeFileHandle file, IntPtr data, uint length, out uint written, IntPtr overlapped);
            [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint count, bool wait);
            [DllImport("kernel32.dll", SetLastError = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);
            [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] internal static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
            [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, byte[] buffer, ref uint length, uint flags);
        }
    }
}
