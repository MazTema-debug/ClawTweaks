// SPDX-License-Identifier: AGPL-3.0-only
// Standalone lab transport derived from LeerIndiceFPS.cs. Identity and HID enumeration follow the port's own
// LegionHidReader. The companion protocol file restricts every outgoing report.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
#if NETFRAMEWORK
using System.Management;
#endif
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
internal static partial class FpsY3NativeTransport
{
    private const int ReportLength = 64;
    private const uint ReadAccess = 0x80000000, WriteAccess = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterface
    {
        internal int Size;
        internal Guid Guid;
        internal uint Flags;
        internal IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes
    {
        internal int Size;
        internal ushort Vendor, Product, Version;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Caps
    {
        internal ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] internal ushort[] Reserved;
        internal ushort LinkNodes, InputButtons, InputValues, InputIndices;
        internal ushort OutputButtons, OutputValues, OutputIndices;
        internal ushort FeatureButtons, FeatureValues, FeatureIndices;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlapped
    {
        internal IntPtr Internal, InternalHigh;
        internal uint Offset, OffsetHigh;
        internal IntPtr Event;
    }

    private static byte[] MapQuery(int selector, int index)
    {
        if ((selector != 3 && selector != 4) || index < 1 || index > 3)
            throw new ArgumentException("Selector o índice fuera del ensayo.");
        var bytes = new byte[ReportLength];
        bytes[0] = 5; bytes[1] = 6; bytes[2] = 0x6c; bytes[3] = 0x0b;
        bytes[4] = (byte)selector; bytes[5] = (byte)index; bytes[6] = 1;
        return bytes;
    }

    private static byte[] IndexQuery()
    {
        var bytes = new byte[ReportLength];
        bytes[0] = 5;
        bytes[1] = 5;
        bytes[2] = 0x6e;
        bytes[3] = 1;
        bytes[4] = 4;
        bytes[5] = 1;
        return bytes;
    }

    private static bool IsIndexReply(byte[] report)
    {
        return report != null && report.Length == ReportLength && report[0] == 4
            && report[1] >= 5 && report[1] <= 63 && report[2] == 0x6e && report[3] == 1;
    }

    private static bool IsReply(byte[] report, byte[] query)
    {
        return report != null && report.Length == ReportLength && report[0] == 4
            && report[1] >= 5 && report[1] <= 63 && report[2] == query[2] && report[3] == query[3];
    }

    private static bool TryIndex(byte[] report, out int index)
    {
        index = 0;
        // The OEM strips the report ID before reading byte 4. This corresponds
        // to byte 5 here. Slot IDs outside 1-3 need a separate interpretation.
        if (!IsIndexReply(report) || report[4] != 4 || report[5] < 1 || report[5] > 3) return false;
        index = report[5];
        return true;
    }

    internal static void CheckDecoder()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf(typeof(DeviceInterface)) != 32
            || Marshal.SizeOf(typeof(NativeOverlapped)) != 32 || Marshal.SizeOf(typeof(Caps)) != 64)
            throw new InvalidOperationException("ABI inesperada.");
        byte[] request = IndexQuery();
        if (request.Length != 64 || BitConverter.ToString(request, 0, 6) != "05-05-6E-01-04-01")
            throw new InvalidOperationException("Consulta inesperada.");
        int index;
        foreach (int slot in new[] { 1, 2, 3 })
        {
            var sample = new byte[64];
            sample[0] = 4; sample[1] = 6; sample[2] = 0x6e; sample[3] = 1; sample[4] = 4; sample[5] = (byte)slot;
            if (!TryIndex(sample, out index) || index != slot) throw new InvalidOperationException("Decodificación inesperada.");
            for (int reason = 0; reason < 6; reason++)
            {
                var invalid = (byte[])sample.Clone();
                if (reason == 0) invalid[0] = 5;
                if (reason == 1) invalid[1] = 64;
                if (reason == 2) invalid[2] = 0x74;
                if (reason == 3) invalid[3] = 2;
                if (reason == 4) invalid[4] = 3;
                if (reason == 5) invalid[5] = 0;
                if (TryIndex(invalid, out index)) throw new InvalidOperationException("Respuesta ajena aceptada.");
            }
        }
        if (TryIndex(null, out index) || TryIndex(new byte[63], out index)) throw new InvalidOperationException("Tamaño inesperado aceptado.");
        if (BitConverter.ToString(MapQuery(3, 1), 0, 7) != "05-06-6C-0B-03-01-01"
            || BitConverter.ToString(MapQuery(4, 3), 0, 7) != "05-06-6C-0B-04-03-01")
            throw new InvalidOperationException("Consulta de mapa inesperada.");
        var other = new byte[64]; other[0] = 4; other[1] = 6; other[2] = 0x6c; other[3] = 7;
        if (IsReply(other, MapQuery(3, 1))) throw new InvalidOperationException("Otra respuesta de comando aceptada.");
    }

