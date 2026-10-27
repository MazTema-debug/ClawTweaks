// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    // Read-only access to the original Legion Go's vendor collection. In particular,
    // do NOT send the vendor init command: it can alter physical XInput availability.
    internal sealed class LegionHidReader : IDisposable
    {
        private SafeFileHandle handle;
        private readonly EventWaitHandle complete = new EventWaitHandle(false, EventResetMode.ManualReset);
        private IntPtr buffer, overlapped;
        internal ushort Product { get; }
        private LegionHidReader(string path, ushort product)
        {
            Product = product;
            try
            {
                handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo leer el HID Legion.");
                buffer = Marshal.AllocHGlobal(64);
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlapped)));
            }
            catch { Dispose(); throw; }
        }
        // Recent original-Go firmware shares the 61EB–61EE IDs with newer models.
        // Program's 8APU1/Z1 Extreme gate still determines which machine may use this.
        internal static bool IsLenovoPath(string path) => path != null
            && path.IndexOf("vid_17ef&pid_", StringComparison.OrdinalIgnoreCase) >= 0;
        internal static bool IsButtonCollection(ushort vendor, ushort product, ushort page, ushort usage, ushort length) =>
            vendor == 0x17ef && ((product >= 0x6182 && product <= 0x6185) || (product >= 0x61eb && product <= 0x61ee))
            && page == 0xffa0 && usage == 1 && length == 64;

        internal static LegionHidReader Open(Action<string> diagnostic)
        {
            Guid guid; HidD_GetHidGuid(out guid);
            var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            var matches = new List<string>();
            var details = new List<string>();
            int lenovo = 0, inaccessible = 0, metadataErrors = 0;
            string selected = null;
            ushort selectedProduct = 0;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var face = new DeviceInterface { Size = Marshal.SizeOf(typeof(DeviceInterface)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref face))
                    { if (Marshal.GetLastWin32Error() == 259) break; throw new Win32Exception(); }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set, ref face, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required < 8 || required > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref face, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (!IsLenovoPath(path)) continue;
                        lenovo++;
                        using (var probe = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (probe.IsInvalid) { inaccessible++; continue; }
                            var attributes = new Attributes { Size = Marshal.SizeOf(typeof(Attributes)) };
                            if (!HidD_GetAttributes(probe, ref attributes)) { metadataErrors++; continue; }
                            IntPtr data;
                            if (!HidD_GetPreparsedData(probe, out data)) { metadataErrors++; continue; }
                            try
                            {
                                Caps caps;
                                if (HidP_GetCaps(data, out caps) != 0x110000) { metadataErrors++; continue; }
                                string description = "PID " + attributes.Product.ToString("X4") + " · uso "
                                    + caps.UsagePage.ToString("X4") + ":" + caps.Usage.ToString("X4") + " · entrada " + caps.InputLength + " B";
                                if (details.Count < 8) details.Add(description);
                                if (IsButtonCollection(attributes.Vendor, attributes.Product, caps.UsagePage, caps.Usage, caps.InputLength))
                                {
                                    matches.Add(path);
                                    selected = description;
                                    selectedProduct = attributes.Product;
                                }
                            }
                            finally { HidD_FreePreparsedData(data); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            diagnostic?.Invoke("HID Lenovo: " + lenovo + " · compatibles: " + matches.Count
                + " · sin acceso: " + inaccessible + " · errores de descriptor: " + metadataErrors
                + (matches.Count == 1 ? "\nSeleccionado: " + selected : details.Count > 0 ? "\n" + string.Join("\n", details) : ""));
            if (matches.Count != 1) throw new InvalidOperationException(matches.Count == 0
                ? "No se encontró una interfaz compatible de botones Legion. Se reintentará automáticamente; si persiste, comparte este diagnóstico."
                : "Hay varios HID Legion compatibles. Conecta un solo juego de mandos.");
            return new LegionHidReader(matches[0], selectedProduct);
        }
        internal byte[] Read(WaitHandle stop, WaitHandle changed)
        {
            complete.Reset();
            Marshal.StructureToPtr(new Overlapped { Event = complete.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            uint bytes;
            bool immediate = ReadFile(handle, buffer, 64, out bytes, overlapped);
            if (!immediate && Marshal.GetLastWin32Error() != 997) throw new Win32Exception();
            if (!immediate)
            {
                int wait = WaitHandle.WaitAny(new[] { stop, changed, complete }, 1000);
                if (wait != 2)
                {
                    CancelIoEx(handle, overlapped);
                    // Cancellation is asynchronous: keep unmanaged memory and the event
                    // alive until the driver finishes before reusing/freeing either.
                    GetOverlappedResult(handle, overlapped, out bytes, true);
                    if (wait != WaitHandle.WaitTimeout) throw new OperationCanceledException();
                    return null;
                }
            }
            if (!GetOverlappedResult(handle, overlapped, out bytes, true)) throw new Win32Exception();
            if (bytes != 64) return new byte[0];
            var result = new byte[64]; Marshal.Copy(buffer, result, 0, 64); return result;
        }
        public void Dispose()
        {
            handle?.Dispose(); handle = null;
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
            if (overlapped != IntPtr.Zero) { Marshal.FreeHGlobal(overlapped); overlapped = IntPtr.Zero; }
            complete.Dispose();
        }
        [StructLayout(LayoutKind.Sequential)] internal struct DeviceInterface { internal int Size; internal Guid Guid; internal uint Flags; internal IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Attributes { internal int Size; internal ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)] private struct Overlapped { internal IntPtr Internal, InternalHigh; internal uint Offset, OffsetHigh; internal IntPtr Event; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps
        {
            internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
            internal ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attributes);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref DeviceInterface face);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface face, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint size, out uint read, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint bytes, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    }
}
