// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    // Optional OSD publisher. The state/output lock never contains a sensor read:
    // a blocked WMI/ADL call cannot delay OFF, suspend or retiring our RTSS slot.
    internal sealed class RtssOverlayService : IRtssOverlay, IRtssAppearanceControl
    {
        internal const int PublishIntervalMilliseconds = 500;
        internal const int SampleIntervalMilliseconds = 2000;
        internal const int MaximumCleanupAttempts = 5;
        private readonly object gate = new object();
        private readonly Func<Response> read;
        private readonly IRtssOsdOutput output;
        private readonly Func<DateTime> utcNow;
        private readonly Func<long> milliseconds;
        private readonly bool supported, automaticSampling;
        private readonly TelemetryFreshness freshness;
        private readonly RtssOverlayStatus status;
        private Snapshot sample;
        private Timer timer;
        private bool disposed, outputDisposed, outputAttempted, readPending, hasReadAttempt, hasObservation, lastPublishWasCurrent;
        private int generation, cleanupAttempts;
        private long lastReadAttempt, lastObservation, sampleRevision;
        private string sampleError;
        private RtssAppearance appearance = new RtssAppearance();

        public void Configure(RtssAppearance value)
        {
            var copy = (value ?? throw new ArgumentNullException(nameof(value))).Copy();
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(RtssOverlayService));
                appearance = copy; // No activation, timer, fresh sample or RTSS write.
            }
        }

        internal RtssOverlayService(Func<Response> read, IRtssOsdOutput output, bool supported,
            bool automaticSampling = true, Func<DateTime> utcNow = null, Func<long> milliseconds = null)
        {
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.supported = supported;
            this.automaticSampling = automaticSampling;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (milliseconds != null) this.milliseconds = milliseconds;
            else
            {
                var clock = Stopwatch.StartNew();
                this.milliseconds = () => clock.ElapsedMilliseconds;
            }
            freshness = new TelemetryFreshness(this.utcNow, this.milliseconds);
            status = new RtssOverlayStatus { Supports = true, State = "off", Message = "Overlay RTSS desactivado." };
        }

        public RtssOverlayStatus GetStatus()
        {
            lock (gate)
            {
                bool expiredPublication = status.Enabled && status.Publishing && lastPublishWasCurrent
                    && (sample == null || !freshness.IsCurrent);
                return new RtssOverlayStatus { Supports = status.Supports, Enabled = status.Enabled,
                Publishing = status.Publishing && !expiredPublication, CleanupPending = status.CleanupPending,
                State = expiredPublication ? "stale" : status.State,
                Message = expiredPublication ? "La última publicación no contiene una lectura vigente; se actualizará el OSD." : status.Message,
                Error = expiredPublication ? "Telemetría no disponible o vencida." : status.Error,
                LastSampleAtUtc = status.LastSampleAtUtc, LastPublishedAtUtc = status.LastPublishedAtUtc,
                Updates = status.Updates, SupportsAppearance = true, Appearance = appearance.Copy() };
            }
        }

        public void SetEnabled(bool enabled)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(RtssOverlayService));
                if (enabled && !supported)
                    throw new InvalidOperationException("El overlay RTSS requiere una Legion Go 83E1 con Z1 Extreme compatible.");
                if (status.Enabled == enabled && (enabled || !status.CleanupPending)) return;
                unchecked { generation++; }
                InvalidateSample();
                status.Enabled = enabled;
                if (enabled)
                {
                    hasReadAttempt = hasObservation = false;
                    status.Error = null; status.State = "waiting";
                    status.Message = "Esperando telemetría y RTSS.";
                    EnsureTimer();
                }
                else StopCore();
            }
        }

        public void Suspend()
        {
            lock (gate)
            {
                if (disposed) return;
                unchecked { generation++; }
                status.Enabled = false;
                InvalidateSample();
                StopCore();
            }
        }

        public void Observe(Response response)
        {
            lock (gate)
            {
                // Controller/recording/OSD status responses have no snapshot and
                // are not telemetry failures. Only the dedicated read path treats
                // a null response as a failed sensor request.
                if (disposed || !status.Enabled || response?.State == null) return;
                Accept(response);
            }
        }

        private void Accept(Response response, bool allowInvalidate = true)
        {
            if (response?.Success != true || response.State?.Supported != true)
            {
                if (allowInvalidate) RejectSample();
                return;
            }
            DateTime captured;
            if (!TelemetryFreshness.TryGetCapture(response.State.CapturedAtUtc, utcNow(), out captured))
            {
                if (allowInvalidate) RejectSample();
                return;
            }
            if (!freshness.TryAccept(response.State.CapturedAtUtc, freshness.Generation, out captured)) return;
            sample = Copy(response.State);
            status.LastSampleAtUtc = sample.CapturedAtUtc;
            hasObservation = true; lastObservation = milliseconds();
            unchecked { sampleRevision++; }
            sampleError = null;
        }

        private void RejectSample()
        {
            // Keep the receipt history, so errors or repeated stale responses
            // cannot renew a sample's monotonic lifetime after a clock rollback.
            sample = null; sampleError = "Telemetría no disponible.";
            unchecked { sampleRevision++; }
        }

        private static Snapshot Copy(Snapshot source)
        {
            Reading[] readings = source.Values == null ? null : new Reading[source.Values.Length];
            if (readings != null)
                for (int i = 0; i < readings.Length; i++)
                {
                    var value = source.Values[i];
                    if (value != null) readings[i] = new Reading { Key = value.Key, Value = value.Value,
                        Error = value.Error, Unit = value.Unit, Source = value.Source, SensorId = value.SensorId,
                        RawValue = value.RawValue, SensorSupported = value.SensorSupported, Diagnostic = value.Diagnostic };
                }
            return new Snapshot { Supported = source.Supported, OnAc = source.OnAc,
                CapturedAtUtc = source.CapturedAtUtc, SavedOriginals = source.SavedOriginals, Values = readings };
        }

        private void InvalidateSample()
        {
            sample = null; sampleError = null; freshness.Invalidate();
            unchecked { sampleRevision++; }
        }

        // Exposed internally for deterministic tests. Timer callbacks skip a busy
        // output rather than accumulating work or starting overlapping reads.
        internal void Tick()
        {
            if (!Monitor.TryEnter(gate)) return;
            try
            {
                if (!status.Enabled || disposed)
                {
                    if (status.CleanupPending && cleanupAttempts > 0) ReleaseCore();
                    FinishIdle();
                    return;
                }
                long now = milliseconds();
                bool current = sample != null && freshness.IsCurrent;
                string text = current ? RtssOverlayText.Format(sample, appearance) : RtssOverlayText.Unavailable;
                outputAttempted = true;
                RtssPublishResult result;
                try { result = output.Publish(text); }
                catch { result = Failure("error", true); }
                ApplyResult(result, current);
                // Do not poll hardware for an unavailable/busy/full RTSS target.
                // An initial N/D publication establishes that our OSD can be
                // updated before starting the first optional sensor request.
                if (result?.Published == true && !readPending
                    && (!hasReadAttempt || Elapsed(now, lastReadAttempt) >= SampleIntervalMilliseconds)
                    && (!hasObservation || Elapsed(now, lastObservation) >= SampleIntervalMilliseconds))
                    BeginRead(now);
            }
            catch
            {
                // A presentation/timer failure must not terminate the helper.
                status.Publishing = false; status.State = "error";
                status.Error = "No se pudo actualizar el overlay RTSS.";
                status.Message = status.Error;
            }
            finally { Monitor.Exit(gate); }
        }

        private static long Elapsed(long now, long previous)
        {
            // A monotonic clock reset makes the next request due; it cannot keep
            // old telemetry alive (TelemetryFreshness separately invalidates it).
            return now < previous ? long.MaxValue : now - previous;
        }

        private void BeginRead(long now)
        {
            readPending = true; hasReadAttempt = true; lastReadAttempt = now;
            int requestedGeneration = generation;
            long requestedRevision = sampleRevision;
            Task.Run(() =>
            {
                lock (gate)
                {
                    if (disposed || !status.Enabled || generation != requestedGeneration)
                    { readPending = false; return; }
                }
                Response response = null;
                try { response = read(); } catch { }
                lock (gate)
                {
                    readPending = false;
                    if (disposed || !status.Enabled || generation != requestedGeneration) return;
                    // A late error cannot erase a newer externally observed sample.
                    Accept(response, sampleRevision == requestedRevision);
                }
            });
        }

        private void ApplyResult(RtssPublishResult result, bool current)
        {
            result = result ?? Failure("error", true);
            status.State = result.Published && !current ? "stale" : KnownState(result.State);
            status.Publishing = result.Published;
            lastPublishWasCurrent = current;
            status.CleanupPending = result.CleanupPending;
            status.Error = result.Error != null ? "No se pudo actualizar el overlay RTSS."
                : current ? null : sampleError ?? "Telemetría no disponible o vencida.";
            if (result.Published)
            {
                status.LastPublishedAtUtc = utcNow().ToString("O");
                if (status.Updates < long.MaxValue) status.Updates++;
                status.Message = current ? "OSD enviado a RTSS. La visualización depende del juego y su configuración."
                    : "OSD enviado a RTSS sin lecturas actuales.";
            }
            else status.Message = StateMessage(status.State);
        }

        private void StopCore()
        {
            status.Publishing = false;
            cleanupAttempts = MaximumCleanupAttempts;
            if (outputAttempted || status.CleanupPending) ReleaseCore();
            else { status.CleanupPending = false; status.State = "off"; status.Error = null; status.Message = "Overlay RTSS desactivado."; }
            FinishIdle();
            if (status.CleanupPending && cleanupAttempts > 0) EnsureTimer();
        }

        private void ReleaseCore()
        {
            cleanupAttempts--;
            RtssPublishResult result;
            try { result = output.Release() ?? Failure("error", true); }
            catch { result = Failure("error", true); }
            status.CleanupPending = result.CleanupPending;
            status.Publishing = false;
            status.State = status.CleanupPending ? KnownState(result.State) : "off";
            status.Error = status.CleanupPending ? "No se confirmó la retirada del OSD propio de RTSS." : null;
            status.Message = status.CleanupPending ? "Overlay apagado; retirada del OSD pendiente." : "Overlay RTSS desactivado.";
            if (!status.CleanupPending) outputAttempted = false;
        }

        private void EnsureTimer()
        {
            if (automaticSampling && timer == null)
                timer = new Timer(_ => Tick(), null, PublishIntervalMilliseconds, PublishIntervalMilliseconds);
        }

        private void FinishIdle()
        {
            if (status.Enabled || (status.CleanupPending && cleanupAttempts > 0)) return;
            timer?.Dispose(); timer = null;
            if (disposed && !outputDisposed)
            {
                outputDisposed = true;
                try { output.Dispose(); }
                catch { status.Error = "No se confirmó el cierre del transporte RTSS."; }
            }
        }

        private static RtssPublishResult Failure(string state, bool cleanupPending)
        { return new RtssPublishResult { State = state, CleanupPending = cleanupPending, Error = "RTSS" }; }

        private static string KnownState(string value)
        {
            switch (value)
            {
                case "published": case "missing": case "busy": case "full": case "incompatible": case "released": return value;
                default: return "error";
            }
        }

        private static string StateMessage(string value)
        {
            switch (value)
            {
                case "missing": return "RTSS no está disponible. Ábrelo por separado para publicar el OSD.";
                case "busy": return "RTSS está ocupado; se volverá a intentar.";
                case "full": return "RTSS no dispone de una entrada OSD libre.";
                case "incompatible": return "La memoria compartida de RTSS no es compatible.";
                default: return "No se pudo actualizar el overlay RTSS.";
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; status.Enabled = false;
                unchecked { generation++; }
                InvalidateSample();
                StopCore();
            }
        }
    }
}
