// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    // Sampling is owned by the helper, independent of any connected widget.
    // The reader uses ControlEngine's existing lock, so reads cannot split a TDP write batch.
    internal sealed class TelemetryRecorder : IDisposable
    {
        internal const int IntervalSeconds = 2;
        internal const long MaximumBytes = 64L * 1024 * 1024;
        private readonly object gate = new object();
        private readonly Func<Response> read;
        private readonly Func<DateTime> utcNow;
        private readonly string directory;
        private readonly bool automaticSampling;
        private readonly RecordingStatus status;
        private StreamWriter writer;
        private FileStream file;
        private Timer timer;
        private DateTime started, lastAttempt;
        private Snapshot previous;
        private bool disposed;

        internal TelemetryRecorder(Func<Response> read, string directory, Func<DateTime> utcNow = null, bool automaticSampling = true)
        {
            this.read = read;
            this.directory = Path.GetFullPath(directory);
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.automaticSampling = automaticSampling;
            status = new RecordingStatus { Directory = this.directory, IntervalSeconds = IntervalSeconds };
        }

        internal RecordingStatus GetStatus()
        {
            lock (gate) return new RecordingStatus { Active = status.Active, Directory = status.Directory, FileName = status.FileName,
                StartedAtUtc = status.StartedAtUtc, StoppedAtUtc = status.StoppedAtUtc, LastSampleAtUtc = status.LastSampleAtUtc,
                Samples = status.Samples, Error = status.Error, IntervalSeconds = IntervalSeconds };
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(TelemetryRecorder));
                if (status.Active) return; // Repeated start is idempotent and never truncates a session.
                try
                {
                    var response = read();
                    Validate(response, utcNow()); // No output is created for an unsupported device or a stale response.
                    Directory.CreateDirectory(directory);
                    started = utcNow(); lastAttempt = started; previous = null;
                    string name = "LegionTweaks-" + started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".csv";
                    file = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(file, new UTF8Encoding(true)) { NewLine = "\r\n" };
                    status.FileName = name; status.StartedAtUtc = started.ToString("O"); status.StoppedAtUtc = null;
                    status.LastSampleAtUtc = null; status.Samples = 0; status.Error = null;
                    writer.WriteLine(TelemetryCsv.Header());
                    WriteSample(response, started, "start");
                    status.Active = true;
                    if (automaticSampling) timer = new Timer(s => Tick(), null, IntervalSeconds * 1000, IntervalSeconds * 1000);
                }
                catch (Exception ex) { Fail(ex); throw; }
            }
        }

        internal void Tick()
        {
            // Skip a busy callback instead of queuing a backlog during slow I/O or resume.
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                if (!status.Active || disposed) return;
                if ((utcNow() - started).TotalHours >= 8 || file.Length >= MaximumBytes)
                { StopCore("limit_reached"); return; }
                Response response = null;
                string error = null;
                try { response = read(); Validate(response, utcNow()); }
                catch (Exception ex) { error = ex.Message; }
                DateTime now = utcNow();
                if (error == null) WriteSample(response, now, null);
                else
                {
                    status.Error = error.Length > 512 ? error.Substring(0, 512) : error;
                    double gap = (now - lastAttempt).TotalSeconds;
                    WriteRow(TelemetryCsv.Row("error", now, started, gap, "read_error" + (gap > 6 ? ";gap" : ""), null, error));
                    lastAttempt = now;
                }
            }
            catch (Exception ex) { Fail(ex); }
            finally { Monitor.Exit(gate); }
        }

        private static void Validate(Response response, DateTime now)
        {
            if (response?.Success != true || response.State?.Supported != true)
                throw new InvalidOperationException(response?.Message ?? "Lectura no disponible.");
            DateTime timestamp;
            if (!DateTime.TryParse(response.State.CapturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp)
                || (now - timestamp.ToUniversalTime()).TotalSeconds > 6 || (timestamp.ToUniversalTime() - now).TotalSeconds > 2)
                throw new InvalidOperationException("Lectura vencida o fecha de captura inválida.");
        }

        private void WriteSample(Response response, DateTime now, string initialEvent)
        {
            var snapshot = response.State;
            var events = new List<string>();
            if (initialEvent != null) events.Add(initialEvent);
            double gap = (now - lastAttempt).TotalSeconds;
            if (gap > 6) events.Add("gap"); // Observed sampling gap; not proof that Windows suspended.
            if (gap < 0) events.Add("clock_changed");
            if (previous != null)
            {
                if (previous.OnAc != snapshot.OnAc) events.Add("ac_changed");
                foreach (string key in TelemetryCsv.SettingKeys)
                    if (TelemetryValue.Value(previous, key) != TelemetryValue.Value(snapshot, key)) events.Add(key + "_changed");
            }
            if ((snapshot.Values ?? new Reading[0]).Any(x => x.Error != null || !x.Value.HasValue)) events.Add("partial");
            WriteRow(TelemetryCsv.Row("sample", now, started, gap, string.Join(";", events), snapshot, null));
            status.Samples++; status.LastSampleAtUtc = snapshot.CapturedAtUtc;
            status.Error = null;
            lastAttempt = now; previous = snapshot;
        }

        private void WriteRow(string row)
        {
            writer.WriteLine(row);
            writer.Flush(); // Flush every row so a terminated helper retains already-written samples.
        }

        internal void Stop()
        {
            lock (gate)
            {
                try { StopCore("stop"); }
                catch (Exception ex) { Fail(ex); throw; }
            }
        }

        private void StopCore(string reason)
        {
            if (!status.Active) return;
            DateTime now = utcNow();
            WriteRow(TelemetryCsv.Row("end", now, started, 0, reason, null, null));
            status.Active = false; status.StoppedAtUtc = now.ToString("O");
            string closeError = Close();
            if (closeError != null) throw new IOException(closeError);
            if (reason == "limit_reached") status.Error = "Registro finalizado al alcanzar 8 horas o 64 MiB. Puedes iniciar otra sesión.";
        }

        private void Fail(Exception ex)
        {
            status.Active = false;
            status.StoppedAtUtc = utcNow().ToString("O");
            status.Error = ex.Message.Length > 512 ? ex.Message.Substring(0, 512) : ex.Message;
            Close();
        }

        private string Close()
        {
            timer?.Dispose(); timer = null;
            string error = null;
            try { writer?.Dispose(); } catch (Exception ex) { error = ex.Message; }
            try { file?.Dispose(); } catch (Exception ex) { error = error ?? ex.Message; }
            writer = null; file = null;
            return error;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                try { StopCore("helper_exit"); } catch (Exception ex) { Fail(ex); }
                disposed = true;
                Close();
            }
        }
    }
}
