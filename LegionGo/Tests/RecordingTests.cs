// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private static void RecordingTests()
    {
        Test("Limits show Lenovo mode and distinguish inactive or unknown custom values", () =>
        {
            foreach (int mode in new[] { 1, 2, 3, 255, 99 })
            {
                var s = RecordingSample(DateTime.UtcNow, mode: mode).State;
                string label = TelemetryValue.DescribeLimits(s);
                Assert(label.Contains("10 W / 10 W / 10 W"), "tuple hidden");
                if (mode == 255) Assert(label.Contains("Modo Lenovo: Personalizado") && !label.Contains("inactivo"), "custom mislabeled");
                else if (mode == 99) Assert(label.Contains("sin confirmar"), "unknown mode presented as active");
                else Assert(label.Contains("Personalizado inactivo") && label.Contains(TelemetryValue.ModeName(mode)), "preset presented as custom");
            }
            var bad = RecordingSample(DateTime.UtcNow).State;
            bad.Values[0].Error = "failed";
            Assert(TelemetryValue.DescribeLimits(bad).Contains("sin confirmar"), "errored mode treated as valid");
            Assert(TelemetryValue.DescribeLimits(null).Contains("N/D"), "missing mode fabricated");
        });
        Test("CSV preserves raw units, unavailable values, Unicode and escaped error text across cultures", () =>
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                DateTime now = DateTime.UtcNow;
                var s = RecordingSample(now).State;
                s.Values = s.Values.Concat(new[] {
                    new Reading { Key = "GpuVoltageMv", Value = 685, Unit = "mV", Source = "AMD · GFX" },
                    new Reading { Key = "GpuTemperature", Value = 0, Unit = "°C", Error = "Error, \"sensor\"\nnuevo" },
                    new Reading { Key = "GpuPower", Value = 0, Unit = "W" },
                    new Reading { Key = "CpuPower", Error = "=HYPERLINK(\"bad\")" },
                    new Reading { Key = "Telemetry", Error = "driver unavailable" } }).ToArray();
                var rows = ParseCsv(TelemetryCsv.Header() + "\r\n" + TelemetryCsv.Row("sample", now, now.AddSeconds(-1.5), 2.5, "start", s, null) + "\r\n");
                var data = MapRow(rows, 1);
                Assert(data["elapsed_s"] == "1.500" && data["gap_s"] == "2.500", "locale changed decimals");
                Assert(data["GpuVoltageMv"] == "685" && data["GpuVoltageMv_unit"] == "mV" && data["GpuVoltageMv_source"] == "AMD · GFX", "raw voltage lost");
                Assert(data["GpuTemperature"] == "" && data["GpuTemperature_error"] == "Error, \"sensor\"\nnuevo", "error became zero or CSV escaped incorrectly");
                Assert(data["GpuPower"] == "0" && data["CpuPower"] == "" && data["CpuPower_error"].StartsWith("'="), "missing values or formula safety");
                Assert(data["Spl_unit"] == "W" && data["custom_mode"] == "1", "setting units or mode lost");
                Assert(data["sample_error"] == "Telemetry: driver unavailable", "unlisted sensor error lost");
                Assert(data["app_version"] == typeof(TelemetryCsv).Assembly.GetName().Version.ToString(), "CSV did not identify its actual producer version");
            }
            finally { CultureInfo.CurrentCulture = old; }
        });
        Test("Recording commands are read-only, idempotent and independent of status polling", () => WithRecordingDirectory(folder =>
        {
            var h = new Fake(); var j = new Journal();
            using (var session = new HelperSession(new ControlEngine(h, j), folder, false))
            {
                Assert(session.Execute(new Request { Operation = "recording_status" }).Recording.Samples == 0 && h.Reads.Count == 0, "idle status sampled hardware");
                var start = session.Execute(new Request { Operation = "recording_start" });
                Assert(start.Success && start.Recording.Active && start.Recording.Samples == 1, "start failed");
                int reads = h.Reads.Count;
                var again = session.Execute(new Request { Operation = "recording_start" });
                Assert(again.Recording.FileName == start.Recording.FileName && h.Reads.Count == reads, "second start truncated or re-sampled");
                session.Recorder.Tick();
                var stop = session.Execute(new Request { Operation = "recording_stop" });
                session.Execute(new Request { Operation = "recording_stop" });
                Assert(!stop.Recording.Active && stop.Recording.Samples == 2, "stop failed");
                Assert(h.Writes.Count == 0 && j.Saves == 0, "recording changed hardware or originals");
                var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, start.Recording.FileName)));
                Assert(File.ReadAllBytes(Path.Combine(folder, start.Recording.FileName)).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }), "UTF-8 BOM missing");
                Assert(rows.Count == 4 && MapRow(rows, 3)["events"] == "stop", "expected header, two samples and end");
                Assert(MapRow(rows, 1)["CpuBoostAC"] == "4" && MapRow(rows, 1)["EppDC"] == "50", "full settings were not sampled");
                var copy = Protocol.Decode<Response>(Protocol.Encode(stop));
                Assert(copy.Recording.Samples == 2 && copy.Recording.Directory == folder, "recording status lost over IPC");
            }
        }));
        Test("Recorder timer samples without widget requests and stops producing after disposal", () => WithRecordingDirectory(folder =>
        {
            int calls = 0;
            var recorder = new TelemetryRecorder(() => { Interlocked.Increment(ref calls); return RecordingSample(DateTime.UtcNow); }, folder);
            recorder.Start();
            try { Assert(SpinWait.SpinUntil(() => recorder.GetStatus().Samples >= 2, TimeSpan.FromSeconds(6)), "background timer did not sample"); }
            finally { recorder.Dispose(); }
            int stopped = calls;
            recorder.Tick();
            Assert(calls == stopped && !recorder.GetStatus().Active, "disposed recording kept sampling");
        }));
        Test("Recording marks AC, mode, limit and EPP changes and sampling gaps without filling fake samples", () => WithRecordingDirectory(folder =>
        {
            DateTime now = DateTime.UtcNow; bool ac = true; int mode = 255, limit = 10, epp = 30;
            using (var recorder = new TelemetryRecorder(() => RecordingSample(now, ac, mode, limit, epp), folder, () => now, false))
            {
                recorder.Start(); now = now.AddSeconds(2); ac = false; recorder.Tick();
                now = now.AddSeconds(62); ac = true; mode = 3; limit = 15; epp = 80; recorder.Tick(); recorder.Stop();
                var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, recorder.GetStatus().FileName)));
                Assert(rows.Count == 5, "gap filled with invented samples");
                Assert(MapRow(rows, 2)["events"].Contains("ac_changed") && MapRow(rows, 2)["on_ac"] == "0", "DC transition missing");
                var last = MapRow(rows, 3);
                foreach (string value in new[] { "gap", "ac_changed", "Mode_changed", "Spl_changed", "EppAC_changed" }) Assert(last["events"].Contains(value), value);
                Assert(last["gap_s"] == "62.000" && last["custom_mode"] == "0" && last["Mode"] == "3", "gap or inactive limits mislabeled");
            }
        }));
        Test("Read errors and stale samples record empty metrics and recover on the next fresh sample", () => WithRecordingDirectory(folder =>
        {
            DateTime now = DateTime.UtcNow; int phase = 0;
            using (var recorder = new TelemetryRecorder(() => {
                if (phase == 1) throw new IOException("sensor disconnected");
                return RecordingSample(phase == 2 ? now.AddMinutes(-1) : now);
            }, folder, () => now, false))
            {
                recorder.Start();
                foreach (int value in new[] { 1, 2, 3 }) { now = now.AddSeconds(2); phase = value; recorder.Tick(); }
                Assert(recorder.GetStatus().Active && recorder.GetStatus().Samples == 2 && recorder.GetStatus().Error == null, "fresh read did not recover");
                recorder.Stop();
                var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, recorder.GetStatus().FileName)));
                foreach (int row in new[] { 2, 3 })
                {
                    var data = MapRow(rows, row);
                    Assert(data["row_type"] == "error" && data["Mode"] == "" && data["on_ac"] == "" && data["sample_error"] != "", "stale/error row retained old metrics");
                }
                Assert(MapRow(rows, 4)["Mode"] == "255", "fresh sample missing");
            }
        }));
        Test("Unavailable initial capture never creates a CSV or claims recording", () => WithRecordingDirectory(folder =>
        {
            foreach (var response in new[] { new Response { Success = false, Message = "offline" }, new Response { Success = true, State = new Snapshot { Supported = false } }, RecordingSample(DateTime.UtcNow.AddMinutes(-1)) })
            using (var recorder = new TelemetryRecorder(() => response, folder, automaticSampling: false))
            {
                bool failed = false;
                try { recorder.Start(); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && !recorder.GetStatus().Active && !string.IsNullOrEmpty(recorder.GetStatus().Error), "failed start reported success");
            }
            Assert(Directory.GetFiles(folder).Length == 0, "failed start created output");
        }));
        Test("Recorder rejects local or future timestamps before creating a session", () => WithRecordingDirectory(folder =>
        {
            var now = new DateTime(2026, 10, 16, 22, 30, 0, DateTimeKind.Utc);
            foreach (var stamp in new[] { now.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"), now.AddSeconds(3).ToString("O") })
            {
                var sample = RecordingSample(now); sample.State.CapturedAtUtc = stamp;
                using (var recorder = new TelemetryRecorder(() => sample, folder, () => now, false))
                {
                    bool rejected = false;
                    try { recorder.Start(); } catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected && !recorder.GetStatus().Active && Directory.GetFiles(folder).Length == 0, "invalid capture started a CSV");
                }
            }
        }));
        Test("Invalid protocol cannot start recording and a file creation failure leaves hardware controls usable", () => WithRecordingDirectory(folder =>
        {
            string blocked = Path.Combine(folder, "not-a-directory"); File.WriteAllText(blocked, "keep");
            var h = new Fake(); var j = new Journal();
            using (var session = new HelperSession(new ControlEngine(h, j), blocked, false))
            {
                Assert(!session.Execute(new Request { Version = 99, Operation = "recording_start" }).Success && h.Reads.Count == 0, "bad protocol sampled");
                var response = session.Execute(new Request { Operation = "recording_start" });
                Assert(!response.Success && !response.Recording.Active && response.Recording.Error != null, "write failure hidden");
                Assert(h.Writes.Count == 0 && j.Saves == 0 && File.ReadAllText(blocked) == "keep", "failed recording changed unrelated state");
                Assert(session.Execute(Tdp(10, 10, 10)).Success, "recording failure broke control engine");
            }
        }));
        Test("A second session and helper restart preserve older CSV files without restarting recording", () => WithRecordingDirectory(folder =>
        {
            var h = new Fake(); var engine = new ControlEngine(h, new Journal());
            string first; byte[] original;
            using (var session = new HelperSession(engine, folder, false))
            {
                first = session.Execute(new Request { Operation = "recording_start" }).Recording.FileName;
                session.Execute(new Request { Operation = "recording_stop" }); original = File.ReadAllBytes(Path.Combine(folder, first));
                var second = session.Execute(new Request { Operation = "recording_start" }).Recording.FileName;
                Assert(first != second, "session filename reused");
            }
            using (var restarted = new HelperSession(engine, folder, false)) Assert(!restarted.Recorder.GetStatus().Active, "restart silently resumed recording");
            Assert(File.ReadAllBytes(Path.Combine(folder, first)).SequenceEqual(original) && Directory.GetFiles(folder).Length == 2, "old CSV overwritten");
        }));
        Test("Session duration cap ends cleanly before sampling again after a long pause", () => WithRecordingDirectory(folder =>
        {
            DateTime now = DateTime.UtcNow; int reads = 0;
            using (var recorder = new TelemetryRecorder(() => { reads++; return RecordingSample(now); }, folder, () => now, false))
            {
                recorder.Start(); now = now.AddHours(8); recorder.Tick();
                Assert(!recorder.GetStatus().Active && reads == 1 && recorder.GetStatus().Error.Contains("8 horas"), "duration cap failed");
                var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, recorder.GetStatus().FileName)));
                Assert(MapRow(rows, 2)["events"] == "limit_reached", "automatic end missing");
            }
        }));
        Test("Background samples cannot observe a partially-applied TDP tuple", () => WithRecordingDirectory(folder =>
        {
            var h = new Fake(); var j = new Journal();
            using (var session = new HelperSession(new ControlEngine(h, j), folder, false))
            {
                session.Execute(new Request { Operation = "recording_start" });
                Parallel.Invoke(() => { for (int i = 0; i < 20; i++) session.Recorder.Tick(); },
                    () => { for (int i = 0; i < 20; i++) Assert(session.Execute(i % 2 == 0 ? Tdp(10,12,15) : Tdp(20,25,30)).Success, "control write failed"); });
                session.Recorder.Stop();
                var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, session.Recorder.GetStatus().FileName)));
                for (int i = 1; i < rows.Count - 1; i++)
                {
                    var row = MapRow(rows, i); string tuple = row["Spl"] + "/" + row["Sppt"] + "/" + row["Fppt"];
                    Assert(new[] { "15/20/25", "10/12/15", "20/25/30" }.Contains(tuple), "torn TDP sample " + tuple);
                }
            }
        }));
    }

    private static Response RecordingSample(DateTime time, bool ac = true, int mode = 255, int limit = 10, int epp = 30)
    {
        return new Response { Success = true, State = new Snapshot { Supported = true, OnAc = ac, CapturedAtUtc = time.ToString("O"),
            Values = new[] { new Reading { Key = "Mode", Value = mode }, new Reading { Key = "Spl", Value = limit },
                new Reading { Key = "Sppt", Value = limit }, new Reading { Key = "Fppt", Value = limit }, new Reading { Key = "EppAC", Value = epp } } } };
    }
    private static void WithRecordingDirectory(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), "LegionTweaks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); } finally { Directory.Delete(path, true); }
    }
    private static Dictionary<string, string> MapRow(List<string[]> rows, int row)
    {
        Assert(rows[row].Length == rows[0].Length, "CSV column mismatch");
        return rows[0].Select((name, index) => new { name, value = rows[row][index] }).ToDictionary(x => x.name, x => x.value);
    }
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>(); var cells = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == ',' && !quoted) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' && !quoted) { cells.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add(cells.ToArray()); cells.Clear(); }
            else cell.Append(c);
        }
        Assert(!quoted && cell.Length == 0 && cells.Count == 0, "CSV record not terminated");
        return rows;
    }
}
