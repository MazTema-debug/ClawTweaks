// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;

namespace LegionTweaks
{
    // Presentation only. The publisher must establish sample freshness before
    // calling Format. RTSS resolves the two fixed macros for each hooked game;
    // neither FPS nor frame time is derived from the helper sampling interval.
    public static class RtssOverlayText
    {
        public const int MaximumBytes = 4095;
        public const string Unavailable = "LEGIONTWEAKS\nTelemetria N/D";

        public static string Format(Snapshot snapshot)
        {
            if (snapshot?.Supported != true || snapshot.Values == null) return Unavailable;
            string cpuTemperature = Number(snapshot, "CpuTemperature", "°C", 1, 150);
            string gpuTemperature = Number(snapshot, "GpuTemperature", "°C", 1, 150);
            string cpuPower = Number(snapshot, "CpuPower", "W", 0, 200);
            string gpuPower = Number(snapshot, "GpuPower", "W", 0, 200);
            string asicPower = Number(snapshot, "AsicPower", "W", 0, 200);
            string gpuLoad = Number(snapshot, "GpuUsage", "%", 0, 100);
            string gpuClock = Number(snapshot, "GpuClockMHz", "MHz", 0, 6000);
            string gpuVoltage = Number(snapshot, "GpuVoltageMv", "mV", 1, 2500, divisor: 1000, format: "0.000");
            string socVoltage = Number(snapshot, "SocVoltageMv", "mV", 1, 2500, divisor: 1000, format: "0.000");
            string fan = Number(snapshot, "FanRpm", "RPM", 0, 15000);
            // These four existing Snapshot fields predate explicit units. An
            // absent unit is accepted only for their documented fixed units;
            // a supplied but different unit is always rejected.
            string battery = Number(snapshot, "Battery", "%", 0, 100, allowAbsentUnit: true);
            string spl = Number(snapshot, "Spl", "W", 1, 200, allowAbsentUnit: true);
            string sppt = Number(snapshot, "Sppt", "W", 1, 200, allowAbsentUnit: true);
            string fppt = Number(snapshot, "Fppt", "W", 1, 200, allowAbsentUnit: true);
            int? mode = Validated(snapshot, "Mode", null, 1, 255, true);
            string limits = mode == 255 ? "LIMITES custom" : mode == 1 || mode == 2 || mode == 3
                ? "LIMITES guardados (custom inactivo)" : "LIMITES (modo N/D)";

            // Every nonnumeric character is fixed here. Device metadata, source,
            // errors, raw values and profile names never enter RTSS markup.
            string text = "LEGIONTWEAKS | <FR> FPS | <FT> ms | lectura " + CaptureTime(snapshot.CapturedAtUtc)
                + "\nCPU " + cpuTemperature + " C | " + cpuPower + " W"
                + "\nGPU " + gpuTemperature + " C | " + gpuLoad + " % | " + gpuClock + " MHz"
                + "\nGFX " + gpuPower + " W | " + gpuVoltage + " V | ASIC " + asicPower + " W | SoC " + socVoltage + " V"
                + "\nFAN " + fan + " RPM | BAT " + battery + " % " + (snapshot.OnAc ? "AC" : "DC")
                + "\n" + limits + " SPL/SPPT/FPPT " + spl + "/" + sppt + "/" + fppt + " W";
            // ASCII only and fixed numeric bounds make this comfortably smaller
            // than RTSS's extended slot. Keep a fail-closed final size guard.
            return text.Length <= MaximumBytes ? text : Unavailable;
        }

        private static string CaptureTime(string timestamp)
        {
            DateTimeOffset captured;
            if (string.IsNullOrEmpty(timestamp) || timestamp.Length > 33
                || !(timestamp.EndsWith("Z", StringComparison.Ordinal) || timestamp.EndsWith("+00:00", StringComparison.Ordinal)
                    || timestamp.EndsWith("-00:00", StringComparison.Ordinal))
                || !DateTimeOffset.TryParseExact(timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out captured)
                || captured.Offset != TimeSpan.Zero) return "N/D";
            return captured.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        }

        private static string Number(Snapshot snapshot, string key, string unit, int minimum, int maximum,
            bool allowAbsentUnit = false, double divisor = 1, string format = "0")
        {
            int? value = Validated(snapshot, key, unit, minimum, maximum, allowAbsentUnit);
            return value.HasValue ? (value.Value / divisor).ToString(format, CultureInfo.InvariantCulture) : "N/D";
        }

        private static int? Validated(Snapshot snapshot, string key, string unit, int minimum, int maximum, bool allowAbsentUnit)
        {
            Reading match = null;
            foreach (Reading reading in snapshot.Values)
            {
                if (reading == null || !string.Equals(reading.Key, key, StringComparison.Ordinal)) continue;
                if (match != null) return null; // Ambiguous duplicate fields are not a reading.
                match = reading;
            }
            if (match?.Value == null || match.Error != null || match.SensorSupported == 0
                || match.Value < minimum || match.Value > maximum) return null;
            if (!string.Equals(match.Unit, unit, StringComparison.Ordinal)
                && !(allowAbsentUnit && match.Unit == null)) return null;
            return match.Value;
        }
    }
}
