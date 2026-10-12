// SPDX-License-Identifier: MPL-2.0
// Read-only subset adapted from LibreHardwareMonitor/Interop/AtiAdlxx.cs.
// Copyright (C) LibreHardwareMonitor and Contributors. All Rights Reserved.
// This Source Code Form is subject to the Mozilla Public License, v. 2.0.
// See LICENSE.LibreHardwareMonitor and SOURCES.md for source and modifications.
using System;
using System.Runtime.InteropServices;

namespace LegionTweaks
{
    internal static class AmdNative
    {
        private const string Dll = "atiadlxx.dll";
        internal const int SensorCount = 256;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr Allocate(int size);
        internal static readonly Allocate Allocator = Marshal.AllocHGlobal;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        internal struct Adapter
        {
            public int Size, Index;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Udid;
            public int Bus, Device, Function, Vendor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Display;
            public int Present, Exist;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string DriverPathExt;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Pnp;
            public int DisplayIndex;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Sensor { public int Supported, Value; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Log
        {
            public int Size;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = SensorCount)] public Sensor[] Sensors;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Temperature { public int Size, MilliCelsius; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Activity
        {
            public int Size, EngineClock, MemoryClock, Vddc, ActivityPercent, PerformanceLevel;
            public int BusSpeed, BusLanes, MaximumBusLanes, Reserved;
        }

        // Load the installed AMD user-mode driver only from System32.
        // No tuning entry points or third-party kernel driver are used.
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_Main_Control_Create")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int Create(Allocate allocate, int connected, ref IntPtr context);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_Main_Control_Destroy")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int Destroy(IntPtr context);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_Adapter_NumberOfAdapters_Get")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int Count(IntPtr context, ref int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_Adapter_AdapterInfo_Get")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int Adapters(IntPtr context, IntPtr adapters, int bytes);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_New_QueryPMLogData_Get")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int Query(IntPtr context, int adapter, ref Log log);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_Overdrive5_Temperature_Get")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetTemperature(IntPtr context, int adapter, int controller, ref Temperature temperature);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_OverdriveN_Temperature_Get")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetTemperatureN(IntPtr context, int adapter, int type, ref int milliCelsius);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ADL2_Overdrive5_CurrentActivity_Get")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetActivity(IntPtr context, int adapter, ref Activity activity);
    }
}
