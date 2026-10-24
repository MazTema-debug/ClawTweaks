// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Threading;
using LegionTweaks;

internal static partial class Program
{
    private static void RtssServiceTests()
    {
        Test("RTSS starts OFF and cached status never samples or opens shared memory", () =>
        {
            int reads = 0; var output = new RtssFakeOutput();
            using (var service = new RtssOverlayService(() => { reads++; return null; }, output, true, false))
            {
                for (int i = 0; i < 3; i++) { service.GetStatus(); service.Tick(); }
                service.Observe(RtssSample(DateTime.UtcNow)); service.Suspend();
                Assert(!service.GetStatus().Enabled && reads == 0 && output.Publishes == 0 && output.Releases == 0,
                    "idle service touched sensors or RTSS");
            }
            Assert(output.Disposals == 1 && output.Releases == 0, "idle disposal opened an OSD entry");
        });
        Test("RTSS unsupported hardware rejects ON before sampling or publishing", () =>
        {
            int reads = 0; var output = new RtssFakeOutput();
            using (var service = new RtssOverlayService(() => { reads++; return null; }, output, false, false))
            {
                bool failed = false;
                try { service.SetEnabled(true); } catch (InvalidOperationException) { failed = true; }
                service.Tick();
                Assert(failed && !service.GetStatus().Enabled && reads == 0 && output.Publishes == 0, "unsupported RTSS started");
            }
        });
        Test("RTSS observes a private snapshot without changing producer values or timestamps", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput(); int reads = 0;
            using (var service = RtssService(clock, output, () => { reads++; return null; }))
            {
                service.SetEnabled(true); var response = RtssSample(clock.Utc, 55);
                service.Observe(response);
                response.State.Values[0].Value = 99; response.State.CapturedAtUtc = "modified";
                service.Tick();
                Assert(output.LastText.Contains("55") && !output.LastText.Contains("99"), "producer mutation changed accepted sample");
                Assert(service.GetStatus().LastSampleAtUtc == clock.Utc.ToString("O") && reads == 0, "observed sample re-read or timestamp replaced");
                var status = service.GetStatus(); status.Enabled = false; status.State = "injected";
                Assert(service.GetStatus().Enabled && service.GetStatus().State == "published", "status exposes mutable internals");
                Assert(response.State.Values[0].RawValue == 555 && response.State.Values[0].Diagnostic == "private raw note", "raw producer data altered");
            }
        });
        Test("RTSS fresh partial readings are formatted without exposing arbitrary errors", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); var response = RtssSample(clock.Utc);
                response.State.Values[0].Error = "C:\\private\\user-token.xml";
                service.Observe(response); service.Tick();
                Assert(!output.LastText.Contains("Telemetria N/D") && !output.LastText.Contains("private"), "partial data leaked an error or became an entire stale sample");
            }
        });
        Test("RTSS unrelated cached status responses preserve the last telemetry sample", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick();
                foreach (var response in new[] { null, new Response { Success = true, Recording = new RecordingStatus() },
                    new Response { Success = false, Message = "controller busy" } })
                    service.Observe(response);
                service.Tick();
                Assert(output.LastText.Contains("55") && service.GetStatus().LastSampleAtUtc == clock.Utc.ToString("O"), "non-telemetry status erased sensors");
            }
        });
        Test("RTSS stale malformed and future samples publish a fixed unavailable message", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true);
                foreach (string timestamp in new[] { clock.Utc.AddSeconds(-7).ToString("O"), clock.Utc.AddSeconds(3).ToString("O"), "2026-10-18T12:00:00", null })
                {
                    var response = RtssSample(clock.Utc); response.State.CapturedAtUtc = timestamp;
                    service.Observe(response); service.Tick();
                    Assert(output.LastText.Contains("Telemetria N/D") && service.GetStatus().State == "stale"
                        && service.GetStatus().Publishing, "invalid telemetry retained numeric output or successful unavailable publication was hidden");
                }
            }
        });
        Test("RTSS monotonic receipt expires repeated telemetry across wall clock rollback", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); var response = RtssSample(clock.Utc);
                service.Observe(response); service.Tick();
                clock.Milliseconds = 6001; clock.Utc = clock.Utc.AddSeconds(-1);
                service.Observe(response); service.Tick();
                Assert(output.LastText.Contains("Telemetria N/D"), "wall rollback renewed old numbers");
                service.Observe(new Response { Success = false, State = new Snapshot { Supported = true } }); service.Observe(response); service.Tick();
                Assert(output.LastText.Contains("Telemetria N/D"), "error reset the expired receipt budget");
            }
        });
        Test("RTSS newer observations reject older successful responses", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc, 55));
                service.Observe(RtssSample(clock.Utc.AddSeconds(-1), 99)); service.Tick();
                Assert(output.LastText.Contains("55") && !output.LastText.Contains("99"), "out of order response replaced newest sample");
            }
        });
        Test("RTSS cached status marks an expired publication without sampling or touching output", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput(); int reads = 0;
            using (var service = RtssService(clock, output, () => { reads++; return null; }))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick();
                clock.Milliseconds = 6001; clock.Utc = clock.Utc.AddMilliseconds(6001);
                var status = service.GetStatus();
                Assert(status.Enabled && !status.Publishing && status.State == "stale" && output.Publishes == 1 && reads == 0,
                    "status represented expired data as current or performed IO");
            }
        });
        Test("RTSS OFF and suspend retire the OSD and never enable it again", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick(); service.SetEnabled(false);
                int calls = output.Publishes; service.Observe(RtssSample(clock.Utc)); service.Tick();
                Assert(output.Releases == 1 && output.Publishes == calls && !service.GetStatus().Enabled, "OFF did not stop publication");
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick(); service.Suspend(); service.Tick();
                Assert(output.Releases == 2 && !service.GetStatus().Enabled && !service.GetStatus().Publishing, "suspend retained activation");
            }
        });
        Test("RTSS absent busy full or incompatible states stay honest and recover without re-enabling", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc));
                foreach (string state in new[] { "missing", "busy", "full", "incompatible" })
                {
                    output.NextPublish = new RtssPublishResult { State = state, Error = "C:\\private\\secret", CleanupPending = state == "busy" };
                    service.Tick(); var status = service.GetStatus();
                    Assert(status.Enabled && !status.Publishing && status.State == state && !Protocol.Encode(status).Contains("private"), "failure hidden or native detail leaked");
                }
                output.NextPublish = null; service.Tick();
                Assert(service.GetStatus().Publishing && service.GetStatus().Updates == 1, "RTSS did not reconnect");
            }
        });
        Test("RTSS read errors clear numeric output and a fresh observation recovers", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput();
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick();
                foreach (var response in new[] { new Response { Success = false, Message = "private path", State = new Snapshot { Supported = true } }, new Response { Success = true, State = new Snapshot { Supported = false } } })
                {
                    service.Observe(response); service.Tick();
                    Assert(output.LastText.Contains("Telemetria N/D") && !Protocol.Encode(service.GetStatus()).Contains("private"), "read error kept numbers or leaked details");
                }
                clock.Utc = clock.Utc.AddSeconds(1); clock.Milliseconds = 1000;
                service.Observe(RtssSample(clock.Utc, 56)); service.Tick();
                Assert(output.LastText.Contains("56") && service.GetStatus().Error == null, "fresh data did not recover");
            }
        });
        Test("RTSS output exceptions cannot terminate sampling and expose only fixed diagnostics", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput { ThrowPublish = true };
            using (var service = RtssService(clock, output))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick();
                Assert(service.GetStatus().State == "error" && !Protocol.Encode(service.GetStatus()).Contains("secret"), "output exception leaked or escaped");
                output.ThrowPublish = false; service.Tick();
                Assert(service.GetStatus().Publishing, "output did not recover after exception");
            }
        });
        Test("RTSS cleanup retries are bounded and do not dispose a still-needed transport early", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput { BusyReleases = 2 };
            var service = RtssService(clock, output);
            service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick(); service.Dispose();
            Assert(service.GetStatus().CleanupPending && output.Releases == 1 && output.Disposals == 0, "transport closed before cleanup retry");
            service.Tick(); Assert(output.Releases == 2 && output.Disposals == 0, "second retry missing");
            service.Tick(); Assert(output.Releases == 3 && output.Disposals == 1 && !service.GetStatus().CleanupPending, "cleanup did not retire own output");
            service.Tick(); service.Dispose(); Assert(output.Releases == 3 && output.Disposals == 1, "disposed transport used again");
        });
        Test("RTSS persistent cleanup failure reports pending and stops retrying at its fixed budget", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput { BusyReleases = 99 };
            var service = RtssService(clock, output);
            service.SetEnabled(true); service.Observe(RtssSample(clock.Utc)); service.Tick(); service.Dispose();
            for (int i = 0; i < 20; i++) service.Tick();
            Assert(output.Releases == RtssOverlayService.MaximumCleanupAttempts && output.Disposals == 1
                && service.GetStatus().CleanupPending && !service.GetStatus().Publishing, "cleanup retry budget or honest pending state lost");
        });
        Test("RTSS blocked sensor read cannot delay OFF or overlap a new generation", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput(); int calls = 0;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var service = RtssService(clock, output, () =>
            {
                int call = Interlocked.Increment(ref calls);
                if (call == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return RtssSample(clock.Utc, 99); }
                return RtssSample(clock.Utc, 55);
            }))
            {
                try
                {
                    service.SetEnabled(true); service.Tick();
                    Assert(entered.Wait(TimeSpan.FromSeconds(2)), "read task did not start");
                    var elapsed = Stopwatch.StartNew(); service.SetEnabled(false); elapsed.Stop();
                    Assert(elapsed.ElapsedMilliseconds < 250 && output.Releases == 1, "OFF waited for sensor read");
                    service.SetEnabled(true); service.Tick(); service.Tick();
                    Assert(calls == 1 && output.LastText.Contains("Telemetria N/D"), "new generation overlapped a retained sensor task");
                    service.Observe(RtssSample(clock.Utc, 55)); release.Set();
                    clock.Utc = clock.Utc.AddSeconds(2); clock.Milliseconds = 2000;
                    Assert(SpinWait.SpinUntil(() => { service.Tick(); return Volatile.Read(ref calls) == 2; }, TimeSpan.FromSeconds(2)), "read flight never retired");
                    service.Tick();
                    Assert(!output.SawNinetyNine, "retired-generation read published old data");
                }
                finally { release.Set(); }
            }
        });
        Test("RTSS observations suppress redundant sensor requests within the sampling interval", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput(); int calls = 0;
            using (var service = RtssService(clock, output, () => { Interlocked.Increment(ref calls); return RtssSample(clock.Utc); }))
            {
                service.SetEnabled(true); service.Observe(RtssSample(clock.Utc));
                foreach (long time in new long[] { 0, 500, 1000, 1500, 1999 }) { clock.Milliseconds = time; service.Tick(); }
                Assert(calls == 0, "recent producer observation triggered duplicate telemetry");
                clock.Milliseconds = 2000; clock.Utc = clock.Utc.AddSeconds(2); service.Tick();
                Assert(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(2)), "due telemetry request missing");
            }
        });
        Test("RTSS unavailable output suppresses sensor reads and successful publication resumes single-flight sampling", () =>
        {
            var clock = new RtssClock(); var output = new RtssFakeOutput(); int calls = 0;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var service = RtssService(clock, output, () =>
            {
                Interlocked.Increment(ref calls); entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return null;
            }))
            {
                try
                {
                    service.SetEnabled(true);
                    foreach (string state in new[] { "missing", "full", "incompatible", "busy", "error" })
                    {
                        output.NextPublish = new RtssPublishResult { State = state };
                        clock.Milliseconds += 2500; clock.Utc = clock.Utc.AddMilliseconds(2500);
                        service.Tick(); service.Tick();
                    }
                    Assert(calls == 0 && !entered.IsSet, "unavailable RTSS unnecessarily polled hardware");
                    output.NextPublish = null; service.Tick();
                    Assert(entered.Wait(TimeSpan.FromSeconds(2)) && calls == 1, "successful RTSS publication did not start sampling");
                    for (int i = 0; i < 3; i++) { clock.Milliseconds += 2500; service.Tick(); }
                    Assert(calls == 1, "reconnected output overlapped a retained read");
                }
                finally { release.Set(); }
            }
        });
    }

    private static RtssOverlayService RtssService(RtssClock clock, RtssFakeOutput output, Func<Response> read = null)
    {
        return new RtssOverlayService(read ?? (() => null), output, true, false, () => clock.Utc, () => clock.Milliseconds);
    }
    private static Response RtssSample(DateTime time, int cpu = 55)
    {
        return new Response { Success = true, State = new Snapshot { Supported = true, CapturedAtUtc = time.ToString("O"),
            Values = new[] { new Reading { Key = "CpuTemperature", Value = cpu, Unit = "°C", Source = "AMD ADL", RawValue = 555, Diagnostic = "private raw note" } } } };
    }
    private sealed class RtssClock
    {
        internal DateTime Utc = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        internal long Milliseconds;
    }
    private sealed class RtssFakeOutput : IRtssOsdOutput
    {
        internal int Publishes, Releases, Disposals, BusyReleases;
        internal bool ThrowPublish, SawNinetyNine;
        internal string LastText;
        internal RtssPublishResult NextPublish;
        public RtssPublishResult Publish(string text)
        {
            Assert(Disposals == 0, "publish after output disposal");
            Publishes++; LastText = text; SawNinetyNine |= text.Contains("99");
            if (ThrowPublish) throw new InvalidOperationException("C:\\secret\\native.log");
            return NextPublish ?? new RtssPublishResult { State = "published", Published = true };
        }
        public RtssPublishResult Release()
        {
            Assert(Disposals == 0, "release after output disposal"); Releases++;
            return BusyReleases-- > 0 ? new RtssPublishResult { State = "busy", CleanupPending = true }
                : new RtssPublishResult { State = "released" };
        }
        public void Dispose() { Disposals++; }
    }
}
