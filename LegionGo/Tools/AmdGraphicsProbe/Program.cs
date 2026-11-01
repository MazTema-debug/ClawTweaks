// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Management;
using LegionTweaks;

internal static class Program
{
    // This executable has no elevation manifest, no service entry point, no journal and no writer command.
    private static int Main(string[] args)
    {
        try {
            if (args.Length != 0) throw new ArgumentException("El diagnóstico AMD no admite órdenes ni valores.");
            var identity = new DeviceIdentity();
            using (var search = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            using (var results = search.Get()) foreach (ManagementObject item in results)
                using (item) { identity.Manufacturer = item["Manufacturer"] as string; identity.Model = item["Model"] as string; }
            using (var search = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            using (var results = search.Get()) foreach (ManagementObject item in results)
                using (item) { identity.Cpu = item["Name"] as string; }
            if (!identity.IsSupported) throw new InvalidOperationException("El diagnóstico requiere Legion Go 83E1 / Z1 Extreme.");
            using (var native = new AmdGraphicsNative()) {
                var state = native.Read(); state.Revision = state.Fingerprint();
                state.Message = "Diagnóstico AMD de solo lectura. Actividad dentro del juego sin comprobar.";
                Console.WriteLine(Protocol.Encode(state));
                return 0;
            }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
