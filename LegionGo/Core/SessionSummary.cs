// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    public sealed class SessionMetricSummary
    {
        public string Key { get; internal set; }
        public string Label { get; internal set; }
        public string Unit { get; internal set; }
        public string Source { get; internal set; }
        public int? SensorId { get; internal set; }
        public int Samples { get; internal set; }
        public int Minimum { get; internal set; }
        public int Maximum { get; internal set; }
        public double Mean { get; internal set; }
        internal void Add(int value)
        {
            if (Samples == 0) Minimum = Maximum = value;
            else { Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value); }
            Samples++; Mean += (value - Mean) / Samples;
        }
    }

    public sealed class SessionSummaryReport
    {
        public int SchemaVersion { get; internal set; }
        public int SampleRows { get; internal set; }
        public int ErrorRows { get; internal set; }
        public int EndRows { get; internal set; }
        public int InvalidTimestamps { get; internal set; }
        public int StaleSamples { get; internal set; }
        public int ClockReversals { get; internal set; }
        public int GapRows { get; internal set; }
        public double LargestGapSeconds { get; internal set; }
        public int PartialSampleRows { get; internal set; }
        public int MissingMetricValues { get; internal set; }
        public int RejectedMetricValues { get; internal set; }
        public int RowsWithSampleError { get; internal set; }
        public DateTimeOffset? FirstRecordedUtc { get; internal set; }
        public DateTimeOffset? LastRecordedUtc { get; internal set; }
        public IReadOnlyList<SessionMetricSummary> Metrics { get; internal set; }
    }

    // An explicit file analysis only. No device reads, interpolation, power-domain
    // arithmetic, setting averages or raw diagnostic promotion occur here.
    public static class SessionSummary
    {
        public const long MaximumBytes = 64L * 1024 * 1024;
        public const int MaximumRows = 200000;
        public const int MaximumSeries = 128;
        public const int MaximumSeconds = 30;
        private const int MaximumColumns = 160, MaximumCellCharacters = 1024, MaximumRowCharacters = 131072;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static readonly string[] SettingKeys = { "Mode", "Spl", "Sppt", "Fppt", "FanFullSpeed", "ChargeLimit", "PowerLight",
            "CpuBoostDC", "CpuBoostAC", "EppDC", "EppAC", "OsPowerMode" };
        private static readonly MetricDefinition[] Definitions = {
            new MetricDefinition("CpuTemperature", "Temperatura CPU", "°C", 1, 150),
            new MetricDefinition("GpuTemperature", "Temperatura GPU", "°C", 1, 150),
            new MetricDefinition("GpuVoltageMv", "Voltaje GPU", "mV", 1, 2500),
            new MetricDefinition("SocVoltageMv", "Voltaje SoC", "mV", 1, 2500),
            new MetricDefinition("GpuClockMHz", "Frecuencia GPU", "MHz", 0, 6000),
            new MetricDefinition("GpuUsage", "Uso GPU", "%", 0, 100),
            new MetricDefinition("GpuPower", "Potencia GPU (GFX)", "W", 0, 200),
            new MetricDefinition("AsicPower", "Potencia ASIC", "W", 0, 200),
            new MetricDefinition("CpuPower", "Potencia CPU", "W", 0, 200),
            new MetricDefinition("FanRpm", "Ventilador", "RPM", 0, 15000),
            new MetricDefinition("Battery", "Batería", "%", 0, 100) };
        private static readonly string[] AmdKeys = { "GpuTemperature", "GpuVoltageMv", "SocVoltageMv", "GpuClockMHz", "GpuUsage", "GpuPower", "AsicPower", "CpuPower" };

        public static SessionSummaryReport Read(TextReader reader, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            var csv = new CsvReader(reader, cancellationToken);
            string[] header = csv.Next();
            if (header == null) throw new InvalidDataException("El archivo CSV está vacío.");
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < header.Length; i++)
            {
                if (header[i].Length == 0 || columns.ContainsKey(header[i])) throw new InvalidDataException("La cabecera CSV contiene columnas vacías o repetidas.");
                columns.Add(header[i], i);
            }
            int schema = HeaderMatches(columns, 1) ? 1 : HeaderMatches(columns, 2) ? 2 : 0;
            if (schema == 0) throw new InvalidDataException("Cabecera no compatible. Se admiten los esquemas CSV 1 y 2 de LegionTweaks.");
            var result = new SessionSummaryReport { SchemaVersion = schema };
            var series = new List<SessionMetricSummary>();
            DateTimeOffset? previous = null;
            int rows = 0;
            string[] row;
            while ((row = csv.Next()) != null)
            {
                if (++rows > MaximumRows) throw new InvalidDataException("El CSV supera el límite de " + MaximumRows + " filas.");
                if (row.Length != header.Length) throw new InvalidDataException("La fila CSV " + (rows + 1) + " no coincide con la cabecera.");
                if (Cell(row, columns, "schema_version") != schema.ToString(Invariant))
                    throw new InvalidDataException("Versión de esquema desconocida o mezclada en la fila " + (rows + 1) + ". Solo se admiten 1 y 2 por archivo.");
                string type = Cell(row, columns, "row_type");
                if (type == "sample") result.SampleRows++;
                else if (type == "error") result.ErrorRows++;
                else if (type == "end") result.EndRows++;
                else throw new InvalidDataException("Tipo de fila desconocido en la fila " + (rows + 1) + ".");
                DateTimeOffset recorded;
                bool recordedValid = TryUtc(Cell(row, columns, "recorded_at_utc"), out recorded);
                if (recordedValid)
                {
                    if (previous.HasValue && recorded < previous.Value) result.ClockReversals++;
                    previous = recorded;
                    if (!result.FirstRecordedUtc.HasValue || recorded < result.FirstRecordedUtc) result.FirstRecordedUtc = recorded;
                    if (!result.LastRecordedUtc.HasValue || recorded > result.LastRecordedUtc) result.LastRecordedUtc = recorded;
                }
                double gap;
                string gapText = Cell(row, columns, "gap_s");
                bool gapValid = double.TryParse(gapText, NumberStyles.AllowDecimalPoint, Invariant, out gap)
                    && !double.IsNaN(gap) && !double.IsInfinity(gap) && gap >= 0 && gap <= TimeSpan.MaxValue.TotalSeconds;
                if (!gapValid) throw new InvalidDataException("Intervalo gap_s inválido en la fila " + (rows + 1) + ".");
                if (gap > 6 || HasEvent(Cell(row, columns, "events"), "gap"))
                { result.GapRows++; result.LargestGapSeconds = Math.Max(result.LargestGapSeconds, gap); }
                if (Cell(row, columns, "sample_error").Length != 0) result.RowsWithSampleError++;
                if (type != "sample") { if (!recordedValid) result.InvalidTimestamps++; continue; }
                DateTimeOffset captured;
                if (!recordedValid || !TryUtc(Cell(row, columns, "captured_at_utc"), out captured))
                { result.InvalidTimestamps++; continue; }
                double age = (recorded - captured).TotalSeconds;
                if (age > 6 || age < -2) { result.StaleSamples++; continue; }
                bool partial = false;
                foreach (var definition in Definitions)
                {
                    string raw = Cell(row, columns, definition.Key), error = Cell(row, columns, definition.Key + "_error");
                    if (raw.Length == 0) { result.MissingMetricValues++; partial = true; continue; }
                    int value;
                    string unit = Cell(row, columns, definition.Key + "_unit");
                    string source = Cell(row, columns, definition.Key + "_source");
                    int? sensor = null;
                    bool valid = int.TryParse(raw, NumberStyles.AllowLeadingSign, Invariant, out value)
                        && error.Length == 0 && unit == definition.Unit && value >= definition.Minimum && value <= definition.Maximum;
                    // Schema 2 may retain an unsupported raw PMLog value. It must
                    // never become a regular reading merely because it is numeric.
                    if (schema == 2 && Array.IndexOf(AmdKeys, definition.Key) >= 0)
                    {
                        string supported = Cell(row, columns, definition.Key + "_supported");
                        string id = Cell(row, columns, definition.Key + "_sensor_id");
                        int parsed;
                        if (id.Length != 0)
                        {
                            if (!int.TryParse(id, NumberStyles.None, Invariant, out parsed) || parsed < 0 || parsed > 255) valid = false;
                            else sensor = parsed;
                        }
                        int supportedValue;
                        bool supportedValid = int.TryParse(supported, NumberStyles.AllowLeadingSign, Invariant, out supportedValue) && supportedValue != 0;
                        if (supported.Length != 0 && !supportedValid) valid = false;
                        if (id.Length != 0 && !supportedValid) valid = false;
                    }
                    if (!valid) { result.RejectedMetricValues++; partial = true; continue; }
                    // Source and sensor identity are part of the grouping: EDGE,
                    // GFX and OD5 transitions cannot silently become one average.
                    var aggregate = series.FirstOrDefault(x => x.Key == definition.Key && x.Unit == unit && x.Source == source && x.SensorId == sensor);
                    if (aggregate == null)
                    {
                        if (series.Count == MaximumSeries) throw new InvalidDataException("El CSV contiene demasiadas series o fuentes distintas (máximo 128).");
                        aggregate = new SessionMetricSummary { Key = definition.Key, Label = definition.Label, Unit = unit, Source = source, SensorId = sensor };
                        series.Add(aggregate);
                    }
                    aggregate.Add(value);
                }
                if (partial) result.PartialSampleRows++;
            }
            if (rows == 0) throw new InvalidDataException("El CSV contiene la cabecera, pero ninguna fila de sesión.");
            result.Metrics = series.AsReadOnly();
            return result;
        }

        public static string Describe(SessionSummaryReport report)
        {
            if (report == null) return "Selecciona un CSV de LegionTweaks para resumirlo localmente.";
            var text = new StringBuilder("CSV de LegionTweaks · esquema " + report.SchemaVersion + "\n");
            text.Append("Muestras: ").Append(report.SampleRows).Append(" · filas de error: ").Append(report.ErrorRows)
                .Append(" · fin registrado: ").Append(report.EndRows > 0 ? "sí" : "no (archivo abierto o incompleto)").Append('\n');
            if (report.FirstRecordedUtc.HasValue && report.LastRecordedUtc.HasValue)
                text.Append("Intervalo registrado UTC: ").Append(report.FirstRecordedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", Invariant))
                    .Append(" a ").Append(report.LastRecordedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", Invariant)).Append('\n');
            text.Append("Huecos observados: ").Append(report.GapRows).Append(" · mayor intervalo: ")
                .Append(report.GapRows == 0 ? "no observado" : report.LargestGapSeconds.ToString("0.###", Invariant) + " s").Append('\n')
                .Append("Fechas inválidas: ").Append(report.InvalidTimestamps).Append(" · muestras vencidas/futuras excluidas: ").Append(report.StaleSamples)
                .Append(" · retrocesos del reloj: ").Append(report.ClockReversals).Append('\n')
                .Append("Muestras parciales: ").Append(report.PartialSampleRows).Append(" · valores ausentes: ").Append(report.MissingMetricValues)
                .Append(" · valores rechazados: ").Append(report.RejectedMetricValues).Append(" · filas con error adicional: ").Append(report.RowsWithSampleError).Append("\n\n");
            if (report.Metrics.Count == 0) text.Append("No hay métricas válidas para resumir.\n");
            foreach (var metric in report.Metrics)
            {
                text.Append(metric.Label).Append(" · ").Append(metric.Source.Length == 0 ? "fuente no indicada" : metric.Source);
                if (metric.SensorId.HasValue) text.Append(" · sensor ").Append(metric.SensorId.Value);
                text.Append("\n  mín ").Append(metric.Minimum).Append(" · máx ").Append(metric.Maximum).Append(" · media ")
                    .Append(metric.Mean.ToString("0.##", Invariant)).Append(' ').Append(metric.Unit).Append(" · n=").Append(metric.Samples).Append('\n');
            }
            text.Append("\nMedia por muestras válidas, sin ponderación temporal ni interpolación. No se suman dominios AMD ni se calcula energía, autonomía o FPS. Los huecos no demuestran suspensión.");
            return text.ToString();
        }

        private static bool TryUtc(string text, out DateTimeOffset value)
        {
            value = default(DateTimeOffset);
            // A timestamp without an explicit UTC designator must not inherit the
            // reviewing computer's local timezone.
            return text.EndsWith("Z", StringComparison.Ordinal) && DateTimeOffset.TryParseExact(text,
                new[] { "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'" }, Invariant,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
        }
        private static bool HasEvent(string events, string value) => events.Split(';').Any(x => x == value);
        private static string Cell(string[] row, Dictionary<string, int> columns, string name) => row[columns[name]];
        private static bool HeaderMatches(Dictionary<string, int> columns, int schema)
        {
            var expected = new HashSet<string>(new[] { "schema_version", "app_version", "row_type", "recorded_at_utc", "captured_at_utc",
                "elapsed_s", "gap_s", "events", "on_ac", "mode_name", "custom_mode", "sample_error" }, StringComparer.Ordinal);
            foreach (string key in SettingKeys.Concat(Definitions.Select(x => x.Key)))
                foreach (string suffix in new[] { "", "_unit", "_source", "_error" }) expected.Add(key + suffix);
            if (schema == 2)
            {
                foreach (string key in AmdKeys)
                    foreach (string suffix in new[] { "_sensor_id", "_raw", "_supported", "_diagnostic" }) expected.Add(key + suffix);
                expected.Add("power_note");
            }
            return expected.Count == columns.Count && expected.All(columns.ContainsKey);
        }
        private sealed class MetricDefinition
        {
            internal readonly string Key, Label, Unit;
            internal readonly int Minimum, Maximum;
            internal MetricDefinition(string key, string label, string unit, int minimum, int maximum)
            { Key = key; Label = label; Unit = unit; Minimum = minimum; Maximum = maximum; }
        }

        // A single bounded record is held in memory. The selected file is never
        // ReadToEnd'ed, split by lines, or copied wholesale into a string.
        private sealed class CsvReader
        {
            private readonly TextReader input;
            private readonly CancellationToken cancellation;
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            private long bytes;
            private int characters, pending = -1;
            private bool first = true, previousHighSurrogate;
            internal CsvReader(TextReader input, CancellationToken cancellation) { this.input = input; this.cancellation = cancellation; }
            private int ReadCharacter()
            {
                if ((characters++ & 1023) == 0) Check();
                int next;
                if (pending >= 0) { next = pending; pending = -1; return next; }
                next = input.Read();
                if (next < 0)
                {
                    if (previousHighSurrogate) throw new InvalidDataException("El CSV contiene texto Unicode inválido.");
                    return next;
                }
                char character = (char)next;
                if (previousHighSurrogate && !char.IsLowSurrogate(character) || char.IsLowSurrogate(character) && !previousHighSurrogate)
                    throw new InvalidDataException("El CSV contiene texto Unicode inválido.");
                bytes += char.IsLowSurrogate(character) ? 1 : character < 0x80 ? 1 : character < 0x800 ? 2 : 3;
                previousHighSurrogate = char.IsHighSurrogate(character);
                if (bytes > MaximumBytes) throw new InvalidDataException("El CSV supera 64 MiB de texto UTF-8.");
                return next;
            }
            private void Check()
            {
                cancellation.ThrowIfCancellationRequested();
                if (elapsed.Elapsed.TotalSeconds > MaximumSeconds) throw new InvalidDataException("El resumen superó 30 segundos. Usa una sesión más corta.");
            }
            internal string[] Next()
            {
                Check();
                var cells = new List<string>(); var cell = new StringBuilder();
                bool quoted = false, closed = false, started = false;
                int rowCharacters = 0;
                while (true)
                {
                    int next = ReadCharacter();
                    if (first) { first = false; if (next == 0xfeff) next = ReadCharacter(); }
                    if (next < 0)
                    {
                        if (quoted) throw new InvalidDataException("El CSV termina dentro de una celda entre comillas.");
                        if (!started && cells.Count == 0 && cell.Length == 0) return null;
                        Add(cells, cell); return cells.ToArray();
                    }
                    started = true;
                    if (++rowCharacters > MaximumRowCharacters) throw new InvalidDataException("Una fila CSV supera 128 KiB de caracteres.");
                    char current = (char)next;
                    if (quoted)
                    {
                        if (current == '"') { quoted = false; closed = true; }
                        else Append(cell, current);
                        continue;
                    }
                    if (closed && current == '"') { Append(cell, '"'); quoted = true; closed = false; continue; }
                    if (current == ',') { Add(cells, cell); closed = false; continue; }
                    if (current == '\r' || current == '\n')
                    {
                        if (current == '\r') { int following = ReadCharacter(); if (following != '\n') pending = following; }
                        Add(cells, cell); return cells.ToArray();
                    }
                    if (closed) throw new InvalidDataException("Texto inesperado después de una celda CSV entre comillas.");
                    if (current == '"')
                    {
                        if (cell.Length != 0) throw new InvalidDataException("Comillas inesperadas dentro de una celda CSV.");
                        quoted = true;
                    }
                    else Append(cell, current);
                }
            }
            private static void Append(StringBuilder text, char character)
            {
                if (text.Length == MaximumCellCharacters) throw new InvalidDataException("Una celda CSV supera 1024 caracteres.");
                text.Append(character);
            }
            private static void Add(List<string> cells, StringBuilder cell)
            {
                if (cells.Count == MaximumColumns) throw new InvalidDataException("El CSV supera 160 columnas.");
                cells.Add(cell.ToString()); cell.Clear();
            }
        }
    }
}
