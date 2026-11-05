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
        void Begin(string devicePath);
        void Verify();
        void Restore();
    }
    internal interface IFpsY3Trial : IDisposable
    {
        FpsY3TrialStatus GetStatus();
        void Start(RemapProfile saved);
        void Stop();
    }

    // Only explicit Start owns a native lease. Catalog reads/saves never call it.
    internal sealed class FpsY3TrialService : IFpsY3Trial
    {
        private readonly object sync = new object();
        private readonly IFpsY3Source source;
        private readonly IFpsY3Lease lease;
        private readonly IFpsKeyOutput output;
        private readonly bool supported;
        private readonly ControllerRemapEngine engine = new ControllerRemapEngine();
        private readonly ManualResetEvent quit = new ManualResetEvent(false);
        private readonly Thread worker;
        private readonly FpsY3TrialStatus status = new FpsY3TrialStatus();
        private long expires;
        private bool reading, wanted, disposed, keyReleasePending;

        internal FpsY3TrialService(bool supported, IFpsY3Source source, IFpsY3Lease lease,
            IFpsKeyOutput output, bool automatic = true)
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
                if (status.Active || lease.Pending || keyReleasePending) throw new InvalidOperationException("Detén o recupera la prueba anterior antes de iniciar otra.");
                if (!source.CanBeginY3) throw new InvalidOperationException("Devuelve el físico y termina captura, preparación o botones FPS fijos antes de probar Y3.");
                status.Error = null; status.PulsesSent = 0;
                try
                {
                    source.BeginY3Read(); reading = true;
                    var wait = Stopwatch.StartNew(); FpsY3Sample sample = null;
                    while (wait.ElapsedMilliseconds < 3000)
                    {
                        try { sample = Fresh(); if ((sample.Report[20] & 0x24) == 0) break; sample = null; }
                        catch (InvalidOperationException) { }
                        Thread.Sleep(20);
                    }
                    if (sample == null) throw new InvalidOperationException("Falta lectura FPS reciente con Y3/M3 sueltos. No se activó el remapeo.");
                    if (!output.Inspect().Available) throw new InvalidOperationException("Suelta modificadores y usa el escritorio normal antes de iniciar la prueba.");
                    lease.Begin(sample.Path); Fresh(lease.DevicePath);
                    if (!lease.Muted) throw new InvalidOperationException("No se confirmó la neutralización de Y3.");
                    engine.Activate(copy, FpsY3TrialRules.Capabilities()); wanted = false;
                    status.ProfileId = copy.Id; status.Key = key; status.Active = true;
                    expires = source.NowMilliseconds + FpsY3TrialRules.DurationMilliseconds;
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
                if (!status.Active) {
                    try {
                        if (keyReleasePending) keyReleasePending = !output.ReleaseOwned();
                        if (reading && !lease.Pending && !keyReleasePending) { source.EndY3Read(); reading = false; status.RecoveryPending = false; status.Message = "Original de Y3 restaurado por lectura."; }
                    }
                    catch (Exception ex) { status.Error = ex.Message; status.RecoveryPending = true; }
                    return;
                }
                try
                {
                    if (source.NowMilliseconds >= expires || !lease.Muted) { StopLocked(); return; }
                    var sample = Fresh(lease.DevicePath); bool down; FpsY3TrialRules.Decode(sample.Product, sample.Report, out down);
                    var desktop = output.Inspect(); long now = source.NowMilliseconds;
                    var frame = new RemapInputFrame { Mode = RemapMode.Fps, Sequence = sample.Sequence,
                        CapturedAtMilliseconds = sample.CapturedAt, Values = new Dictionary<RemapInput, double> { { RemapInput.Y3, down ? 1 : 0 } } };
                    var result = engine.Observe(frame, now, desktop.Available, desktop.Foreground);
                    bool on = result.Keys.Length != 0;
                    if (on && !wanted)
                    {
                        // Recheck native substitution immediately before adding output.
                        lease.Verify(); Fresh(lease.DevicePath);
                        if (source.NowMilliseconds - sample.CapturedAt > ControllerRemapEngine.FreshMilliseconds) {
                            status.Error = "La comprobación nativa tardó demasiado; no se envió ese pulso. Suelta Y3 antes de repetir.";
                            engine.Resume(); on = false;
                        }
                        else if (!output.Pulse(status.Key, desktop)) { engine.Resume(); on = false; }
                        else status.PulsesSent++;
                    }
                    wanted = on;
                    status.Message = engine.WaitingForNeutral ? "Suelta Y3 y modificadores para rearmar la prueba."
                        : "Prueba FPS Y3 activa. Pulsos enviados: " + status.PulsesSent + ". Comprueba la acción en la aplicación de destino.";
                }
                catch (Exception ex)
                {
                    status.Error = ex.Message;
                    try { StopLocked(); } catch (Exception recovery) { status.Error += " Retorno pendiente: " + recovery.Message; }
                }
            }
        }
        public void Stop() { lock (sync) StopLocked(); }
        private void StopLocked()
        {
            status.Active = false; wanted = false; engine.Stop();
            bool released = output.ReleaseOwned(); keyReleasePending = !released;
            try { if (lease.Pending) lease.Restore(); }
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
            lock (sync) { if (disposed) return; try { StopLocked(); } finally { disposed = true; quit.Set(); } }
            if (worker == null || worker.Join(2000)) quit.Dispose();
        }
    }
}