    internal static void RequireOriginalGo()
    {
#if NETFRAMEWORK
        string model = null, manufacturer = null, cpu = null;
        using (var search = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        using (var rows = search.Get())
            foreach (ManagementBaseObject row in rows)
            using (row) { model = Convert.ToString(row["Model"]); manufacturer = Convert.ToString(row["Manufacturer"]); }
        using (var search = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
        using (var rows = search.Get())
            foreach (ManagementBaseObject row in rows)
            using (row) { cpu = Convert.ToString(row["Name"]); }
        if (!string.Equals(manufacturer, "LENOVO", StringComparison.OrdinalIgnoreCase)
            || model == null || !model.StartsWith("83E1", StringComparison.OrdinalIgnoreCase)
            || cpu == null || cpu.IndexOf("AMD Ryzen Z1 Extreme", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("El equipo no coincide con la Legion Go original objetivo.");
        Console.WriteLine("MODELO=83E1 CPU=Z1Extreme");

#else
        throw new PlatformNotSupportedException("Las pruebas .NET 8 no abren el dispositivo FPS.");
#endif
    }
    internal static string FindInterface()
    {
        Guid guid; HidD_GetHidGuid(out guid);
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var matches = new List<string>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var face = new DeviceInterface { Size = Marshal.SizeOf(typeof(DeviceInterface)) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref face))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                uint required;
                SetupDiGetDeviceInterfaceDetail(set, ref face, IntPtr.Zero, 0, out required, IntPtr.Zero);
                if (required < 8 || required > 65536) continue;
                IntPtr detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, 8);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref face, detail, required, out required, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (path == null || path.IndexOf("vid_17ef&pid_61ee", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    using (var probe = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (probe.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Metadatos FPS inaccesibles.");
                        var attrs = new Attributes { Size = Marshal.SizeOf(typeof(Attributes)) };
                        if (!HidD_GetAttributes(probe, ref attrs)) throw new Win32Exception();
                        IntPtr preparsed;
                        if (!HidD_GetPreparsedData(probe, out preparsed)) throw new Win32Exception();
                        try
                        {
                            Caps caps;
                            if (HidP_GetCaps(preparsed, out caps) != 0x110000) throw new InvalidOperationException("Descriptor HID inválido.");
                            if (attrs.Vendor == 0x17ef && attrs.Product == 0x61ee && caps.UsagePage == 0xffa0
                                && caps.Usage == 1 && caps.InputLength == 64 && caps.OutputLength == 64)
                            {
                                matches.Add(path);
                                Console.WriteLine("COLECCION=61EE FFA0:0001 IN=64 OUT=64 VERSION=" + attrs.Version.ToString("X4"));
                            }
                        }
                        finally { HidD_FreePreparsedData(preparsed); }
                    }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        if (matches.Count != 1) throw new InvalidOperationException("Se requiere una sola colección FPS coincidente. Coincidencias=" + matches.Count);
        return matches[0];
    }

    private static byte[] ExchangeUnchecked(SafeFileHandle handle, byte[] request)
    {
        var deadline = Stopwatch.StartNew();
        using (var initialRead = new PendingIo(handle, null))
        {
            using (var write = new PendingIo(handle, request))
            {
                byte[] ignored;
                if (!write.Wait(750, out ignored)) throw new TimeoutException("La consulta no terminó a tiempo.");
                Console.WriteLine("QUERY " + BitConverter.ToString(request));
            }
            byte[] report;
            if (initialRead.Wait(750, out report) && IsReply(report, request)) return report;
        }
        int seen = 0;
        while (deadline.ElapsedMilliseconds < 2500 && seen++ < 200)
        {
            using (var read = new PendingIo(handle, null))
            {
                byte[] report;
                int remaining = (int)Math.Min(250, 2500 - deadline.ElapsedMilliseconds);
                if (remaining <= 0) break;
                if (read.Wait(remaining, out report) && IsReply(report, request)) return report;
            }
        }
        throw new TimeoutException("Sin respuesta reconocida a la consulta. Informes examinados=" + seen);
    }

    private sealed class PendingIo : IDisposable
    {
        private readonly SafeFileHandle handle;
        private readonly EventWaitHandle complete = new EventWaitHandle(false, EventResetMode.ManualReset);
        private IntPtr buffer, overlapped;
        private bool pending, disposed;
        internal PendingIo(SafeFileHandle handle, byte[] outgoing)
        {
            this.handle = handle;
            try
            {
                buffer = Marshal.AllocHGlobal(ReportLength);
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                Marshal.Copy(outgoing ?? new byte[ReportLength], 0, buffer, ReportLength);
                Marshal.StructureToPtr(new NativeOverlapped { Event = complete.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                uint transferred;
                bool immediate = outgoing == null
                    ? ReadFile(handle, buffer, ReportLength, out transferred, overlapped)
                    : WriteFile(handle, buffer, ReportLength, out transferred, overlapped);
                if (!immediate && Marshal.GetLastWin32Error() != 997) throw new Win32Exception();
                pending = !immediate;
            }
            catch { Dispose(); throw; }
        }
        internal bool Wait(int milliseconds, out byte[] bytes)
        {
            bytes = null;
            if (pending && !complete.WaitOne(milliseconds)) return false;
            uint transferred;
            if (!GetOverlappedResult(handle, overlapped, out transferred, false)) throw new Win32Exception();
            pending = false;
            if (transferred != ReportLength) throw new InvalidOperationException("Transferencia HID incompleta.");
            bytes = new byte[ReportLength]; Marshal.Copy(buffer, bytes, 0, ReportLength);
            return true;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (pending)
            {
                CancelIoEx(handle, overlapped);
                uint ignored; GetOverlappedResult(handle, overlapped, out ignored, true);
                pending = false;
            }
            if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            complete.Dispose();
        }
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle file, ref Attributes attrs);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref DeviceInterface face);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterface face, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr bytes, uint size, out uint read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, IntPtr bytes, uint size, out uint written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out uint bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
}

}
