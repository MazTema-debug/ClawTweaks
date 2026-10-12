// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LegionTweaks
{
    internal sealed class AmdTelemetry : IDisposable
    {
        private IntPtr context;
        private int adapter;
        private string name;
        private DateTime retryAfter;
        private string lastError;
        private static readonly string[] Keys = { "GpuTemperature", "GpuVoltageMv", "SocVoltageMv", "GpuClockMHz", "GpuUsage", "GpuPower", "AsicPower", "CpuPower" };

        private void Initialize()
        {
            if (context != IntPtr.Zero) return;
            if (DateTime.UtcNow < retryAfter) throw new InvalidOperationException(lastError);
            retryAfter = DateTime.UtcNow.AddSeconds(30);
            try
            {
                if (AmdNative.Create(AmdNative.Allocator, 1, ref context) != 0 || context == IntPtr.Zero)
                    throw new InvalidOperationException("No se pudo iniciar la lectura del controlador AMD.");
                int count = 0;
                if (AmdNative.Count(context, ref count) != 0 || count < 1 || count > 64)
                    throw new InvalidOperationException("AMD no devolvió una lista válida de GPU.");
                int size = Marshal.SizeOf(typeof(AmdNative.Adapter));
                IntPtr buffer = Marshal.AllocHGlobal(size * count);
                try
                {
                    // ADL fills fixed-size ANSI records. Zero the allocation and set each size.
                    Marshal.Copy(new byte[size * count], 0, buffer, size * count);
                    for (int i = 0; i < count; i++) Marshal.WriteInt32(buffer, i * size, size);
                    if (AmdNative.Adapters(context, buffer, size * count) != 0)
                        throw new InvalidOperationException("AMD no pudo enumerar la GPU.");
                    var items = new List<AmdNative.Adapter>();
                    for (int i = 0; i < count; i++) items.Add((AmdNative.Adapter)Marshal.PtrToStructure(IntPtr.Add(buffer, i * size), typeof(AmdNative.Adapter)));
                    var chosen = SelectAdapter(items);
                    adapter = chosen.Index; name = chosen.Name;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex)
            {
                lastError = "Telemetría AMD no disponible: " + ex.Message;
                Dispose();
                throw new InvalidOperationException(lastError);
            }
        }

        internal static AmdNative.Adapter SelectAdapter(IEnumerable<AmdNative.Adapter> adapters)
        {
            // Windows ADL can report VendorID as decimal 1002. The PCI string is authoritative.
            // Multiple display records can refer to the same physical adapter.
            var amd = adapters.Where(a => a.Present != 0 &&
                ((a.Udid ?? "").IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (a.Pnp ?? "").IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0))
                .GroupBy(a => a.Bus + ":" + a.Device + ":" + a.Function).Select(g => g.First()).ToArray();
            if (amd.Length != 1)
                throw new InvalidOperationException(amd.Length == 0 ? "No se detectó la GPU AMD." : "Varias GPU AMD: no se puede identificar de forma inequívoca la GPU interna.");
            return amd[0];
        }

        public Reading[] Read()
        {
            try
            {
                Initialize();
                var log = new AmdNative.Log { Size = Marshal.SizeOf(typeof(AmdNative.Log)), Sensors = new AmdNative.Sensor[AmdNative.SensorCount] };
                bool available = false;
                try { available = AmdNative.Query(context, adapter, ref log) == 0; }
                catch (EntryPointNotFoundException) { }
                var readings = Decode(available ? log.Sensors : null, "AMD ADL · " + name);
                // Older AMD drivers may expose OD5 readings but not PMLog. Never substitute CPU temperature.
                var temperature = readings.First(x => x.Key == "GpuTemperature");
                if (!temperature.Value.HasValue)
                {
                    try
                    {
                        var t = new AmdNative.Temperature { Size = Marshal.SizeOf(typeof(AmdNative.Temperature)) };
                        if (AmdNative.GetTemperature(context, adapter, 0, ref t) == 0 && t.MilliCelsius > 0 && t.MilliCelsius <= 150000)
                            Replace(readings, TelemetryValue.Checked("GpuTemperature", (int)Math.Round(t.MilliCelsius / 1000.0), "°C", "AMD ADL OD5 · " + name, 1, 150));
                    }
                    catch (EntryPointNotFoundException) { }
                    if (!readings.First(x => x.Key == "GpuTemperature").Value.HasValue)
                    {
                        try
                        {
                            // ODN EDGE (type 1) uses millidegrees, unlike other ODN temperature types.
                            int milliCelsius = 0;
                            if (AmdNative.GetTemperatureN(context, adapter, 1, ref milliCelsius) == 0 && milliCelsius > 0 && milliCelsius <= 150000)
                                Replace(readings, TelemetryValue.Checked("GpuTemperature", (int)Math.Round(milliCelsius / 1000.0), "°C", "AMD ADL ODN · " + name, 1, 150));
                        }
                        catch (EntryPointNotFoundException) { }
                    }
                }
                if (readings.Any(x => (x.Key == "GpuVoltageMv" || x.Key == "GpuClockMHz" || x.Key == "GpuUsage") && !x.Value.HasValue))
                {
                    try
                    {
                        var a = new AmdNative.Activity { Size = Marshal.SizeOf(typeof(AmdNative.Activity)) };
                        if (AmdNative.GetActivity(context, adapter, ref a) == 0)
                        {
                            string source = "AMD ADL OD5 · " + name;
                            Replace(readings, TelemetryValue.Checked("GpuVoltageMv", a.Vddc, "mV", source, 1, 2500));
                            Replace(readings, TelemetryValue.Checked("GpuClockMHz", a.EngineClock / 100, "MHz", source, 1, 6000));
                            Replace(readings, TelemetryValue.Checked("GpuUsage", a.ActivityPercent, "%", source, 0, 100));
                        }
                    }
                    catch (EntryPointNotFoundException) { }
                }
                // Re-enumerate after a driver reset or initially unavailable metrics, with bounded retries.
                if (readings.All(x => !x.Value.HasValue) && DateTime.UtcNow >= retryAfter)
                { Dispose(); retryAfter = DateTime.MinValue; }
                return readings;
            }
            catch (Exception ex)
            {
                Dispose(); lastError = ex.Message;
                if (DateTime.UtcNow >= retryAfter) retryAfter = DateTime.UtcNow.AddSeconds(30);
                return Keys.Select(k => new Reading { Key = k, Error = ex.Message, Source = "AMD ADL" }).ToArray();
            }
        }
        private static void Replace(Reading[] readings, Reading candidate)
        {
            int index = Array.FindIndex(readings, x => x.Key == candidate.Key);
            if (index >= 0 && !readings[index].Value.HasValue && candidate.Value.HasValue) readings[index] = candidate;
        }
        internal static Reading[] Decode(AmdNative.Sensor[] sensors, string source)
        {
            // PMLog IDs/units follow the read-only mapping in LibreHardwareMonitor AmdGpu.cs.
            Func<int, int?> raw = id => sensors != null && id < sensors.Length && sensors[id].Supported != 0 ? (int?)sensors[id].Value : null;
            var t = TelemetryValue.Checked("GpuTemperature", raw(8), "°C", source + " · EDGE", 1, 150);
            if (!t.Value.HasValue) t = TelemetryValue.Checked("GpuTemperature", raw(28), "°C", source + " · GFX", 1, 150);
            return new[] { t,
                TelemetryValue.Checked("GpuVoltageMv", raw(21), "mV", source, 1, 2500),
                TelemetryValue.Checked("SocVoltageMv", raw(16), "mV", source, 1, 2500),
                TelemetryValue.Checked("GpuClockMHz", raw(1), "MHz", source, 0, 6000),
                TelemetryValue.Checked("GpuUsage", raw(19), "%", source, 0, 100),
                TelemetryValue.Checked("GpuPower", raw(30), "W", source + " · GFX", 0, 200),
                TelemetryValue.Checked("AsicPower", raw(23), "W", source + " · ASIC", 0, 200),
                TelemetryValue.Checked("CpuPower", raw(33), "W", source + " · CPU", 0, 200) };
        }
        public void Dispose()
        {
            if (context == IntPtr.Zero) return;
            try { AmdNative.Destroy(context); } catch (Exception) { }
            context = IntPtr.Zero;
        }
    }
}
