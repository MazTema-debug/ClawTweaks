// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace LegionTweaks
{
    // Owns one read-only native context. Lifecycle/retry policy is in AmdTelemetry.
    internal interface IAmdSession : IDisposable
    {
        Reading[] Read(out int? queryStatus, out bool hasPmLogValues);
    }

    internal sealed class AmdSession : IAmdSession
    {
        private IntPtr context;
        private int adapter;
        private string name;
        internal AmdSession()
        {
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
                    var chosen = AmdTelemetry.SelectAdapter(items);
                    adapter = chosen.Index; name = chosen.Name;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception ex)
            {
                Dispose();
                throw new InvalidOperationException("Telemetría AMD no disponible: " + ex.Message);
            }
        }

        public Reading[] Read(out int? queryStatus, out bool hasPmLogValues)
        {
            var log = new AmdNative.Log { Size = Marshal.SizeOf(typeof(AmdNative.Log)), Sensors = new AmdNative.Sensor[AmdNative.SensorCount] };
            queryStatus = null;
            try { queryStatus = AmdNative.Query(context, adapter, ref log); }
            catch (EntryPointNotFoundException) { }
            var readings = AmdTelemetry.Decode(queryStatus == 0 ? log.Sensors : null, "AMD ADL PMLog · " + name);
            hasPmLogValues = readings.Any(x => x.Value.HasValue);
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
            return readings;
        }
        private static void Replace(Reading[] readings, Reading candidate)
        {
            int index = Array.FindIndex(readings, x => x.Key == candidate.Key);
            if (index >= 0 && !readings[index].Value.HasValue && candidate.Value.HasValue) readings[index] = candidate;
        }
        public void Dispose()
        {
            if (context == IntPtr.Zero) return;
            try { AmdNative.Destroy(context); } catch (Exception) { }
            context = IntPtr.Zero;
        }
    }
}
