// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void AmdRecoveryTests()
    {
        Test("Failed PMLog recovers despite valid fallback temperatures, without polling a new context each tick", () =>
        {
            DateTime now = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
            var broken = new FakeAmdSession { QueryStatus = -1, HasPmLogValues = false, Fallback = true };
            var healthy = new FakeAmdSession(); int opens = 0;
            using (var amd = new AmdTelemetry(() => ++opens == 1 ? broken : healthy, () => now))
            {
                for (int i = 0; i <= 15; i++)
                {
                    var values = amd.Read();
                    Assert(values.Single(x => x.Key == "GpuTemperature").Value == 48, "fallback temperature lost");
                    Assert(values.Single(x => x.Key == "GpuPower").Value == null, "failed PMLog fabricated power");
                    Assert(values[0].Diagnostic.Contains("pmlog_status=-1"), "query failure hidden by fallback");
                    Assert(opens == 1, "reinitialization storm");
                    now = now.AddSeconds(2);
                }
                var recovered = amd.Read();
                Assert(opens == 2 && broken.Disposals == 1, "failed context retained forever");
                Assert(recovered.Single(x => x.Key == "GpuPower").Value == 7, "fresh context did not restore power");
                Assert(recovered[0].Diagnostic.Contains("amd_session=2") && recovered[0].Diagnostic.Contains("after_pmlog_error"), "recovery provenance lost");
                now = now.AddSeconds(2);
                Assert(amd.Read()[0].Diagnostic.Contains("after_pmlog_error"), "concurrent widget read could consume the recovery marker");
            }
            Assert(healthy.Disposals == 1, "context not released at shutdown");
        });
        Test("Repeated PMLog failures use bounded re-enumeration even when every new context fails", () =>
        {
            DateTime now = DateTime.UtcNow; int opens = 0;
            using (var amd = new AmdTelemetry(() => { opens++; return new FakeAmdSession { QueryStatus = 0, HasPmLogValues = false, Fallback = true }; }, () => now))
            {
                for (int i = 0; i < 50; i++) { amd.Read(); now = now.AddSeconds(2); }
                Assert(opens == 4, "empty successful PMLog result looped or never recovered");
            }
        });
        Test("Drivers without PMLog keep working OD5 fallback instead of repeatedly reopening", () =>
        {
            DateTime now = DateTime.UtcNow; int opens = 0;
            var fallback = new FakeAmdSession { QueryStatus = null, HasPmLogValues = false, Fallback = true };
            using (var amd = new AmdTelemetry(() => { opens++; return fallback; }, () => now))
            {
                for (int i = 0; i < 40; i++)
                {
                    Assert(amd.Read()[0].Diagnostic.Contains("pmlog_unavailable"), "missing API hidden");
                    now = now.AddSeconds(2);
                }
                Assert(opens == 1 && fallback.Disposals == 0, "unsupported API created retry churn");
            }
        });
        Test("AMD open failures respect retry delay and return unavailable readings with no stale values", () =>
        {
            DateTime now = DateTime.UtcNow; int attempts = 0;
            using (var amd = new AmdTelemetry(() => { attempts++; if (attempts == 1) throw new IOException("driver reset"); return new FakeAmdSession(); }, () => now))
            {
                Assert(amd.Read().All(x => x.Value == null && x.Error == "driver reset"), "error values fabricated");
                now = now.AddSeconds(12);
                Assert(amd.Read().All(x => x.Value == null) && attempts == 1, "gap bypassed initialization backoff");
                now = now.AddSeconds(18);
                Assert(amd.Read().Single(x => x.Key == "GpuPower").Value == 7 && attempts == 2, "initialization never retried");
            }
        });
        Test("AMD read exceptions clear the old sample, release the context, and recover after cooldown", () =>
        {
            DateTime now = DateTime.UtcNow; int opens = 0;
            var first = new FakeAmdSession();
            var amd = new AmdTelemetry(() => ++opens == 1 ? first : new FakeAmdSession(), () => now);
            amd.Read(); first.Throw = true; now = now.AddSeconds(2);
            Assert(amd.Read().All(x => x.Value == null && x.Error == "read failed"), "previous values reused after read failure");
            Assert(first.Disposals == 1, "faulted context leaked");
            now = now.AddSeconds(30);
            Assert(amd.Read()[0].Diagnostic.Contains("after_error") && opens == 2, "read failure did not recover");
            amd.Dispose(); amd.Dispose();
            bool rejected = false;
            try { amd.Read(); } catch (ObjectDisposedException) { rejected = true; }
            Assert(rejected && opens == 2, "disposed owner reopened a native context");
        });
        Test("Recording across a simulated suspend gap reopens AMD and records only fresh post-gap data", () => WithRecordingDirectory(folder =>
        {
            DateTime now = DateTime.UtcNow; int opens = 0;
            var first = new FakeAmdSession(); var second = new FakeAmdSession { GpuWatts = 28, AsicWatts = 15 };
            using (var amd = new AmdTelemetry(() => ++opens == 1 ? first : second, () => now))
            using (var recorder = new TelemetryRecorder(() => {
                var response = RecordingSample(now);
                response.State.Values = response.State.Values.Concat(amd.Read()).ToArray();
                return response;
            }, folder, () => now, false))
            {
                recorder.Start(); now = now.AddSeconds(2); recorder.Tick();
                now = now.AddSeconds(60); recorder.Tick(); now = now.AddSeconds(2); recorder.Tick();
                Assert(recorder.GetStatus().Active && recorder.GetStatus().Samples == 4, "session stopped or sleep samples invented");
                recorder.Stop();
                var rows = ParseCsv(File.ReadAllText(Path.Combine(folder, recorder.GetStatus().FileName)));
                Assert(rows.Count == 6 && opens == 2 && first.Disposals == 1, "incorrect resume lifecycle");
                var resumed = MapRow(rows, 3);
                Assert(resumed["gap_s"] == "60.000" && resumed["events"].Contains("gap") && !resumed["events"].Contains("suspend"), "gap misrepresented");
                Assert(resumed["GpuPower"] == "28" && resumed["AsicPower"] == "15" && resumed["GpuPower_raw"] == "28", "raw discrepancy changed or stale sample replayed");
                Assert(resumed["GpuPower_diagnostic"].Contains("after_gap") && resumed["events"].Contains("power_review") && resumed["power_note"] != "", "resume or power diagnostics missing");
                Assert(MapRow(rows, 4)["gap_s"] == "2.000", "normal sampling did not resume");
            }
        }));
        Test("PMLog diagnostics retain invalid raw values while normal readings remain unavailable through IPC and CSV", () =>
        {
            var sensors = new AmdNative.Sensor[256];
            sensors[21] = new AmdNative.Sensor { Supported = 0, Value = 900 };
            sensors[16] = new AmdNative.Sensor { Supported = 1, Value = 65535 };
            sensors[30] = new AmdNative.Sensor { Supported = 1, Value = 0 };
            var state = RecordingSample(DateTime.UtcNow).State;
            state.Values = state.Values.Concat(AmdTelemetry.Decode(sensors, "AMD ADL PMLog test")).ToArray();
            string json = Protocol.Encode(new Response { Success = true, State = state });
            Assert(json.Length < Protocol.MaxMessageLength, "diagnostic response exceeded pipe limit");
            var copy = Protocol.Decode<Response>(json).State;
            var rows = ParseCsv(TelemetryCsv.Header() + "\r\n" + TelemetryCsv.Row("sample", DateTime.UtcNow, DateTime.UtcNow, 0, "start", copy, null) + "\r\n");
            var row = MapRow(rows, 1);
            Assert(row["schema_version"] == "2" && row["GpuVoltageMv"] == "" && row["GpuVoltageMv_raw"] == "900" && row["GpuVoltageMv_supported"] == "0", "unsupported raw sample promoted or lost");
            Assert(row["SocVoltageMv"] == "" && row["SocVoltageMv_raw"] == "65535" && row["SocVoltageMv_sensor_id"] == "16", "sentinel evidence lost");
            Assert(row["GpuPower"] == "0" && row["GpuPower_raw"] == "0" && row["GpuPower_sensor_id"] == "30", "valid zero lost");
            Assert(row["power_note"] == "", "missing ASIC produced a comparison warning");
        });
        Test("Power review preserves separate domains and requires two valid measurements", () =>
        {
            var s = new Snapshot { Values = new[] { new Reading { Key = "GpuPower", Value = 28 }, new Reading { Key = "AsicPower", Value = 15 }, new Reading { Key = "CpuPower", Value = 1 } } };
            Assert(TelemetryValue.PowerNote(s) != null && s.Values[0].Value == 28 && s.Values[1].Value == 15, "review altered values");
            s.Values[0].Error = "failed";
            Assert(TelemetryValue.PowerNote(s) == null, "errored power used for review");
            s.Values[0].Error = null; s.Values[0].Value = 15;
            Assert(TelemetryValue.PowerNote(s) == null && TelemetryValue.PowerNote(null) == null, "false discrepancy");
        });
    }

    private sealed class FakeAmdSession : IAmdSession
    {
        internal int? QueryStatus = 0;
        internal bool HasPmLogValues = true, Fallback, Throw;
        internal int Disposals, GpuWatts = 7, AsicWatts = 10;
        public Reading[] Read(out int? queryStatus, out bool hasPmLogValues)
        {
            if (Throw) throw new IOException("read failed");
            if (Disposals > 0) throw new ObjectDisposedException("session");
            queryStatus = QueryStatus; hasPmLogValues = HasPmLogValues;
            AmdNative.Sensor[] sensors = null;
            if (HasPmLogValues)
            {
                sensors = new AmdNative.Sensor[256];
                sensors[8] = new AmdNative.Sensor { Supported = 1, Value = 51 };
                sensors[30] = new AmdNative.Sensor { Supported = 1, Value = GpuWatts };
                sensors[23] = new AmdNative.Sensor { Supported = 1, Value = AsicWatts };
            }
            var readings = AmdTelemetry.Decode(sensors, "AMD ADL PMLog test");
            if (Fallback) readings[0] = new Reading { Key = "GpuTemperature", Value = 48, Unit = "°C", Source = "AMD ADL OD5 test" };
            return readings;
        }
        public void Dispose() { Disposals++; }
    }
}
