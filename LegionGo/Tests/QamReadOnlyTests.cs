// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Text;
using LegionTweaks;
using LegionTweaks.QamProbe;

internal static partial class Program
{
    private static void QamReadOnlyTests()
    {
        Test("QAM read-only endpoint rejects all commands and mixed context before calling providers", () => {
            int reads = 0;
            var service = new ReadOnlyStatusService(true, () => { reads++; return null; },
                () => { reads++; return null; }, () => { reads++; return null; });
            foreach (string operation in new[] { null, "", "status", "set", "tdp", "profile_apply", "restore", "display_resolution",
                "display_refresh_rate", "display_recover", "display_confirm", "lossless_open", "controller_stop", "recording_start", "remap_save", "amd_graphics_apply" })
                Assert(!service.Execute(new Request { Operation = operation }).Success, "command escaped read-only channel: " + operation);
            var mutations = new Action<Request>[] {
                r => r.Version++, r => r.Key = "Mode", r => r.Values = new int[0], r => r.Ac = true,
                r => r.Profile = new ProfileDefinition(), r => r.RtssAppearance = new RtssAppearance(),
                r => r.Display = new DisplayRequest(), r => r.GameBarInput = new GameBarInputContext(),
                r => r.Remap = new RemapEditingRequest(), r => r.AmdGraphics = new AmdGraphicsRequest()
            };
            foreach (string operation in new[] { "telemetry", "display_status", "lossless_status" }) foreach (var mutate in mutations) {
                var request = new Request { Operation = operation }; mutate(request);
                Assert(!service.Execute(Protocol.Decode<Request>(Protocol.Encode(request))).Success, "mixed payload accepted");
            }
            Assert(!service.Execute(null).Success && reads == 0, "rejected request reached a provider");
        });
        Test("QAM read-only hardware gate blocks every provider independently of USB IDs", () => {
            int calls = 0;
            var service = new ReadOnlyStatusService(false, () => { calls++; return null; },
                () => { calls++; return null; }, () => { calls++; return null; });
            foreach (string operation in new[] { "telemetry", "display_status", "lossless_status" })
                Assert(!service.Execute(new Request { Operation = operation }).Success, "unsupported device accepted");
            Assert(calls == 0, "unsupported device queried hardware");
        });
        Test("QAM power query reads the engine without saving originals or writing hardware", () => {
            var hardware = new Fake(); var journal = new Journal(); var engine = new ControlEngine(hardware, journal);
            var service = new ReadOnlyStatusService(true, () => engine.Execute(new Request { Operation = "telemetry" }), null, null);
            var result = service.Execute(new Request { Operation = "telemetry" });
            Assert(result.Success && result.State.Values.Any(x => x.Key == "Spl") && result.State.CapturedAtUtc != null, "power result missing");
            Assert(hardware.Writes.Count == 0 && journal.Saves == 0 && hardware.Reads.Count == 4, "query wrote or read unrelated settings");
            Assert(result.Controller == null && result.Recording == null && result.LegionSpace == null, "general session data leaked");
        });
        Test("QAM display read leaves the widget selection token valid and cannot issue a trial", () => WithRecordingDirectory(folder => {
            var backend = new DisplayBackendFake();
            using (var display = new DisplayControlService(folder, true, backend)) {
                var widget = display.Inspect();
                var service = new ReadOnlyStatusService(true, null, () => backend.Query().Info, null);
                var query = service.Execute(new Request { Operation = "display_status" });
                Assert(query.Success && query.Display.Info.Current.Same(widget.Info.Current) && query.Display.SnapshotId == null
                    && query.Display.TrialToken == null && !query.Display.Pending, "read response exposed command/trial state");
                Assert(backend.Tests == 0 && backend.Applies == 0 && backend.Restores == 0 && !Directory.EnumerateFiles(folder).Any(), "display read wrote");
                var selected = display.ApplyResolution(new DisplayRequest { SnapshotId = widget.SnapshotId, ResolutionHeight = 1600 });
                Assert(selected.Error == null && backend.Applies == 0, "QAM query invalidated the widget token");
            }
        }));
        Test("QAM read-only failures discard payloads and preserve unknown Lossless process state", () => {
            var failed = new ReadOnlyStatusService(true, () => new Response { Success = false, State = new Snapshot(), Message = "sensor failed" },
                () => { throw new IOException("monitor failed"); }, () => new LosslessScalingStatus { Supported = true, Installed = true, Running = null });
            var power = failed.Execute(new Request { Operation = "telemetry" });
            var display = failed.Execute(new Request { Operation = "display_status" });
            var lossless = failed.Execute(new Request { Operation = "lossless_status" });
            Assert(!power.Success && power.State == null && !display.Success && display.Display == null, "failed read kept values");
            Assert(lossless.Success && lossless.LosslessScaling.Running == null && lossless.SupportsLosslessScaling, "unknown process became stopped");
        });
        Test("QAM power text separates stored custom limits from measured ASIC power and source", () => {
            DateTime now = DateTime.UtcNow;
            var state = new Snapshot { Supported = true, OnAc = false, CapturedAtUtc = now.ToString("O"), Values = new[] {
                new Reading { Key = "Mode", Value = 2 }, new Reading { Key = "Spl", Value = 15 }, new Reading { Key = "Sppt", Value = 20 },
                new Reading { Key = "Fppt", Value = 25 }, new Reading { Key = "AsicPower", Value = 9, Unit = "W", Source = "AMD test ASIC" },
                new Reading { Key = "CpuPower", Value = 8, Unit = "W" }, new Reading { Key = "GpuPower", Value = 7, Unit = "W" }
            } };
            string text = QamReadText.Render(QamQuery.Power, new Response { Success = true, State = state }, now);
            Assert(text.Contains("Personalizado inactivo") && text.Contains("Potencia ASIC: 9 W") && text.Contains("AMD test ASIC")
                && text.Contains("Alimentación: DC") && !text.Contains("Potencia ASIC: 15 W"), "invented combined power or active custom limit");
            state.Values[4].Error = "failed";
            Assert(QamReadText.Render(QamQuery.Power, new Response { Success = true, State = state }, now).Contains("Potencia ASIC: N/D"), "error became a measurement");
            state.Values[4].Error = null; state.Values[4].Unit = "mW";
            Assert(QamReadText.Render(QamQuery.Power, new Response { Success = true, State = state }, now).Contains("Potencia ASIC: N/D"), "wrong unit relabeled as watts");
        });
        Test("QAM reading timestamps become old and never substitute receipt time for missing capture", () => {
            DateTime now = DateTime.UtcNow;
            var info = new DisplayInfo { Current = new DisplayMode { Width = 1600, Height = 1000, Frequency = 144 }, ActiveDisplays = 1, CapturedAtUtc = now.ToString("O") };
            var response = new Response { Success = true, Display = new DisplayStatus { Supported = true, Info = info } };
            Assert(QamReadText.Render(QamQuery.Display, response, now).Contains("1600 × 1000") && QamReadText.Render(QamQuery.Display, response, now).Contains("144 Hz"), "display query lost current mode");
            Assert(QamReadText.Render(QamQuery.Display, response, now.AddSeconds(7)).Contains("Lectura anterior"), "old reading looks current");
            Assert(QamReadText.Render(QamQuery.Display, response, now, true).Contains("Lectura anterior"), "expired receipt revived after clock correction");
            Assert(QamReadText.Render(QamQuery.Display, response, now.AddSeconds(-5)).Contains("futura sin confirmar"), "future reading looks current");
            info.CapturedAtUtc = null;
            Assert(QamReadText.Render(QamQuery.Display, response, now).Contains("Hora de lectura sin confirmar"), "missing capture got a new timestamp");
            response.Success = false; response.Message = "failed";
            Assert(!QamReadText.Render(QamQuery.Display, response, now).Contains("1600"), "failure retained old display data");
        });
        Test("QAM Lossless text distinguishes running unknown and absent without claiming active scaling", () => {
            DateTime now = DateTime.UtcNow;
            var value = new LosslessScalingStatus { Supported = true, Installed = true, CapturedAtUtc = now.ToString("O"), FileVersion = "3.2", Running = null };
            var response = new Response { Success = true, LosslessScaling = value };
            string text = QamReadText.Render(QamQuery.Lossless, response, now);
            Assert(text.Contains("Proceso: Sin confirmar") && text.Contains("Versión: 3.2") && text.Contains("no confirma escalado ni LSFG activo"), "claimed unknown process or scaling state");
            value.Running = true;
            Assert(QamReadText.Render(QamQuery.Lossless, response, now).Contains("Proceso: En ejecución"), "running process lost");
            value.Running = false; value.Installed = false;
            Assert(QamReadText.Render(QamQuery.Lossless, response, now).Contains("Instalado: No detectado"), "absent installation invented");
        });
        Test("QAM pipe framing rejects truncated and oversized messages", () => {
            foreach (string bad in new[] { "{}", new string('x', Protocol.MaxMessageLength + 1) + "\n" }) {
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(bad))) using (var reader = new StreamReader(stream)) {
                    bool rejected = false;
                    try { ReadOnlyQueries.ReadLine(reader).GetAwaiter().GetResult(); }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException) { rejected = true; }
                    Assert(rejected, "invalid IPC framing accepted");
                }
            }
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}\r\n"))) using (var reader = new StreamReader(stream))
                Assert(ReadOnlyQueries.ReadLine(reader).GetAwaiter().GetResult() == "{}\r", "normal CRLF failed");
        });
    }
}
