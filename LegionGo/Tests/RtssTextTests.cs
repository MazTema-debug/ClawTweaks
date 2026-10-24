// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using LegionTweaks;

internal static partial class Program
{
    private static Snapshot RtssTextSample(params Reading[] values)
    {
        return new Snapshot { Supported = true, OnAc = true, Values = values };
    }

    private static Reading RtssReading(string key, int? value, string unit = null)
    {
        return new Reading { Key = key, Value = value, Unit = unit };
    }

    private static void RtssTextTests()
    {
        Test("RTSS display placement anchors one bounded block at the top-left margin without leaking markup into preview", () => {
            var appearance = new RtssAppearance { Scale = 150 };
            string text = RtssOverlayText.PlaceAtTopLeft(RtssOverlayText.Format(RtssTextSample(), appearance));
            Assert(text.StartsWith("<P=8,8><A=0>LEGIONTWEAKS") || text.StartsWith("<P=8,8><A=0><S=150>LEGIONTWEAKS"), "missing top-left position or left alignment");
            Assert(text.EndsWith("<A>\n") && text.Length <= RtssOverlayText.MaximumBytes
                && text.IndexOf("LEGIONTWEAKS", text.IndexOf("LEGIONTWEAKS") + 1, StringComparison.Ordinal) < 0, "block duplicates, overruns or lacks a final line boundary");
            Assert(!RtssOverlayText.Preview(appearance).Contains("<P="), "placement markup entered the UI preview");
            Assert(RtssOverlayText.PlaceAtTopLeft(new string('X', 4095)).Contains("Telemetria N/D"), "placement overflow was not bounded");
        });
        Test("RTSS text preserves CPU GFX ASIC and SoC domains without summing", () =>
        {
            string text = RtssOverlayText.Format(RtssTextSample(
                RtssReading("CpuTemperature", 55, "°C"), RtssReading("CpuPower", 3, "W"),
                RtssReading("GpuTemperature", 54, "°C"), RtssReading("GpuUsage", 98, "%"), RtssReading("GpuClockMHz", 1800, "MHz"),
                RtssReading("GpuPower", 17, "W"), RtssReading("AsicPower", 10, "W"),
                RtssReading("GpuVoltageMv", 808, "mV"), RtssReading("SocVoltageMv", 950, "mV")));
            Assert(text.Contains("CPU 55 C | 3 W") && text.Contains("GPU 54 C | 98 % | 1800 MHz"), "CPU/GPU readings lost");
            Assert(text.Contains("GFX 17 W | 0.808 V | ASIC 10 W | SoC 0.950 V"), "power or voltage domains merged or normalized");
            Assert(text.Split('\n').Length == 6, "compact line count changed");
        });
        Test("RTSS dynamic FPS and frame time use fixed per-game RTSS macros only", () =>
        {
            string text = RtssOverlayText.Format(RtssTextSample());
            Assert(text.StartsWith("LEGIONTWEAKS | <FR> FPS | <FT> ms | lectura N/D\n", StringComparison.Ordinal), "RTSS macros missing");
            Assert(text.Count(c => c == '<') == 2 && text.Count(c => c == '>') == 2, "unexpected RTSS markup");
            Assert(!RtssOverlayText.Unavailable.Contains("<FR>") && !RtssOverlayText.Unavailable.Contains("<FT>"), "unavailable content retains dynamic tags");
        });
        Test("RTSS null or unsupported snapshots never advertise game frame metrics", () =>
        {
            Assert(RtssOverlayText.Format(null) == RtssOverlayText.Unavailable, "null sample formatted");
            Assert(RtssOverlayText.Format(new Snapshot { Supported = true }) == RtssOverlayText.Unavailable, "missing readings formatted");
            Assert(RtssOverlayText.Format(new Snapshot { Supported = false, Values = new[] { RtssReading("CpuPower", 7, "W") } }) == RtssOverlayText.Unavailable, "unsupported sample formatted");
            Assert(RtssOverlayText.Unavailable == "LEGIONTWEAKS\nTelemetria N/D", "unavailable marker changed");
        });
        Test("RTSS validates error nullable values units and duplicate fields", () =>
        {
            var snapshot = RtssTextSample(null,
                new Reading { Key = "CpuPower", Value = 9, Unit = "W", Error = "" },
                RtssReading("CpuTemperature", 55, null), RtssReading("GpuTemperature", 54, "C"),
                RtssReading("GpuUsage", 100, "W"), RtssReading("GpuClockMHz", null, "MHz"),
                RtssReading("GpuVoltageMv", 800, "mV"), RtssReading("GpuVoltageMv", 900, "mV"),
                RtssReading("AsicPower", 7, "W"), new Reading { Key = "AsicPower", Error = "failure" });
            string text = RtssOverlayText.Format(snapshot);
            Assert(text.Contains("CPU N/D C | N/D W") && text.Contains("GPU N/D C | N/D % | N/D MHz"), "unvalidated reading promoted");
            Assert(text.Contains("GFX N/D W | N/D V | ASIC N/D W | SoC N/D V"), "ambiguous or missing reading promoted");
        });
        Test("RTSS valid zero readings remain zero while zero temperature and voltage are unavailable", () =>
        {
            string text = RtssOverlayText.Format(RtssTextSample(
                RtssReading("CpuTemperature", 0, "°C"), RtssReading("GpuTemperature", 0, "°C"),
                RtssReading("CpuPower", 0, "W"), RtssReading("GpuPower", 0, "W"), RtssReading("AsicPower", 0, "W"),
                RtssReading("GpuUsage", 0, "%"), RtssReading("GpuClockMHz", 0, "MHz"),
                RtssReading("GpuVoltageMv", 0, "mV"), RtssReading("SocVoltageMv", 0, "mV"),
                RtssReading("FanRpm", 0, "RPM"), RtssReading("Battery", 0)));
            Assert(text.Contains("CPU N/D C | 0 W") && text.Contains("GPU N/D C | 0 % | 0 MHz"), "zero validity confused");
            Assert(text.Contains("GFX 0 W | N/D V | ASIC 0 W | SoC N/D V") && text.Contains("FAN 0 RPM | BAT 0 % AC"), "zero reading lost");
        });
        Test("RTSS range boundaries reject sentinels instead of clamping or using raw values", () =>
        {
            var snapshot = RtssTextSample(
                RtssReading("CpuTemperature", 150, "°C"), RtssReading("GpuTemperature", 151, "°C"),
                RtssReading("CpuPower", 200, "W"), RtssReading("GpuPower", 201, "W"), RtssReading("AsicPower", -1, "W"),
                RtssReading("GpuUsage", 101, "%"), RtssReading("GpuClockMHz", 6001, "MHz"),
                RtssReading("GpuVoltageMv", 2500, "mV"), RtssReading("SocVoltageMv", 65535, "mV"),
                RtssReading("FanRpm", 15001, "RPM"), RtssReading("Battery", 255));
            snapshot.Values[4].RawValue = 8; snapshot.Values[4].SensorSupported = 1;
            string text = RtssOverlayText.Format(snapshot);
            Assert(text.Contains("CPU 150 C | 200 W") && text.Contains("GPU N/D C | N/D % | N/D MHz"), "range boundary changed");
            Assert(text.Contains("GFX N/D W | 2.500 V | ASIC N/D W | SoC N/D V") && text.Contains("FAN N/D RPM | BAT N/D % AC"), "sentinel/raw value promoted");
        });
        Test("RTSS all maxima fit the ASCII slot without culture-specific decimals", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-MX");
                var snapshot = RtssTextSample(
                    RtssReading("CpuTemperature", 150, "°C"), RtssReading("GpuTemperature", 150, "°C"),
                    RtssReading("CpuPower", 200, "W"), RtssReading("GpuPower", 200, "W"), RtssReading("AsicPower", 200, "W"),
                    RtssReading("GpuUsage", 100, "%"), RtssReading("GpuClockMHz", 6000, "MHz"),
                    RtssReading("GpuVoltageMv", 2500, "mV"), RtssReading("SocVoltageMv", 2500, "mV"),
                    RtssReading("FanRpm", 15000, "RPM"), RtssReading("Battery", 100),
                    RtssReading("Spl", 200), RtssReading("Sppt", 200), RtssReading("Fppt", 200), RtssReading("Mode", 2));
                string first = RtssOverlayText.Format(snapshot);
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                string second = RtssOverlayText.Format(snapshot);
                Assert(first == second && second.Contains("2.500 V") && !second.Contains("2,500"), "current culture changed RTSS numeric syntax");
                Assert(second.All(c => c <= 127 && (c >= 32 || c == '\n')) && Encoding.ASCII.GetByteCount(second) <= RtssOverlayText.MaximumBytes, "RTSS payload is not bounded ASCII");
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });
        Test("RTSS capture labels use strictly parsed UTC instead of untrusted timestamp text", () =>
        {
            var snapshot = RtssTextSample();
            foreach (string stamp in new[] { "2026-10-18T12:34:56.0000000Z", "2026-10-18T12:34:56.0000000+00:00", "2026-10-18T12:34:56.0000000-00:00" })
            {
                snapshot.CapturedAtUtc = stamp;
                Assert(RtssOverlayText.Format(snapshot).Contains(" | lectura 12:34:56 UTC\n"), "UTC capture not labelled");
            }
            foreach (string stamp in new[] { null, "", "2026-10-18T12:34:56.0000000", "2026-10-18T13:34:56.0000000+01:00", "18/10/2026 12:34:56",
                "2026-10-18T12:34:56.0000000Z<C=secret>", "2026-10-18T12:34:56Z" })
            {
                snapshot.CapturedAtUtc = stamp;
                string text = RtssOverlayText.Format(snapshot);
                Assert(text.Contains(" | lectura N/D\n") && !text.Contains("secret"), "ambiguous or injected capture label accepted");
            }
        });
        Test("RTSS contradictory unsupported sensor values are unavailable and raw values stay diagnostic", () =>
        {
            var power = RtssReading("GpuPower", 7, "W"); power.SensorSupported = 0; power.RawValue = 17;
            var voltage = RtssReading("GpuVoltageMv", 808, "mV"); voltage.SensorSupported = 0; voltage.RawValue = 900;
            var cpu = RtssReading("CpuPower", 3, "W"); cpu.SensorSupported = 1;
            string text = RtssOverlayText.Format(RtssTextSample(power, voltage, cpu));
            Assert(text.Contains("CPU N/D C | 3 W") && text.Contains("GFX N/D W | N/D V"), "unsupported reading promoted despite contradictory metadata");
        });
        Test("RTSS settings are labelled limits and retain the live custom-mode distinction", () =>
        {
            var mode = RtssReading("Mode", 255);
            var snapshot = RtssTextSample(mode, RtssReading("Spl", 15), RtssReading("Sppt", 20, "W"), RtssReading("Fppt", 25));
            Assert(RtssOverlayText.Format(snapshot).Contains("LIMITES custom SPL/SPPT/FPPT 15/20/25 W"), "active custom limits not labelled");
            mode.Value = 2;
            Assert(RtssOverlayText.Format(snapshot).Contains("LIMITES guardados (custom inactivo) SPL/SPPT/FPPT 15/20/25 W"), "inactive custom limits shown as active consumption");
            mode.Error = "missing"; snapshot.Values[1].Unit = "mW"; snapshot.OnAc = false;
            string text = RtssOverlayText.Format(snapshot);
            Assert(text.Contains("LIMITES (modo N/D) SPL/SPPT/FPPT N/D/20/25 W") && text.Contains(" % DC"), "unknown mode or mismatched limit unit accepted");
        });
        Test("RTSS output never interpolates metadata error raw or injected markup", () =>
        {
            string injected = "<C=FF0000><P=1>secret-path\nINJECTED";
            var reading = RtssReading("CpuPower", 6, "W"); reading.Source = injected; reading.Diagnostic = injected; reading.RawValue = int.MaxValue;
            var snapshot = RtssTextSample(reading, new Reading { Key = injected, Value = int.MaxValue, Unit = injected },
                new Reading { Key = "GpuPower", Value = 9, Unit = "W", Error = injected });
            snapshot.Device = new DeviceIdentity { Model = injected, Cpu = injected };
            string text = RtssOverlayText.Format(snapshot);
            Assert(text.Contains("CPU N/D C | 6 W") && text.Contains("GFX N/D W"), "validated value lost or error ignored");
            Assert(!text.Contains("INJECTED") && !text.Contains("secret") && !text.Contains(int.MaxValue.ToString(CultureInfo.InvariantCulture))
                && text.Count(c => c == '<') == 2, "external text reached RTSS markup");
        });
        Test("RTSS optional IPC status round-trips without changing legacy responses", () =>
        {
            var legacy = Protocol.Decode<Response>("{\"Version\":1,\"Success\":true,\"Message\":\"old\"}");
            Assert(legacy.Rtss == null && !Protocol.Encode(new Response()).Contains("Rtss"), "legacy response acquired RTSS capability");
            var original = new Response { Success = true, Rtss = new RtssOverlayStatus { Supports = true, Enabled = false, Publishing = false,
                CleanupPending = true, State = "busy", Message = "pending", Error = "retry", LastSampleAtUtc = "2026-10-18T12:00:00.0000000Z",
                LastPublishedAtUtc = "2026-10-18T12:00:01.0000000Z", Updates = 42 } };
            var copy = Protocol.Decode<Response>(Protocol.Encode(original)).Rtss;
            Assert(copy.Supports && !copy.Enabled && !copy.Publishing && copy.CleanupPending && copy.State == "busy" && copy.Message == "pending"
                && copy.Error == "retry" && copy.Updates == 42 && copy.LastSampleAtUtc == original.Rtss.LastSampleAtUtc
                && copy.LastPublishedAtUtc == original.Rtss.LastPublishedAtUtc, "RTSS state lost on IPC roundtrip");
        });
    }
}
