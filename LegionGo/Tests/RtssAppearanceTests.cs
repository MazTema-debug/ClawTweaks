// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Text;
using LegionTweaks;

internal static partial class Program
{
    private static void RtssAppearanceTests()
    {
        Test("RTSS draft and saved summaries distinguish scale level orientation and custom metric count", () => {
            var saved = new RtssAppearance { Level = RtssLevel.Basic, Orientation = RtssOrientation.Horizontal, Scale = 125 };
            var draft = saved.Copy(); draft.Scale = 150;
            Assert(saved.Describe().Contains("125 %") && draft.Describe().Contains("150 %") && saved.Scale == 125, "draft presentation replaced saved size");
            draft.Level = RtssLevel.Custom; draft.Orientation = RtssOrientation.Vertical; draft.Metrics = new[] { RtssMetric.FrameRate, RtssMetric.Battery };
            Assert(draft.Describe() == "Personalizado · Vertical · 150 % · 2 métricas" && saved.Describe() == "Básico · Horizontal · 125 %", "summary omitted the choice that will be applied");
        });
        Test("RTSS appearance defaults preserve the prior complete layout", () => {
            var sample = RtssTextSample(RtssReading("CpuPower", 7, "W"));
            Assert(RtssOverlayText.Format(sample, new RtssAppearance()) == RtssOverlayText.Format(sample), "default OSD changed");
            Assert(new RtssAppearance().Level == RtssLevel.Full && new RtssAppearance().Scale == 100, "defaults changed");
        });
        Test("RTSS levels select frame metrics and sensors independently", () => {
            var sample = RtssTextSample(RtssReading("CpuTemperature", 50, "°C"), RtssReading("GpuTemperature", 48, "°C"), RtssReading("Battery", 80));
            var appearance = new RtssAppearance { Level = RtssLevel.Minimal };
            string text = RtssOverlayText.Format(sample, appearance);
            Assert(text.Contains("<FR> FPS") && text.Contains("<FT> ms") && !text.Contains("CPU") && !text.Contains("BAT"), "minimal includes sensors");
            appearance.Level = RtssLevel.Basic; text = RtssOverlayText.Format(sample, appearance);
            Assert(text.Contains("CPU 50 C") && text.Contains("GPU 48 C") && text.Contains("BAT 80 % AC") && !text.Contains("ASIC"), "basic selection wrong");
        });
        Test("RTSS custom ordering keeps GFX ASIC CPU and limits separate", () => {
            var appearance = new RtssAppearance { Level = RtssLevel.Custom,
                Metrics = new[] { RtssMetric.AsicPower, RtssMetric.CpuPower, RtssMetric.GfxPower, RtssMetric.CustomLimits } };
            string text = RtssOverlayText.Format(RtssTextSample(RtssReading("AsicPower", 8, "W"), RtssReading("CpuPower", 3, "W"),
                RtssReading("GpuPower", 6, "W"), RtssReading("Mode", 255), RtssReading("Spl", 15), RtssReading("Sppt", 20), RtssReading("Fppt", 25)), appearance);
            Assert(text.Contains("\nASIC 8 W\nCPU 3 W\nGFX 6 W\nLIMITES custom SPL/SPPT/FPPT 15/20/25 W"), "domains/order changed");
            Assert(!text.Contains("<FR>") && !text.Contains("<FT>"), "unselected metrics emitted");
        });
        Test("RTSS configurable metrics reject invalid units metadata and unsupported sensors", () => {
            var appearance = new RtssAppearance { Level = RtssLevel.Custom,
                Metrics = new[] { RtssMetric.GfxPower, RtssMetric.GpuVoltage, RtssMetric.CpuTemperature } };
            var sample = RtssTextSample(new Reading { Key = "GpuPower", Value = 20, Unit = "W", SensorSupported = 0, RawValue = 7 },
                RtssReading("GpuVoltageMv", 850, "W"), RtssReading("CpuTemperature", 0, "°C"));
            string text = RtssOverlayText.Format(sample, appearance);
            Assert(text.Contains("GFX N/D W") && text.Contains("GPU N/D V") && text.Contains("CPU N/D C"), "invalid sensor promoted");
        });
        Test("RTSS appearance validates versions enums scale uniqueness and bounded arrays", () => {
            var invalid = new[] { new RtssAppearance { Version = 2 }, new RtssAppearance { Level = (RtssLevel)9 },
                new RtssAppearance { Orientation = (RtssOrientation)(-1) }, new RtssAppearance { Scale = 101 },
                new RtssAppearance { Metrics = null }, new RtssAppearance { Metrics = new RtssMetric[0] },
                new RtssAppearance { Metrics = new[] { RtssMetric.FrameRate, RtssMetric.FrameRate } },
                new RtssAppearance { Metrics = new[] { (RtssMetric)99 } }, new RtssAppearance { Metrics = new RtssMetric[15] } };
            foreach (var value in invalid) {
                bool rejected = false; try { value.Encode(); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "invalid appearance accepted");
            }
        });
        Test("RTSS preference encoding round trips custom ordering and copies arrays", () => {
            var value = new RtssAppearance { Level = RtssLevel.Custom, Orientation = RtssOrientation.Horizontal, Scale = 125,
                Metrics = new[] { RtssMetric.Battery, RtssMetric.FrameTime, RtssMetric.GpuClock } };
            var copy = RtssAppearance.Decode(value.Encode());
            Assert(copy.Encode() == value.Encode() && !ReferenceEquals(copy.Metrics, value.Metrics), "roundtrip lost preferences");
            var detached = value.Copy(); var effective = value.EffectiveMetrics(); detached.Metrics[0] = RtssMetric.Fan; effective[0] = RtssMetric.Fan;
            Assert(value.Metrics[0] == RtssMetric.Battery, "mutable array leaked");
        });
        Test("RTSS malformed missing required and oversized preferences fail without defaults", () => {
            foreach (string json in new[] { null, "", "{", "null", "{}", "{\"Version\":2}", new string('x', RtssAppearance.MaximumBytes + 1) }) {
                bool rejected = false; try { RtssAppearance.Decode(json); } catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "invalid file became defaults");
            }
        });
        Test("RTSS layout size markup is bounded and restores the font attribute", () => {
            foreach (int scale in new[] { 75, 100, 125, 150 }) foreach (RtssLevel level in Enum.GetValues(typeof(RtssLevel))) {
                var appearance = new RtssAppearance { Level = level, Scale = scale, Orientation = RtssOrientation.Horizontal };
                string text = RtssOverlayText.Format(RtssTextSample(), appearance);
                Assert(!text.Contains("\n") && Encoding.ASCII.GetByteCount(text) <= RtssOverlayText.MaximumBytes, "layout unbounded/multiline");
                Assert(scale == 100 ? !text.Contains("<S") : text.StartsWith("<S=" + scale + ">") && text.EndsWith("<S>"), "size tag leaks");
            }
        });
        Test("RTSS preview cannot advertise real FPS sample values or untrusted text", () => {
            var appearance = new RtssAppearance { Level = RtssLevel.Basic, Scale = 150 };
            string text = RtssOverlayText.Preview(appearance);
            Assert(text.Contains("[FPS RTSS]") && text.Contains("N/D") && !text.Contains("<"), "preview injects RTSS markup or fake values");
            Assert(RtssOverlayText.Format(null, appearance) == RtssOverlayText.Unavailable, "stale null promoted by appearance");
            Assert(RtssOverlayText.Format(new Snapshot { Supported = false }, appearance) == RtssOverlayText.Unavailable, "unsupported promoted");
        });
        Test("RTSS appearance IPC keeps old helper capabilities opt in", () => {
            var old = Protocol.Decode<Response>("{\"Version\":1,\"Success\":true,\"Rtss\":{\"Supports\":true}}");
            Assert(!old.Rtss.SupportsAppearance && old.Rtss.Appearance == null && !old.SupportsOptiScalerMenu, "old helper gained capabilities");
            var request = new Request { Operation = "rtss_configure", RtssAppearance = new RtssAppearance() };
            var copy = Protocol.Decode<Request>(Protocol.Encode(request));
            Assert(copy.RtssAppearance.Encode() == request.RtssAppearance.Encode() && !Protocol.Encode(new Request()).Contains("RtssAppearance"), "optional IPC broken");
        });
        Test("RTSS changing appearance while OFF does not start publish or sampling", () => {
            int reads = 0; var output = new AppearanceOutput();
            using (var overlay = new RtssOverlayService(() => { reads++; return null; }, output, true, false)) {
                var appearance = new RtssAppearance { Level = RtssLevel.Minimal };
                overlay.Configure(appearance); overlay.Tick();
                appearance.Level = RtssLevel.Full;
                var status = overlay.GetStatus(); status.Appearance.Level = RtssLevel.Custom;
                Assert(!status.Enabled && output.Publishes == 0 && reads == 0 && overlay.GetStatus().Appearance.Level == RtssLevel.Minimal, "configure enabled or leaked state");
            }
        });
        Test("RTSS appearance edits reuse samples but never renew their freshness", () => {
            var now = DateTime.UtcNow; long milliseconds = 0; int reads = 0; var output = new AppearanceOutput();
            using (var overlay = new RtssOverlayService(() => { reads++; return null; }, output, true, false, () => now, () => milliseconds)) {
                overlay.SetEnabled(true);
                var sample = RtssTextSample(RtssReading("CpuTemperature", 51, "°C")); sample.CapturedAtUtc = now.ToString("O");
                overlay.Observe(new Response { Success = true, State = sample });
                overlay.Configure(new RtssAppearance { Level = RtssLevel.Basic }); overlay.Tick();
                Assert(reads == 0 && output.Last.Contains("CPU 51 C"), "configure added sensor query");
                now = now.AddSeconds(20); milliseconds = 20000;
                overlay.Configure(new RtssAppearance { Level = RtssLevel.Minimal });
                Assert(!overlay.GetStatus().Publishing, "appearance renewed cached freshness");
                // A missing RTSS output prevents optional sampling on this tick.
                output.Accept = false; overlay.Tick();
                Assert(output.Last.Contains("Telemetria N/D") && !output.Last.Contains("<FR>") && reads == 0, "stale data/macros survived layout change");
            }
        });
        Test("RTSS appearance session rejects invalid requests before activation and energy", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var output = new SessionRtss();
            using (var session = new HelperSession(new ControlEngine(hardware, journal), folder, false, rtss: output)) {
                foreach (var request in new[] { new Request { Operation = "rtss_configure" },
                    new Request { Operation = "rtss_configure", Values = new[] { 1 }, RtssAppearance = new RtssAppearance() },
                    new Request { Operation = "rtss_set", Values = new[] { 1 }, RtssAppearance = new RtssAppearance { Scale = -1 } },
                    new Request { Operation = "rtss_set", Values = new[] { 0 }, RtssAppearance = new RtssAppearance() } })
                    Assert(!session.Execute(request).Success, "invalid appearance request accepted");
                Assert(output.Sets == 0 && output.Configures == 0, "invalid request mutated output");
                Assert(session.Execute(new Request { Operation = "rtss_configure", RtssAppearance = new RtssAppearance() }).Success
                    && output.Configures == 1 && !output.Enabled, "configuration activated output");
                Assert(session.Execute(new Request { Operation = "rtss_set", Values = new[] { 1 }, RtssAppearance = new RtssAppearance() }).Success
                    && output.Configures == 2 && output.Enabled, "combined configure/enable failed");
                Assert(hardware.Reads.Count == 0 && hardware.Writes.Count == 0 && journal.Saves == 0, "appearance touched power/sensors");
            }
        }));
    }
    private sealed class AppearanceOutput : IRtssOsdOutput
    {
        internal int Publishes; internal string Last; internal bool Accept = true;
        public RtssPublishResult Publish(string text) { Publishes++; Last = text; return new RtssPublishResult { Published = Accept, State = Accept ? "published" : "missing" }; }
        public RtssPublishResult Release() => new RtssPublishResult { State = "released" };
        public void Dispose() { }
    }
}
