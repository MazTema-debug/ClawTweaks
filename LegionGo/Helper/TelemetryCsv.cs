// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegionTweaks
{
    internal static class TelemetryCsv
    {
        // Raw integer values retain their units (including millivolts); unavailable is empty, never zero.
        internal static readonly string[] Keys = { "Mode", "Spl", "Sppt", "Fppt", "FanFullSpeed", "ChargeLimit", "PowerLight",
            "CpuBoostDC", "CpuBoostAC", "EppDC", "EppAC", "OsPowerMode", "CpuTemperature", "GpuTemperature",
            "GpuVoltageMv", "SocVoltageMv", "GpuClockMHz", "GpuUsage", "GpuPower", "AsicPower", "CpuPower", "FanRpm", "Battery" };
        internal static readonly string[] SettingKeys = Keys.Take(12).ToArray();
        private static readonly string[] AmdKeys = { "GpuTemperature", "GpuVoltageMv", "SocVoltageMv", "GpuClockMHz", "GpuUsage", "GpuPower", "AsicPower", "CpuPower" };
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string AppVersion = typeof(TelemetryCsv).Assembly.GetName().Version.ToString();

        internal static string Header()
        {
            var cells = new List<string> { "schema_version", "app_version", "row_type", "recorded_at_utc", "captured_at_utc",
                "elapsed_s", "gap_s", "events", "on_ac", "mode_name", "custom_mode" };
            foreach (string key in Keys) cells.AddRange(new[] { key, key + "_unit", key + "_source", key + "_error" });
            foreach (string key in AmdKeys) cells.AddRange(new[] { key + "_sensor_id", key + "_raw", key + "_supported", key + "_diagnostic" });
            cells.Add("power_note");
            cells.Add("sample_error");
            return string.Join(",", cells);
        }

        internal static string Row(string type, DateTime now, DateTime start, double gap, string events, Snapshot snapshot, string error)
        {
            var mode = TelemetryValue.Value(snapshot, "Mode");
            bool knownMode = mode == 1 || mode == 2 || mode == 3 || mode == 255;
            var cells = new List<string> { "2", AppVersion, Cell(type), now.ToString("O", Invariant), Cell(snapshot?.CapturedAtUtc),
                Math.Max(0, (now - start).TotalSeconds).ToString("0.000", Invariant), Math.Max(0, gap).ToString("0.000", Invariant), Cell(events),
                snapshot == null ? "" : (snapshot.OnAc ? "1" : "0"), snapshot == null ? "" : Cell(TelemetryValue.ModeName(mode)),
                !knownMode ? "" : (mode == 255 ? "1" : "0") };
            foreach (string key in Keys)
            {
                var reading = snapshot?.Values?.FirstOrDefault(x => x.Key == key);
                cells.Add(reading?.Value != null && reading.Error == null ? reading.Value.Value.ToString(Invariant) : "");
                cells.Add(Cell(reading?.Unit ?? DefaultUnit(key)));
                cells.Add(Cell(reading?.Source));
                cells.Add(Cell(snapshot == null ? null : reading?.Error ?? (reading?.Value == null ? "No disponible" : null)));
            }
            foreach (string key in AmdKeys)
            {
                var reading = snapshot?.Values?.FirstOrDefault(x => x.Key == key);
                cells.Add(reading?.SensorId?.ToString(Invariant) ?? "");
                cells.Add(reading?.RawValue?.ToString(Invariant) ?? "");
                cells.Add(reading?.SensorSupported?.ToString(Invariant) ?? "");
                cells.Add(Cell(reading?.Diagnostic));
            }
            cells.Add(Cell(TelemetryValue.PowerNote(snapshot)));
            string extraErrors = string.Join("; ", (snapshot?.Values ?? new Reading[0])
                .Where(x => x.Error != null && !Keys.Contains(x.Key)).Select(x => x.Key + ": " + x.Error));
            cells.Add(Cell(error ?? extraErrors));
            return string.Join(",", cells);
        }

        private static string DefaultUnit(string key)
        {
            if (key == "Spl" || key == "Sppt" || key == "Fppt" || key.EndsWith("Power", StringComparison.Ordinal)) return "W";
            if (key.EndsWith("Temperature", StringComparison.Ordinal)) return "°C";
            if (key.EndsWith("VoltageMv", StringComparison.Ordinal)) return "mV";
            if (key == "GpuClockMHz") return "MHz";
            if (key == "FanRpm") return "RPM";
            if (key == "Battery" || key == "GpuUsage") return "%";
            return "";
        }

        internal static string Cell(string value)
        {
            value = value ?? "";
            if (value.Length > 512) value = value.Substring(0, 512);
            // Driver/error text must remain text when opened in a spreadsheet.
            string trimmed = value.TrimStart();
            if (trimmed.Length > 0 && ("=+-@".IndexOf(trimmed[0]) >= 0 || value[0] == '\t' || value[0] == '\r' || value[0] == '\n')) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
