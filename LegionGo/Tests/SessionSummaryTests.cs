// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LegionTweaks;

internal static partial class Program
{
    private static void SessionSummaryTests()
    {
        Test("Session summary accepts schemas 1 and 2, BOM, CSV quotes and culture-independent valid zero", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                foreach (int schema in new[] { 1, 2 })
                {
                    var first = SummaryRow("GpuPower", "0", "GpuPower_unit", "W", "GpuPower_source", "AMD, \"GFX\" 🌡",
                        "sample_error", "'=FAKE(\"text\")\nsecond line");
                    var second = SummaryRow("GpuPower", "9", "GpuPower_unit", "W", "GpuPower_source", "AMD, \"GFX\" 🌡");
                    var result = SessionSummary.Read(new StringReader("\ufeff" + SummaryCsv(schema, first, second)));
                    var metric = result.Metrics.Single();
                    Assert(result.SchemaVersion == schema && result.SampleRows == 2 && result.RowsWithSampleError == 1, "schema or quoted text lost");
                    Assert(metric.Minimum == 0 && metric.Maximum == 9 && metric.Mean == 4.5 && metric.Samples == 2, "valid zero or invariant mean lost");
                    Assert(metric.Source == "AMD, \"GFX\" 🌡" && SessionSummary.Describe(result).Contains("4.5 W"), "source or output culture changed");
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("Session summary keeps AMD domains, source transitions and sensor identities separate", () =>
        {
            var result = SessionSummary.Read(new StringReader(SummaryCsv(2,
                SummaryRow("GpuPower", "28", "GpuPower_unit", "W", "GpuPower_source", "AMD GFX", "GpuPower_sensor_id", "30", "GpuPower_supported", "1",
                    "AsicPower", "15", "AsicPower_unit", "W", "AsicPower_source", "AMD ASIC", "AsicPower_sensor_id", "23", "AsicPower_supported", "1"),
                SummaryRow("GpuPower", "7", "GpuPower_unit", "W", "GpuPower_source", "AMD fallback"),
                SummaryRow("GpuPower", "6", "GpuPower_unit", "W", "GpuPower_source", "AMD GFX", "GpuPower_sensor_id", "31", "GpuPower_supported", "1"))));
            Assert(result.Metrics.Count == 4 && result.Metrics.All(x => x.Samples == 1), "domains, sources or sensor IDs were merged");
            Assert(result.Metrics.Single(x => x.Key == "AsicPower").Mean == 15, "ASIC normalized or combined with GPU");
            Assert(!result.Metrics.Any(x => x.Key == "total"), "invented combined power");
        });
        Test("Session summary excludes raw unsupported errored malformed wrong-unit and missing readings", () =>
        {
            var result = SessionSummary.Read(new StringReader(SummaryCsv(2,
                SummaryRow("GpuPower_raw", "900", "GpuPower_supported", "0", "GpuPower_unit", "W"),
                SummaryRow("GpuPower", "900", "GpuPower_supported", "0", "GpuPower_unit", "W"),
                SummaryRow("GpuPower", "12", "GpuPower_error", "failed", "GpuPower_unit", "W"),
                SummaryRow("GpuPower", "12", "GpuPower_unit", "mW"),
                SummaryRow("GpuPower", "NaN", "GpuPower_unit", "W"),
                SummaryRow("GpuPower", "1.5", "GpuPower_unit", "W"),
                SummaryRow("GpuPower", "12", "GpuPower_unit", "W", "GpuPower_sensor_id", "30"),
                SummaryRow("GpuPower", "12", "GpuPower_unit", "W", "GpuPower_sensor_id", "256", "GpuPower_supported", "1"),
                SummaryRow("GpuPower", "0", "GpuPower_unit", "W"))));
            Assert(result.Metrics.Single().Samples == 1 && result.Metrics.Single().Mean == 0, "raw, invalid or missing values promoted");
            Assert(result.RejectedMetricValues == 7 && result.MissingMetricValues > 0, "rejections and absence not distinguished");
        });
        Test("Session summary excludes stale future invalid-time error and end rows from metrics", () =>
        {
            var rows = new[] {
                SummaryRow("GpuPower", "1", "GpuPower_unit", "W"),
                SummaryRow("GpuPower", "2", "GpuPower_unit", "W", "captured_at_utc", "2026-10-16T00:00:06.0000000Z"),
                SummaryRow("GpuPower", "3", "GpuPower_unit", "W", "captured_at_utc", "2026-10-15T23:59:58.0000000Z"),
                SummaryRow("GpuPower", "99", "GpuPower_unit", "W", "captured_at_utc", "2026-10-15T23:59:53.0000000Z"),
                SummaryRow("GpuPower", "99", "GpuPower_unit", "W", "captured_at_utc", "2026-10-16T00:00:03.0000000Z"),
                SummaryRow("GpuPower", "99", "GpuPower_unit", "W", "recorded_at_utc", "2026-10-16T00:00:00"),
                SummaryRow("GpuPower", "99", "GpuPower_unit", "W", "captured_at_utc", "invalid"),
                SummaryRow("GpuPower", "99", "GpuPower_unit", "W", "row_type", "error"),
                SummaryRow("GpuPower", "99", "GpuPower_unit", "W", "row_type", "end") };
            // Boundary ages: recorded minus captured = +6 and -2 are valid.
            rows[1]["captured_at_utc"] = "2026-10-15T23:59:54.0000000Z";
            rows[2]["captured_at_utc"] = "2026-10-16T00:00:02.0000000Z";
            var result = SessionSummary.Read(new StringReader(SummaryCsv(2, rows)));
            Assert(result.Metrics.Single().Samples == 3 && result.Metrics.Single().Mean == 2, "freshness boundaries or row types wrong");
            Assert(result.StaleSamples == 2 && result.InvalidTimestamps == 2 && result.ErrorRows == 1 && result.EndRows == 1, "excluded rows not counted");
        });
        Test("Session summary rejects out-of-range sensor values without clamping and retains supported boundaries", () =>
        {
            var result = SessionSummary.Read(new StringReader(SummaryCsv(2,
                SummaryRow("Battery", "500", "Battery_unit", "%", "GpuUsage", "-1", "GpuUsage_unit", "%",
                    "CpuTemperature", "0", "CpuTemperature_unit", "°C", "GpuTemperature", "65535", "GpuTemperature_unit", "°C",
                    "GpuVoltageMv", "2501", "GpuVoltageMv_unit", "mV", "FanRpm", "15001", "FanRpm_unit", "RPM"),
                SummaryRow("Battery", "0", "Battery_unit", "%", "GpuUsage", "100", "GpuUsage_unit", "%",
                    "CpuTemperature", "150", "CpuTemperature_unit", "°C", "GpuTemperature", "1", "GpuTemperature_unit", "°C",
                    "GpuVoltageMv", "2500", "GpuVoltageMv_unit", "mV", "FanRpm", "0", "FanRpm_unit", "RPM",
                    "GpuPower", "200", "GpuPower_unit", "W", "GpuPower_sensor_id", "30", "GpuPower_supported", "2"))));
            Assert(result.RejectedMetricValues == 6 && result.Metrics.Count == 7 && result.Metrics.All(x => x.Samples == 1), "range rejection clamped values or erased boundaries");
            Assert(result.Metrics.Single(x => x.Key == "GpuPower").Mean == 200, "nonzero backend-supported flag rejected");
        });
        Test("Session summary reports observed gaps clock reversals and incomplete sessions without filling rows", () =>
        {
            var result = SessionSummary.Read(new StringReader(SummaryCsv(1,
                SummaryRow("recorded_at_utc", "2026-10-16T00:00:10.0000000Z", "captured_at_utc", "2026-10-16T00:00:10.0000000Z", "gap_s", "10.000", "events", "gap"),
                SummaryRow("recorded_at_utc", "2026-10-16T00:00:05.0000000Z", "captured_at_utc", "2026-10-16T00:00:05.0000000Z", "events", "clock_changed"),
                SummaryRow("recorded_at_utc", "2026-10-16T00:00:07.0000000Z", "captured_at_utc", "2026-10-16T00:00:07.0000000Z", "gap_s", "2.000"))));
            Assert(result.SampleRows == 3 && result.GapRows == 1 && result.LargestGapSeconds == 10 && result.ClockReversals == 1, "gaps or reversals lost");
            Assert(result.FirstRecordedUtc.Value.Second == 5 && result.LastRecordedUtc.Value.Second == 10, "UTC span corrupted by reversal");
            string description = SessionSummary.Describe(result);
            Assert(description.Contains("incompleto") && description.Contains("sin ponderación temporal") && description.Contains("no demuestran suspensión"), "limits not described");
        });
        Test("Session summary rejects unknown mixed duplicate malformed and incomplete CSV structures", () =>
        {
            string valid = SummaryCsv(2, SummaryRow());
            SummaryReject("", "vacío");
            SummaryReject(TelemetryCsv.Header(), "ninguna fila");
            SummaryReject(valid.Replace("schema_version,app_version", "schema_version,schema_version"), "repetidas");
            SummaryReject(valid.Replace("schema_version,app_version", "schema_version,unknown_version"), "Cabecera");
            SummaryReject(SummaryCsv(2, SummaryRow("schema_version", "3")), "esquema");
            SummaryReject(SummaryCsv(2, SummaryRow(), SummaryRow("schema_version", "1")), "mezclada");
            SummaryReject(SummaryCsv(2, SummaryRow("row_type", "future")), "Tipo de fila");
            SummaryReject(SummaryCsv(2, SummaryRow("gap_s", "NaN")), "gap_s");
            SummaryReject(valid + "\"unfinished", "comillas");
            SummaryReject(valid + "a\"b", "Comillas");
            SummaryReject(valid + "\"a\"x", "inesperado");
            SummaryReject(valid + "a,b\n", "cabecera");
            SummaryReject(valid + new string('a', 1025), "1024");
        });
        Test("Session summary accepts reordered known headers and LF CRLF CR record separators", () =>
        {
            foreach (string newline in new[] { "\n", "\r\n", "\r" })
            {
                var source = ParseCsv(SummaryCsv(1, SummaryRow("GpuPower", "7", "GpuPower_unit", "W")));
                var order = Enumerable.Range(0, source[0].Length).Reverse().ToArray();
                string csv = string.Join(newline, source.Select(row => string.Join(",", order.Select(i => TelemetryCsv.Cell(row[i]))))) + newline;
                var result = SessionSummary.Read(new StringReader(csv));
                Assert(result.Metrics.Single().Mean == 7, "header order or line endings changed reading");
            }
        });
        Test("Session summary streams through Read without ReadToEnd and honors cancellation", () =>
        {
            string valid = SummaryCsv(2, SummaryRow("GpuPower", "5", "GpuPower_unit", "W"));
            using (var reader = new SummaryStreamingReader(valid))
                Assert(SessionSummary.Read(reader).Metrics.Single().Mean == 5, "streaming reader failed");
            using (var cancellation = new CancellationTokenSource())
            using (var reader = new SummaryStreamingReader(valid, () => cancellation.Cancel(), 25))
            {
                bool stopped = false;
                try { SessionSummary.Read(reader, cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
                Assert(stopped && reader.Characters < valid.Length, "cancellation waited for full input");
            }
        });
        Test("Session summary limits distinct sensor series instead of retaining unbounded source text", () =>
        {
            var rows = Enumerable.Range(0, SessionSummary.MaximumSeries + 1).Select(i =>
                SummaryRow("GpuPower", "1", "GpuPower_unit", "W", "GpuPower_source", "synthetic " + i)).ToArray();
            SummaryReject(SummaryCsv(2, rows), "series");
        });
        Test("Session summary enforces its UTF-8 byte budget during incremental reading", () =>
        {
            string csv = SummaryCsv(2, SummaryRow("sample_error", new string('ñ', 1000)));
            int separator = csv.IndexOf('\n');
            string header = csv.Substring(0, separator + 1), row = csv.Substring(separator + 1);
            using (var reader = new SummaryRepeatingReader(header, row))
            {
                bool stopped = false;
                try { SessionSummary.Read(reader); }
                catch (InvalidDataException ex) { stopped = ex.Message.Contains("64 MiB"); }
                Assert(stopped && reader.Characters < SessionSummary.MaximumBytes, "UTF-8 bytes were replaced by UTF-16 character count");
            }
        });
    }

    private static Dictionary<string, string> SummaryRow(params string[] changes)
    {
        var row = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "row_type", "sample" }, { "recorded_at_utc", "2026-10-16T00:00:00.0000000Z" },
            { "captured_at_utc", "2026-10-16T00:00:00.0000000Z" }, { "gap_s", "0.000" }, { "elapsed_s", "0.000" } };
        for (int i = 0; i < changes.Length; i += 2) row[changes[i]] = changes[i + 1];
        return row;
    }
    private static string SummaryCsv(int schema, params Dictionary<string, string>[] rows)
    {
        var headers = TelemetryCsv.Header().Split(',').Where(name => schema == 2 || name != "power_note"
            && !name.EndsWith("_sensor_id", StringComparison.Ordinal) && !name.EndsWith("_raw", StringComparison.Ordinal)
            && !name.EndsWith("_supported", StringComparison.Ordinal) && !name.EndsWith("_diagnostic", StringComparison.Ordinal)).ToArray();
        var csv = new StringBuilder(string.Join(",", headers) + "\n");
        foreach (var row in rows)
        {
            string version;
            if (!row.TryGetValue("schema_version", out version)) row["schema_version"] = schema.ToString(CultureInfo.InvariantCulture);
            csv.Append(string.Join(",", headers.Select(name => { string value; return TelemetryCsv.Cell(row.TryGetValue(name, out value) ? value : ""); }))).Append('\n');
        }
        return csv.ToString();
    }
    private static void SummaryReject(string csv, string message)
    {
        bool rejected = false;
        try { SessionSummary.Read(new StringReader(csv)); }
        catch (InvalidDataException ex) { rejected = ex.Message.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0; }
        Assert(rejected, "CSV was accepted or wrong error, expected: " + message);
    }
    private sealed class SummaryStreamingReader : TextReader
    {
        private readonly string text;
        private readonly Action callback;
        private readonly int callbackAt;
        internal int Characters;
        internal SummaryStreamingReader(string text, Action callback = null, int callbackAt = 0) { this.text = text; this.callback = callback; this.callbackAt = callbackAt; }
        public override int Read()
        {
            if (Characters == callbackAt) callback?.Invoke();
            return Characters < text.Length ? text[Characters++] : -1;
        }
        public override string ReadToEnd() => throw new Exception("whole-file read forbidden");
        public override string ReadLine() => throw new Exception("line splitting cannot parse quoted CSV");
    }
    private sealed class SummaryRepeatingReader : TextReader
    {
        private readonly string header, row;
        internal long Characters;
        internal SummaryRepeatingReader(string header, string row) { this.header = header; this.row = row; }
        public override int Read()
        {
            long at = Characters++;
            return at < header.Length ? header[(int)at] : row[(int)((at - header.Length) % row.Length)];
        }
        public override string ReadToEnd() => throw new Exception("whole-file read forbidden");
    }
}
