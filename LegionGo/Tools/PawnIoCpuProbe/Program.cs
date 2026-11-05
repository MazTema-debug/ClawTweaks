// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LegionTweaks.CpuProbe
{
    internal static class Program
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private static volatile bool cancelled;
        private static int Main(string[] args)
        {
            string run = null;
            var report = new Dictionary<string, object> {
                { "StartedAtUtc", DateTime.UtcNow.ToString("O") }, { "ProbeVersion", 2 },
                { "ModuleRelease", CpuProbeModule.Release }, { "ModuleCommit", CpuProbeModule.Commit }, { "ModuleSha256", CpuProbeModule.Sha256 },
                { "AllowedMsr", new[] { "C0010293", "C00000E7", "C00000E8" } },
                { "FrequencyMeaning", "EffectiveCoreMHz = arithmetic mean of both SMT logical effective frequencies in the current sweep, including observed idle zero; missing/invalid sibling gives N/D. Logical frequencies use APERF delta / monotonic seconds / 1e6. SessionPeakCoreMHz = largest observed hardware-Pstate FID/DFS frequency, 200 MHz reference, across siblings during this session. Candidate readings pending independent comparison." },
                { "PrimarySamplesFile", "cores.csv" }, { "DiagnosticSamplesFile", "samples.csv" },
                { "MsrReads", 0 }, { "MsrWrites", 0 }, { "ExecutorClosed", false }, { "Completed", false }
            };
            int exit = 1;
            try {
                var options = Parse(args);
                if (!Environment.Is64BitProcess) throw new InvalidOperationException("La sonda requiere proceso x64.");
                if (!Directory.Exists(options.Output)) throw new DirectoryNotFoundException("Crea primero la carpeta privada de salida.");
                if ((File.GetAttributes(options.Output) & FileAttributes.ReparsePoint) != 0) throw new IOException("Carpeta de salida no admitida.");
                run = Path.Combine(Path.GetFullPath(options.Output), "cpu-probe-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(run);
                report["ReadRequested"] = options.Read; report["Label"] = options.Label;
                report["SecondsRequested"] = options.Seconds; report["IntervalMs"] = options.Interval;
                report["Elevated"] = CpuProbeNative.IsAdministrator;
                var device = CpuProbeNative.Device(); report["Device"] = device;
                if (!device.Supported) throw new InvalidOperationException("Modelo/CPU/topología WMI no soportados por esta sonda Z1 Extreme original.");
                var targets = CpuProbeNative.Topology(); report["Targets"] = targets;
                string affinity = CpuProbeNative.CurrentAffinity(); report["OriginalThreadAffinity"] = affinity;
                if (options.Read) {
                    if (!CpuProbeNative.IsAdministrator) throw new UnauthorizedAccessException("Lectura pendiente: abre esta sonda como administrador con --read. No se eleva automáticamente.");
                    byte[] module = CpuProbeModule.Read(options.Module);
                    Console.CancelKeyPress += (s, e) => { cancelled = true; e.Cancel = true; };
                    var sweepMs = new List<double>(); int errors = 0, samples = 0, effective = 0, invalidCounters = 0, coreSamples = 0, effectiveCores = 0;
                    var coreAverages = new CpuProbeCoreAverages(targets);
                    CpuCoreEffectiveSample[] lastCores = null;
                    var progress = new HashSet<string>();
                    var coreProgress = new HashSet<int>();
                    var peaks = new CpuProbePeaks();
                    var process = Process.GetCurrentProcess(); TimeSpan cpuStart = process.TotalProcessorTime;
                    var wall = Stopwatch.StartNew();
                    using (var api = new CpuPawnNative()) {
                        report["LibraryVersion"] = api.LibraryVersion; report["LibrarySha256"] = api.LibrarySha256;
                        report["ApiVersion"] = api.ApiVersion;
                        using (var reader = new CpuProbeReader(api, module)) {
                            try {
                                using (var file = new FileStream(Path.Combine(run, "samples.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                                using (var csv = new StreamWriter(file, new UTF8Encoding(false)))
                                using (var coreFile = new FileStream(Path.Combine(run, "cores.csv"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                                using (var coreCsv = new StreamWriter(coreFile, new UTF8Encoding(false))) {
                                    csv.WriteLine("sweep,utc,core,smt_sibling,group,logical,pstate_raw_hex,aperf_raw_hex,mperf_raw_hex,qpc_ticks,aperf_read_us,effective_logical_mhz,aperf_mperf_ratio,counter_state,session_peak_core_mhz,peak_observed_utc,error");
                                    coreCsv.WriteLine("sweep,utc,core,effective_core_mhz,core_state,session_peak_core_mhz,peak_observed_utc");
                                    var counters = new CpuProbeCounters(Stopwatch.Frequency); int sweep = 0;
                                    long samplingStart = Stopwatch.GetTimestamp(), next = samplingStart;
                                    while (!cancelled && (Stopwatch.GetTimestamp() - samplingStart) / (double)Stopwatch.Frequency < options.Seconds) {
                                        var timer = Stopwatch.StartNew();
                                        var currentSweep = new Dictionary<string, CpuCounterResult>();
                                        foreach (var target in targets) {
                                            if (cancelled) break;
                                            ulong? pstate = null, aperf = null, mperf = null; long stamp = 0, ioTicks = 0;
                                            var faults = new List<string>(); string utc = DateTime.UtcNow.ToString("O");
                                            CpuProbeNative.OnProcessor(target, () => {
                                                pstate = Read(reader, CpuProbeMsr.HardwarePstate, faults);
                                                mperf = Read(reader, CpuProbeMsr.Mperf, faults);
                                                long before = Stopwatch.GetTimestamp();
                                                aperf = Read(reader, CpuProbeMsr.Aperf, faults);
                                                long after = Stopwatch.GetTimestamp(); ioTicks = after - before; stamp = before + ioTicks / 2;
                                            });
                                            CpuCounterResult counter;
                                            if (aperf.HasValue && mperf.HasValue && ioTicks / (double)Stopwatch.Frequency <= 0.01) {
                                                counter = counters.Observe(target.Key, new CpuCounterReading { Aperf = aperf.Value, Mperf = mperf.Value, Timestamp = stamp });
                                            }
                                            else { counters.Invalidate(target.Key); counter = new CpuCounterResult { State = "read-error-or-delay" }; }
                                            currentSweep[target.Key] = counter;
                                            var peak = peaks.Observe(target.Core, pstate, utc);
                                            if (!pstate.HasValue || !CpuProbeCounters.PstateMHz(pstate.Value).HasValue) faults.Add("Pstate ausente o no decodificable");
                                            if (faults.Count != 0) errors++;
                                            if (counter.EffectiveLogicalMHz.HasValue) effective++;
                                            if (counter.EffectiveLogicalMHz > 0) progress.Add(target.Key);
                                            if (counter.State != "warmup" && counter.State != "sampled" && counter.State != "no-cycles-observed") invalidCounters++;
                                            samples++;
                                            csv.WriteLine(string.Join(",", sweep.ToString(Invariant), utc, target.Core.ToString(Invariant), target.Sibling.ToString(Invariant),
                                                target.Group.ToString(Invariant), target.Logical.ToString(Invariant), Hex(pstate), Hex(aperf), Hex(mperf), stamp.ToString(Invariant),
                                                (ioTicks * 1000000.0 / Stopwatch.Frequency).ToString("F3", Invariant), Number(counter.EffectiveLogicalMHz), Number(counter.AperfMperfRatio),
                                                counter.State, Number(peak?.MHz), peak?.ObservedAtUtc ?? "", Escape(string.Join("; ", faults))));
                                        }
                                        lastCores = coreAverages.Average(currentSweep);
                                        var currentPeaks = peaks.GetAll().ToDictionary(x => x.Core);
                                        string coreUtc = DateTime.UtcNow.ToString("O");
                                        foreach (var core in lastCores) {
                                            CpuSessionPeak peak; currentPeaks.TryGetValue(core.Core, out peak);
                                            coreCsv.WriteLine(string.Join(",", sweep.ToString(Invariant), coreUtc, core.Core.ToString(Invariant),
                                                Number(core.EffectiveCoreMHz), core.State, Number(peak?.MHz), peak?.ObservedAtUtc ?? ""));
                                            coreSamples++;
                                            if (core.EffectiveCoreMHz.HasValue) { effectiveCores++; coreProgress.Add(core.Core); }
                                        }
                                        report["LastCoreSweepAtUtc"] = coreUtc;
                                        csv.Flush(); coreCsv.Flush(); sweepMs.Add(timer.Elapsed.TotalMilliseconds); sweep++;
                                        next += (long)(options.Interval * (Stopwatch.Frequency / 1000.0));
                                        long now = Stopwatch.GetTimestamp();
                                        if (next < now) next = now; // Never burst to replay missed samples.
                                        while (!cancelled && (next - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency > 0.001) Thread.Sleep(1);
                                    }
                                }
                            }
                            finally { report["MsrReads"] = reader.Reads; }
                        }
                        report["ExecutorClosed"] = true;
                    }
                    report["SessionPeaksByCore"] = peaks.GetAll(); report["SampleRows"] = samples;
                    report["RowsWithReadErrors"] = errors; report["EffectiveRows"] = effective;
                    report["CoreSampleRows"] = coreSamples; report["EffectiveCoreRows"] = effectiveCores;
                    report["CoresWithEffectiveSamples"] = coreProgress.Count;
                    report["LastCoreReadings"] = lastCores;
                    report["LogicalProcessorsWithCounterProgress"] = progress.Count; report["InvalidCounterRows"] = invalidCounters;
                    report["SweepMeanMs"] = sweepMs.Count == 0 ? 0 : sweepMs.Average(); report["SweepMaxMs"] = sweepMs.Count == 0 ? 0 : sweepMs.Max();
                    report["ProcessCpuMs"] = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                    report["WallSeconds"] = wall.Elapsed.TotalSeconds; report["Cancelled"] = cancelled;
                    report["DataObserved"] = errors == 0 && progress.Count == 16 && coreProgress.Count == 8 && peaks.GetAll().Length == 8;
                    if (errors != 0 || progress.Count != 16 || coreProgress.Count != 8 || peaks.GetAll().Length != 8) throw new IOException("Captura terminada con lecturas incompletas; revisa CSV y resumen.");
                }
                string afterAffinity = CpuProbeNative.CurrentAffinity(); report["FinalThreadAffinity"] = afterAffinity;
                report["AffinityRestored"] = affinity == afterAffinity;
                if (affinity != afterAffinity) throw new IOException("La afinidad final no coincide con la original.");
                report["Completed"] = true; exit = 0;
            }
            catch (Exception ex) { report["Error"] = ex.GetType().Name + ": " + ex.Message; Console.Error.WriteLine(report["Error"]); }
            finally {
                if (report.ContainsKey("OriginalThreadAffinity")) {
                    try {
                        string final = CpuProbeNative.CurrentAffinity(); report["FinalThreadAffinity"] = final;
                        report["AffinityRestored"] = (string)report["OriginalThreadAffinity"] == final;
                        if (!(bool)report["AffinityRestored"]) { report["Completed"] = false; exit = 1; report["AffinityError"] = "La afinidad final difiere de la original."; }
                    }
                    catch (Exception ex) { report["AffinityRestored"] = false; report["AffinityError"] = ex.Message; report["Completed"] = false; exit = 1; }
                }
                report["FinishedAtUtc"] = DateTime.UtcNow.ToString("O");
                string json = new JavaScriptSerializer().Serialize(report);
                if (run != null) {
                    using (var output = new FileStream(Path.Combine(run, "summary.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(output, new UTF8Encoding(false))) writer.Write(json);
                }
                Console.WriteLine(json);
            }
            return exit;
        }
        private static ulong? Read(CpuProbeReader reader, uint msr, List<string> faults)
        { try { return reader.Read(msr); } catch (IOException ex) { faults.Add(ex.Message); return null; } }
        private static string Number(double? value) => value.HasValue ? value.Value.ToString("F3", Invariant) : "";
        private static string Hex(ulong? value) => value.HasValue ? value.Value.ToString("X16", Invariant) : "";
        private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        private sealed class Options { internal bool Read; internal string Module, Output, Label = "baseline"; internal int Seconds = 10, Interval = 500; }
        private static Options Parse(string[] args)
        {
            var value = new Options(); var seen = new HashSet<string>();
            for (int i = 0; i < args.Length; i++) {
                string key = args[i];
                if (!seen.Add(key)) throw new ArgumentException("Opción repetida: " + key);
                if (key == "--read") { value.Read = true; continue; }
                if (++i >= args.Length) throw new ArgumentException("Falta valor para " + key);
                switch (key) {
                    case "--module": value.Module = Path.GetFullPath(args[i]); break;
                    case "--output": value.Output = Path.GetFullPath(args[i]); break;
                    case "--label": value.Label = args[i]; break;
                    case "--seconds": value.Seconds = int.Parse(args[i], Invariant); break;
                    case "--interval-ms": value.Interval = int.Parse(args[i], Invariant); break;
                    default: throw new ArgumentException("Opción desconocida: " + key);
                }
            }
            if (value.Output == null || (value.Read && value.Module == null) || value.Seconds < 2 || value.Seconds > 60
                || value.Interval < 250 || value.Interval > 2000 || value.Label.Length > 32 || value.Label.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ArgumentException("Uso: --output carpeta [--read --module AMDFamily17.bin] [--seconds 2..60] [--interval-ms 250..2000] [--label nombre]. Sin --read solo inventaría equipo/topología.");
            return value;
        }
    }
}
