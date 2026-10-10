// SPDX-License-Identifier: AGPL-3.0-only
// Adapted from GoTweaks LenovoWMIService / LegionManager and PowerManager.
// The original MIT notice is retained in LICENSE.GoTweaks. Exact sources: SOURCES.md.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class LenovoHardware : IHardware
    {
        private static readonly Dictionary<Setting, uint> Features = new Dictionary<Setting, uint>
        {
            // Match LegionManager.ApplyTDPValues, NOT the inconsistent short/long XML comments.
            { Setting.Spl, 0x0101FF00 }, { Setting.Sppt, 0x0102FF00 }, { Setting.Fppt, 0x0103FF00 },
            { Setting.FanFullSpeed, 0x04020000 }, { Setting.ChargeLimit, 0x03010001 }
        };
        public DeviceIdentity Identity { get; }
        public string ActiveScheme => PowerSettings.ActiveScheme.ToString();
        public bool OnAc { get { NativePowerStatus status; return GetSystemPowerStatus(out status) && status.Ac == 1; } }
        public LenovoHardware()
        {
            Identity = new DeviceIdentity
            {
                Manufacturer = Dmi("Win32_ComputerSystemProduct", "Vendor"),
                Model = Dmi("Win32_ComputerSystemProduct", "Name"),
                Version = Dmi("Win32_ComputerSystemProduct", "Version"),
                Cpu = Dmi("Win32_Processor", "Name")
            };
        }
        private static string Dmi(string type, string property)
        {
            using (var searcher = new ManagementObjectSearcher("SELECT " + property + " FROM " + type))
            using (var results = searcher.Get())
                foreach (ManagementObject item in results)
                    using (item) return Convert.ToString(item[property]);
            return "Unknown";
        }
        private static Dictionary<string, object> Invoke(string type, string method, params object[] arguments)
        {
            using (var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM " + type))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using (var objects = searcher.Get())
                    foreach (ManagementObject item in objects)
                        using (item)
                        using (var parameters = arguments.Length == 0 ? null : item.GetMethodParameters(method))
                        {
                            for (int i = 0; i < arguments.Length; i += 2)
                                parameters[(string)arguments[i]] = arguments[i + 1];
                            using (var result = item.InvokeMethod(method, parameters, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(3) }))
                            {
                                var values = new Dictionary<string, object>();
                                if (result != null)
                                    foreach (PropertyData property in result.Properties) values[property.Name] = property.Value;
                                object returnValue;
                                if (values.TryGetValue("ReturnValue", out returnValue) && returnValue != null && Convert.ToUInt32(returnValue) != 0)
                                    throw new InvalidOperationException(method + " devolvió " + returnValue);
                                return values;
                            }
                        }
            }
            throw new InvalidOperationException(type + " no disponible. Comprueba los controladores Lenovo.");
        }
        private static int ReadOutput(Dictionary<string, object> values, string name)
        {
            object value;
            if (!values.TryGetValue(name, out value) || value == null)
                throw new InvalidOperationException("WMI no devolvió " + name + ".");
            return Convert.ToInt32(value);
        }
        private static int Feature(uint id) => ReadOutput(Invoke("LENOVO_OTHER_METHOD", "GetFeatureValue", "IDs", unchecked((int)id)), "Value");
        public int Read(Address address)
        {
            if (!Identity.IsSupported) throw new InvalidOperationException("Hardware no compatible.");
            uint id;
            if (Features.TryGetValue(address.Setting, out id)) return Feature(id);
            switch (address.Setting)
            {
                case Setting.Mode: return ReadOutput(Invoke("LENOVO_GAMEZONE_DATA", "GetSmartFanMode"), "Data");
                case Setting.PowerLight:
                    int state = ReadOutput(Invoke("LENOVO_LIGHTING_METHOD", "Get_Lighting_Current_Status", "Lighting_ID", (byte)4), "Current_Brightness_Level");
                    if (state != 1 && state != 2) throw new InvalidOperationException("Formato LED no compatible con este firmware.");
                    return state == 2 ? 1 : 0;
                case Setting.CpuBoost:
                case Setting.Epp: return PowerSettings.Read(address);
                case Setting.OsPowerMode: return PowerSettings.ReadOverlay();
                default: throw new ArgumentOutOfRangeException(nameof(address));
            }
        }
        public void Write(Address address, int value)
        {
            if (!Identity.IsSupported) throw new InvalidOperationException("Hardware no compatible.");
            uint id;
            if (Features.TryGetValue(address.Setting, out id))
            {
                Invoke("LENOVO_OTHER_METHOD", "SetFeatureValue", "IDs", unchecked((int)id), "value", value);
                return;
            }
            switch (address.Setting)
            {
                case Setting.Mode: Invoke("LENOVO_GAMEZONE_DATA", "SetSmartFanMode", "Data", value); break;
                case Setting.PowerLight:
                    Invoke("LENOVO_LIGHTING_METHOD", "Set_Lighting_Current_Status", "Lighting_ID", (byte)4,
                        "Current_State_Type", (byte)0, "Current_Brightness_Level", (byte)(value == 1 ? 2 : 1)); break;
                case Setting.CpuBoost:
                case Setting.Epp: PowerSettings.Write(address, value); break;
                case Setting.OsPowerMode: PowerSettings.WriteOverlay(value); break;
                default: throw new ArgumentOutOfRangeException(nameof(address));
            }
        }
        public Reading[] ReadTelemetry()
        {
            var readings = new List<Reading>();
            foreach (var sensor in new[] { Tuple.Create("CpuTemperature", 0x05040000u), Tuple.Create("GpuTemperature", 0x05050000u), Tuple.Create("FanRpm", 0x04030001u) })
            {
                try { readings.Add(new Reading { Key = sensor.Item1, Value = Feature(sensor.Item2) }); }
                catch (Exception ex) { readings.Add(new Reading { Key = sensor.Item1, Error = ex.Message }); }
            }
            NativePowerStatus status;
            if (GetSystemPowerStatus(out status) && status.Percent != 255)
                readings.Add(new Reading { Key = "Battery", Value = status.Percent });
            return readings.ToArray();
        }
        public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
        [StructLayout(LayoutKind.Sequential)] private struct NativePowerStatus
        { public byte Ac, Flags, Percent, Saver; public int Life, FullLife; }
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out NativePowerStatus status);
    }

    internal static class PowerSettings
    {
        private static Guid Subgroup = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid Boost = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid Epp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        private static readonly Guid[] Overlays = { new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a"), Guid.Empty, new Guid("ded574b5-45a0-4f42-8737-46345c09c238") };
        private static void Check(uint status) { if (status != 0) throw new Win32Exception((int)status); }
        public static Guid ActiveScheme
        {
            get
            {
                IntPtr pointer;
                Check(PowerGetActiveScheme(IntPtr.Zero, out pointer));
                try { return (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); }
                finally { LocalFree(pointer); }
            }
        }
        public static int Read(Address address)
        {
            var scheme = new Guid(address.Scheme);
            var setting = address.Setting == Setting.CpuBoost ? Boost : Epp;
            uint value;
            Check(address.Ac ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref setting, out value)
                : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref setting, out value));
            return checked((int)value);
        }
        public static void Write(Address address, int value)
        {
            var scheme = new Guid(address.Scheme);
            var setting = address.Setting == Setting.CpuBoost ? Boost : Epp;
            Check(address.Ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref setting, (uint)value)
                : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref Subgroup, ref setting, (uint)value));
            // Restore the recorded plan without switching away from a user's newly selected plan.
            if (scheme == ActiveScheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
        }
        public static int ReadOverlay()
        {
            Guid value; Check(PowerGetActualOverlayScheme(out value));
            int index = Array.IndexOf(Overlays, value);
            if (index < 0) throw new InvalidOperationException("Modo de energía Windows desconocido.");
            return index;
        }
        public static void WriteOverlay(int value) { var overlay = Overlays[value]; Check(PowerSetActiveOverlayScheme(ref overlay)); }
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid subgroup, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(ref Guid scheme);
    }
}
