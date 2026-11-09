// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class FpsY3Sample
    {
        internal ushort Product;
        internal string Path;
        internal byte[] Report;
        internal long Sequence, CapturedAt;
    }
    internal interface IFpsY3Source
    {
        long NowMilliseconds { get; }
        bool CanBeginY3 { get; }
        void BeginY3Read();
        FpsY3Sample ReadY3();
        void EndY3Read();
    }
    internal interface IFpsY3Lease
    {
        bool Pending { get; }
        bool Muted { get; }
        string DevicePath { get; }
        long VerifierWarmupMs { get; }
        long LastNativeVerifyMs { get; }
        void Begin(string devicePath);
        void Verify();
        void Restore();
    }
    internal interface IFpsY3Trial : IDisposable
    {
        FpsY3TrialStatus GetStatus();
        FpsY3Diagnostics GetDiagnostics();
        void Start(RemapProfile saved);
        void Stop();
    }

    // Only explicit Start owns a native lease. Catalog reads/saves never call it.
    internal sealed class FpsY3TrialService : IFpsY3Trial
    {
        private readonly object sync = new object();
        private readonly IFpsY3Source source;
        private readonly IFpsY3Lease lease;
        private readonly IFpsGuardedKeyOutput output;
        private readonly bool supported;
        private readonly ControllerRemapEngine engine = new ControllerRemapEngine();
        private readonly ManualResetEvent quit = new ManualResetEvent(false);
        private readonly Thread worker;
        private readonly FpsY3TrialStatus status = new FpsY3TrialStatus();
        private long expires;
        private bool reading, wanted, disposed, keyReleasePending;
        private const int DiagnosticCapacity = 32;
        private readonly Queue<FpsY3AttemptDiagnostic> diagnostics = new Queue<FpsY3AttemptDiagnostic>();
        private long diagnosticStart, diagnosticNumber, diagnosticDropped, observedSequence = -1;
        private long sessionOrdinal;
        private long diagnosticSetupMs = -1, diagnosticWarmupMs = -1;
        private bool observedDown;
        private int stopWaiters;
        internal bool StopRequested => Volatile.Read(ref stopWaiters) != 0;

        internal FpsY3TrialService(bool supported, IFpsY3Source source, IFpsY3Lease lease,
            IFpsGuardedKeyOutput output, bool automatic = true)
        {
            this.supported = supported; this.source = source; this.lease = lease; this.output = output;
            status.Message = "Prueba disponible: FPS, solo Y3 → una tecla en Pulso, durante dos minutos. Guardar no activa el mando.";
            if (automatic) { worker = new Thread(Run) { IsBackground = true, Name = "LegionTweaks.Y3Trial" }; worker.Start(); }
        }
        public FpsY3TrialStatus GetStatus()
        {
            lock (sync)
            {
                status.Available = supported && !disposed && (status.Active || source.CanBeginY3) && !Pending();
                status.RecoveryPending = Pending();
                status.RemainingMilliseconds = status.Active ? (int)Math.Max(0, Math.Min(FpsY3TrialRules.DurationMilliseconds, expires - source.NowMilliseconds)) : 0;
                return Protocol.Decode<FpsY3TrialStatus>(Protocol.Encode(status));
            }
        }
        private bool Pending() { try { return keyReleasePending || lease.Pending && !status.Active; } catch { return true; } }
        public FpsY3Diagnostics GetDiagnostics()
        {
            lock (sync) return Protocol.Decode<FpsY3Diagnostics>(Protocol.Encode(new FpsY3Diagnostics {
                Capacity = DiagnosticCapacity, SessionOrdinal = status.SessionOrdinal, ActiveAtSnapshot = status.Active,
                RecoveryPendingAtSnapshot = Pending(), Dropped = diagnosticDropped, SetupMs = diagnosticSetupMs,
                VerifierWarmupMs = diagnosticWarmupMs, Attempts = diagnostics.ToArray() }));
        }
        private void Record(FpsY3AttemptDiagnostic item)
        {
            if (diagnostics.Count == DiagnosticCapacity) { diagnostics.Dequeue(); diagnosticDropped++; }
            diagnostics.Enqueue(item);
        }
        private static long Age(long now, long captured) => now < captured ? -1 : now - captured;
        private static string RejectionMessage(string reason)
        {
            if (reason == "edge_expired") return "La muestra de Y3 venció antes del envío; no se envió ese pulso. Suelta Y3 antes de repetir.";
            if (reason == "subsequent_source_expired") return "La última lectura FPS venció antes del envío; no se envió ese pulso. Suelta Y3 antes de repetir.";
            if (reason == "focus_changed") return "Cambió la ventana activa; no se envió ese pulso. Suelta Y3 antes de repetir.";
            if (reason == "key_already_down") return "La tecla elegida ya está pulsada; no se envió otro pulso. Suelta Y3 antes de repetir.";
            if (reason == "desktop_or_modifier") return "El escritorio o un modificador impidió el envío. Suelta Y3 antes de repetir.";
            return "No se confirmó el envío de ese pulso. Suelta Y3 antes de repetir.";
        }
        private FpsY3AttemptDiagnostic Attempt(FpsY3Sample sample, long initial, long engineAge, bool down, bool armed, MouseDesktop desktop)
        {
            return new FpsY3AttemptDiagnostic { Number = ++diagnosticNumber,
                SinceStartMs = Math.Max(0, source.NowMilliseconds - diagnosticStart), Sequence = sample.Sequence, SampleAtMs = sample.CapturedAt - diagnosticStart,
                InitialAgeMs = initial, EngineAgeMs = engineAge, Down = down, Armed = armed, DesktopAvailable = desktop.Available };
        }
        private FpsY3Sample Fresh(string expectedPath = null)
        {
            var sample = source.ReadY3(); long now = source.NowMilliseconds; bool down;
            if (sample == null || string.IsNullOrEmpty(sample.Path) || sample.Sequence < 0 || sample.CapturedAt < 0
                || sample.CapturedAt > now || now - sample.CapturedAt > ControllerRemapEngine.FreshMilliseconds
                || !FpsY3TrialRules.Decode(sample.Product, sample.Report, out down)
                || expectedPath != null && !string.Equals(expectedPath, sample.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Se perdió la lectura física FPS de Y3. Mantén FPS ON para devolver su mapa original.");
            return sample;
        }
        public void Start(RemapProfile saved)
        {
            var copy = saved?.Copy(); int key = FpsY3TrialRules.Require(copy);
            lock (sync)
            {
                if (!supported || disposed) throw new InvalidOperationException("Este equipo no admite la prueba de Y3.");
                if (StopRequested) throw new InvalidOperationException("La devolución de Y3 está en curso.");
                if (status.Active || lease.Pending || keyReleasePending) throw new InvalidOperationException("Detén o recupera la prueba anterior antes de iniciar otra.");
                if (!source.CanBeginY3) throw new InvalidOperationException("Devuelve el físico y termina captura, preparación o botones FPS fijos antes de probar Y3.");
                status.Error = null; status.PulsesSent = 0;
                try
                {
                    source.BeginY3Read(); reading = true;
                    var wait = Stopwatch.StartNew(); FpsY3Sample sample = null;
                    while (wait.ElapsedMilliseconds < 3000)
                    {
                        if (StopRequested) throw new InvalidOperationException("Se solicitó devolver Y3 durante el inicio.");
                        try { sample = Fresh(); if ((sample.Report[20] & 0x24) == 0) break; sample = null; }
                        catch (InvalidOperationException) { }
                        Thread.Sleep(20);
                    }
                    if (sample == null) throw new InvalidOperationException("Falta lectura FPS reciente con Y3/M3 sueltos. No se activó el remapeo.");
                    if (!output.Inspect().Available) throw new InvalidOperationException("Suelta modificadores y usa el escritorio normal antes de iniciar la prueba.");
                    long setupStart = source.NowMilliseconds;
                    lease.Begin(sample.Path); diagnosticSetupMs = Age(source.NowMilliseconds, setupStart);
                    diagnosticWarmupMs = lease.VerifierWarmupMs; Fresh(lease.DevicePath);
                    if (StopRequested) throw new InvalidOperationException("Se solicitó devolver Y3 durante el inicio.");
                    if (!lease.Muted) throw new InvalidOperationException("No se confirmó la neutralización de Y3.");
                    engine.Activate(copy, FpsY3TrialRules.Capabilities()); wanted = false;
                    status.ProfileId = copy.Id; status.Key = key; status.Active = true;
                    status.SessionOrdinal = ++sessionOrdinal;
                    expires = source.NowMilliseconds + FpsY3TrialRules.DurationMilliseconds;
                    diagnostics.Clear(); diagnosticStart = source.NowMilliseconds; diagnosticNumber = diagnosticDropped = 0;
                    observedSequence = -1; observedDown = false;
                    status.Message = "Prueba Y3 activa. Suelta Y3 para armar; cada presión enviará la tecla guardada. Se devolverá el original en dos minutos.";
                }
                catch
                {
                    try { StopLocked(); } catch (Exception ex) { status.Error = ex.Message; }
                    throw;
                }
            }
        }
        internal void Tick()
        {
            lock (sync)
            {
                if (disposed) return;
                if (StopRequested) return; // The waiting Stop owns recovery.
                if (!status.Active) {
                    try {
                        if (keyReleasePending) keyReleasePending = !output.ReleaseOwned();
                        if (reading && !lease.Pending && !keyReleasePending) { source.EndY3Read(); reading = false; status.RecoveryPending = false; status.Message = "Original de Y3 restaurado por lectura."; }
                    }
                    catch (Exception ex) { status.Error = ex.Message; status.RecoveryPending = true; }
                    return;
                }
                FpsY3AttemptDiagnostic attempt = null; string phase = "source";
                long verifyStart = -1, subsequentStart = -1, pulseStart = -1;
                try
                {
                    long tickStart = source.NowMilliseconds;
                    if (tickStart >= expires || !lease.Muted) {
                        attempt = new FpsY3AttemptDiagnostic { Number = ++diagnosticNumber, Sequence = -1,
                            SinceStartMs = Math.Max(0, tickStart - diagnosticStart), Decision = tickStart >= expires ? "lease_expired" : "lease_unavailable" };
                        StopLocked(); return;
                    }
                    long preflightEnd = source.NowMilliseconds;
                    var sample = Fresh(lease.DevicePath); bool down; FpsY3TrialRules.Decode(sample.Product, sample.Report, out down);
                    long readEnd = source.NowMilliseconds; long initialAge = Age(readEnd, sample.CapturedAt);
                    var desktop = output.Inspect(); long now = source.NowMilliseconds;
                    bool armed = !engine.WaitingForNeutral;
                    bool rising = sample.Sequence != observedSequence && down && !observedDown;
                    if (sample.Sequence != observedSequence) { observedSequence = sample.Sequence; observedDown = down; }
                    var frame = new RemapInputFrame { Mode = RemapMode.Fps, Sequence = sample.Sequence,
                        CapturedAtMilliseconds = sample.CapturedAt, Values = new Dictionary<RemapInput, double> { { RemapInput.Y3, down ? 1 : 0 } } };
                    var result = engine.Observe(frame, now, desktop.Available, desktop.Foreground);
                    long engineEnd = source.NowMilliseconds;
                    bool on = result.Keys.Length != 0;
                    if (on && !wanted)
                    {
                        attempt = Attempt(sample, initialAge, Age(now, sample.CapturedAt), down, armed, desktop);
                        attempt.PreflightMs = Age(preflightEnd, tickStart); attempt.InputReadMs = Age(readEnd, preflightEnd);
                        attempt.DesktopInspectMs = Age(now, readEnd); attempt.EngineMs = Age(engineEnd, now);
                        // Recheck native substitution immediately before adding output.
                        phase = "verify"; verifyStart = source.NowMilliseconds; attempt.VerifyStartMs = verifyStart - diagnosticStart;
                        lease.Verify(); attempt.VerifyMs = Age(source.NowMilliseconds, verifyStart);
                        attempt.VerifyEndMs = source.NowMilliseconds - diagnosticStart;
                        attempt.NativeVerifyMs = lease.LastNativeVerifyMs; attempt.VerifyResult = "confirmed";
                        if (StopRequested) { attempt.Decision = "stop_requested"; engine.Resume(); return; }
                        phase = "subsequent_source"; subsequentStart = source.NowMilliseconds;
                        var subsequent = Fresh(lease.DevicePath); bool subsequentDown;
                        FpsY3TrialRules.Decode(subsequent.Product, subsequent.Report, out subsequentDown);
                        attempt.SubsequentReadMs = Age(source.NowMilliseconds, subsequentStart);
                        attempt.SubsequentSequence = subsequent.Sequence; attempt.SubsequentAgeMs = Age(source.NowMilliseconds, subsequent.CapturedAt);
                        attempt.SubsequentAtMs = subsequent.CapturedAt - diagnosticStart;
                        attempt.SubsequentDown = subsequentDown;
                        if (subsequent.Sequence < sample.Sequence || subsequent.CapturedAt < sample.CapturedAt
                            || subsequent.Sequence == sample.Sequence && (subsequent.CapturedAt != sample.CapturedAt || subsequentDown != down))
                            throw new InvalidOperationException("La secuencia física FPS dejó de ser monotónica.");
                        attempt.OriginalAgeAfterVerifyMs = Age(source.NowMilliseconds, sample.CapturedAt);
                        if (source.NowMilliseconds >= expires) { attempt.Decision = "lease_expired"; StopLocked(); return; }
                        if (attempt.OriginalAgeAfterVerifyMs < 0 || attempt.OriginalAgeAfterVerifyMs > FpsY3TrialRules.MaxPulseAgeMilliseconds) {
                            attempt.Decision = "original_edge_expired";
                            status.Error = "La muestra de Y3 venció antes del envío; no se envió ese pulso. Suelta Y3 antes de repetir.";
                            engine.Resume(); on = false;
                        }
                        else {
                            phase = "output"; pulseStart = source.NowMilliseconds; attempt.PulseStartMs = pulseStart - diagnosticStart;
                            bool leaseExpired = false, stopRequested = false, sourceExpired = false;
                            bool sent = output.Pulse(status.Key, desktop, () => {
                                long current = source.NowMilliseconds;
                                attempt.OriginalAgeBeforeSendMs = Age(current, sample.CapturedAt);
                                // The enlarged Y3 pulse window must not enlarge the
                                // freshness of the most recent physical report.
                                long lastSampleAge = Age(current, subsequent.CapturedAt);
                                attempt.SubsequentAgeBeforeSendMs = lastSampleAge;
                                sourceExpired = lastSampleAge < 0 || lastSampleAge > ControllerRemapEngine.FreshMilliseconds;
                                leaseExpired = current >= expires;
                                stopRequested = StopRequested;
                                return attempt.OriginalAgeBeforeSendMs >= 0
                                    && attempt.OriginalAgeBeforeSendMs <= FpsY3TrialRules.MaxPulseAgeMilliseconds
                                    && !sourceExpired && !leaseExpired && !stopRequested;
                            });
                            attempt.PulseMs = Age(source.NowMilliseconds, pulseStart);
                            attempt.PulseEndMs = source.NowMilliseconds - diagnosticStart;
                            attempt.AcceptedEvents = output.LastAcceptedEvents; attempt.Decision = output.LastPulseDecision;
                            if (!sent) {
                                if (stopRequested) { attempt.Decision = "stop_requested"; engine.Resume(); return; }
                                if (leaseExpired) { attempt.Decision = "lease_expired"; StopLocked(); return; }
                                if (sourceExpired && attempt.OriginalAgeBeforeSendMs >= 0
                                    && attempt.OriginalAgeBeforeSendMs <= FpsY3TrialRules.MaxPulseAgeMilliseconds)
                                    attempt.Decision = "subsequent_source_expired";
                                status.Error = RejectionMessage(attempt.Decision);
                                engine.Resume(); on = false;
                            }
                            else { status.PulsesSent++; status.Error = null; }
                        }
                    }
                    else if (rising) {
                        attempt = Attempt(sample, initialAge, Age(now, sample.CapturedAt), down, armed, desktop);
                        attempt.PreflightMs = Age(preflightEnd, tickStart); attempt.InputReadMs = Age(readEnd, preflightEnd);
                        attempt.DesktopInspectMs = Age(now, readEnd); attempt.EngineMs = Age(engineEnd, now);
                        attempt.Decision = !desktop.Available || desktop.Foreground == 0 ? "desktop_or_modifier" : !armed ? "not_armed" : "no_candidate";
                    }
                    wanted = on;
                    status.Message = engine.WaitingForNeutral ? "Suelta Y3 y modificadores para rearmar la prueba."
                        : "Prueba FPS Y3 activa. Pulsos enviados: " + status.PulsesSent + ". Comprueba la acción en la aplicación de destino.";
                }
                catch (Exception ex)
                {
                    if (attempt == null && phase == "source")
                        attempt = new FpsY3AttemptDiagnostic { Number = ++diagnosticNumber, Sequence = -1,
                            SinceStartMs = Math.Max(0, source.NowMilliseconds - diagnosticStart) };
                    if (attempt != null) {
                        if (phase == "verify") { attempt.VerifyMs = verifyStart < 0 ? -1 : Age(source.NowMilliseconds, verifyStart); attempt.VerifyEndMs = source.NowMilliseconds - diagnosticStart; attempt.NativeVerifyMs = lease.LastNativeVerifyMs; attempt.VerifyResult = ex is TimeoutException ? "timeout" : "failed"; }
                        if (phase == "subsequent_source" && subsequentStart >= 0) attempt.SubsequentReadMs = Age(source.NowMilliseconds, subsequentStart);
                        if (phase == "output") { attempt.PulseMs = pulseStart < 0 ? -1 : Age(source.NowMilliseconds, pulseStart); attempt.PulseEndMs = source.NowMilliseconds - diagnosticStart; attempt.AcceptedEvents = output.LastAcceptedEvents; }
                        attempt.Decision = phase == "output" && output.LastPulseDecision != null ? output.LastPulseDecision : phase + "_error";
                    }
                    status.Error = ex.Message;
                    try { StopLocked(); } catch (Exception recovery) { status.Error += " Retorno pendiente: " + recovery.Message; }
                }
                finally { if (attempt != null) Record(attempt); }
            }
        }
        public void Stop()
        {
            Interlocked.Increment(ref stopWaiters);
            try { lock (sync) { if (status.Active || reading || Pending()) StopLocked(); } }
            finally { Interlocked.Decrement(ref stopWaiters); }
        }
        private void StopLocked()
        {
            status.Active = false; wanted = false; engine.Stop();
            bool released = output.ReleaseOwned(); keyReleasePending = !released;
            // Also closes an idle verifier if the independent watcher already
            // restored the native map before this worker observed the deadline.
            try { lease.Restore(); }
            finally
            {
                if (!lease.Pending && !keyReleasePending && reading) { source.EndY3Read(); reading = false; }
                status.RecoveryPending = lease.Pending || keyReleasePending;
                status.Message = lease.Pending ? "Prueba detenida; vuelve a FPS ON y pulsa Devolver Y3 para recuperar el original."
                    : "Prueba detenida. El mapa original de Y3 quedó conservado o restaurado por lectura.";
                if (keyReleasePending) status.Message += " Falta confirmar la liberación de una tecla propia; se volverá a intentar.";
            }
            if (!released) throw new InvalidOperationException("Queda una tecla propia pendiente de liberar.");
        }
        private void Run()
        { while (!quit.WaitOne(20)) Tick(); }
        public void Dispose()
        {
            Interlocked.Increment(ref stopWaiters);
            try {
                lock (sync) { if (disposed) return; try { StopLocked(); } finally { disposed = true; quit.Set(); } }
                if (worker == null || worker.Join(2000)) quit.Dispose();
            }
            finally { Interlocked.Decrement(ref stopWaiters); }
        }
    }
}
