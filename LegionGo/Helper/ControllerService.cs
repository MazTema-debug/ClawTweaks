// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace LegionTweaks
{
    internal sealed class ControllerService : IControllerService
    {
        private readonly object gate = new object();
        private readonly object commands = new object();
        private readonly HidHideLease lease;
        private ControllerLayout layout = new ControllerLayout();
        private ControllerStatus status = new ControllerStatus { Message = "Mando físico. Aplica un perfil para activar la salida virtual." };
        private Thread worker;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private Process watchdog;
        private int generation;
        private int feedback; // low/high motors, exchanged atomically with the driver's callback
        internal ControllerService(string directory) { lease = new HidHideLease(directory); }
        public ControllerLayout Layout { get { lock (gate) return layout.Copy(); } }
        public ControllerStatus GetStatus() { lock (gate) return Protocol.Decode<ControllerStatus>(Protocol.Encode(status)); }
        public void Preflight(ControllerLayout next)
        {
            next.Validate();
            if (!next.Enabled) return;
            lock (gate) if (status.Active) return;
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SDL2.dll"))) throw new InvalidOperationException("Falta SDL2.dll. Vuelve a instalar el paquete completo.");
            string device = ControllerDevices.FindPhysicalInstance();
            using (var config = new HidHideConfiguration()) HideLease.Check(config, device);
            try { using (var client = new ViGEmClient()) { } }
            catch (Exception ex) { throw new InvalidOperationException("ViGEmBus no está disponible. Instala su controlador oficial y reinicia Windows. " + ex.Message); }
        }
        public void Apply(ControllerLayout next)
        {
            next.Validate();
            lock (commands)
            {
                if (!next.Enabled) { Stop(); return; }
                lock (gate)
                    if (status.Active && worker?.IsAlive == true) { layout = next.Copy(); generation++; return; }
                Stop(); Preflight(next); EnsureWatchdog();
                stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                var ready = new ManualResetEvent(false);
                lock (gate) { layout = next.Copy(); status = new ControllerStatus { Message = "Conectando mando virtual…" }; }
                worker = new Thread(() => Run(ready)) { IsBackground = true, Name = "LegionTweaks.Controller" };
                worker.Start();
                if (!ready.WaitOne(15000)) { stop.Set(); throw new TimeoutException("La conexión del mando tardó demasiado. Pulsa Devolver mando físico."); }
                ready.Dispose();
                var result = GetStatus();
                if (!result.Active) { Stop(); throw new InvalidOperationException(result.Error ?? "No se pudo activar el mando virtual."); }
            }
        }
        private void EnsureWatchdog()
        {
            if (watchdog != null && !watchdog.HasExited) return;
            watchdog?.Dispose(); watchdog = null;
            var self = Process.GetCurrentProcess();
            string name = @"Local\LegionTweaks.ControllerWatchdog." + Guid.NewGuid().ToString("N");
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name))
            {
                var child = Process.Start(new ProcessStartInfo(self.MainModule.FileName,
                    "--controller-watchdog " + self.Id + " " + self.StartTime.ToUniversalTime().Ticks + " " + name)
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
                if (!ready.WaitOne(5000) || child.HasExited)
                { child.Dispose(); throw new InvalidOperationException("No se pudo iniciar el supervisor del mando. No se ocultó ningún dispositivo."); }
                watchdog = child;
            }
        }
        private void Stop()
        {
            stop.Set();
            if (worker != null && worker.IsAlive && !worker.Join(5000)) throw new InvalidOperationException("El mando está tardando en detenerse; el respaldo de recuperación sigue disponible.");
            worker = null;
            Interlocked.Exchange(ref feedback, 0);
            lease.Restore();
            lock (gate) { layout = new ControllerLayout(); status.Active = false; status.Connected = false; status.Hidden = false;
                status.Input = new GamepadFrame(); status.Output = new GamepadFrame(); status.VirtualSlot = null; status.Message = "Mando físico disponible."; }
        }
        private void Run(ManualResetEvent ready)
        {
            bool signalled = false, owns = false;
            using (var owner = new Mutex(false, @"Global\LegionTweaks.ControllerOwner"))
            {
                var source = new SdlController();
                ViGEmClient client = null; IXbox360Controller target = null;
                try
                {
                    try { owns = owner.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new InvalidOperationException("Otra sesión de LegionTweaks está usando el mando.");
                    string device = ControllerDevices.FindPhysicalInstance();
                    source.Open();
                    GamepadFrame first;
                    if (!source.Read(out first)) throw new InvalidOperationException("El mando físico no responde.");
                    client = new ViGEmClient(); target = client.CreateXbox360Controller(); target.AutoSubmitReport = false;
                    target.FeedbackReceived += (sender, args) => Interlocked.Exchange(ref feedback, args.LargeMotor | (args.SmallMotor << 8));
                    target.Connect(); Submit(target, new GamepadFrame());
                    if (stop.WaitOne(0)) return;
                    lease.Activate(device);
                    if (!source.Read(out first)) throw new InvalidOperationException("Se perdió el acceso al mando físico al activar HidHide.");
                    int? slot = null; try { slot = target.UserIndex; } catch { }
                    lock (gate) { status.Active = true; status.Hidden = true; status.Connected = true; status.VirtualSlot = slot;
                        status.Message = "Xbox 360 virtual activo. Abre el juego después de activar el mando."; }
                    signalled = true; ready.Set();
                    var clock = Stopwatch.StartNew(); long chordSince = -1, lastRumble = -1000; int revision = -1;
                    while (!stop.WaitOne(4))
                    {
                        GamepadFrame input;
                        if (!source.Read(out input)) throw new InvalidOperationException("Mando físico desconectado. Reconéctalo y aplica de nuevo el perfil.");
                        if (input.EmergencyChord)
                        {
                            if (chordSince < 0) chordSince = clock.ElapsedMilliseconds;
                            if (clock.ElapsedMilliseconds - chordSince >= 2000)
                            { lock (gate) status.Message = "Salida virtual detenida por la combinación de emergencia."; break; }
                        }
                        else chordSince = -1;
                        ControllerLayout current; int nextRevision;
                        lock (gate) { current = layout; nextRevision = generation; }
                        if (revision != nextRevision) { Submit(target, new GamepadFrame()); revision = nextRevision; }
                        var output = current.Transform(input); Submit(target, output);
                        if (clock.ElapsedMilliseconds - lastRumble >= 100)
                        { int motors = Volatile.Read(ref feedback); source.Rumble((byte)motors, (byte)(motors >> 8), current.Rumble); lastRumble = clock.ElapsedMilliseconds; }
                        lock (gate) { status.Input = input; status.Output = output; status.Frames++; }
                    }
                }
                catch (Exception ex) { lock (gate) { status.Error = ex.Message; status.Message = "No se pudo mantener la salida virtual."; } }
                finally
                {
                    if (target != null) { try { Submit(target, new GamepadFrame()); } catch { } try { target.Disconnect(); } catch { } }
                    try { client?.Dispose(); } catch { }
                    try { source.Dispose(); } catch { }
                    // Restore physical visibility even on input/output failure; never silently discard the lease.
                    bool recovered = false;
                    try { if (owns) lease.Restore(); recovered = true; }
                    catch (Exception ex) { lock (gate) status.Error = (status.Error ?? "") + " Recuperación pendiente: " + ex.Message; }
                    lock (gate) { status.Active = false; status.Connected = false; status.Hidden = !recovered; layout = new ControllerLayout();
                        status.Input = new GamepadFrame(); status.Output = new GamepadFrame(); status.VirtualSlot = null; }
                    if (owns) owner.ReleaseMutex();
                    if (!signalled) ready.Set();
                }
            }
        }
        private static void Submit(IXbox360Controller target, GamepadFrame frame)
        {
            target.SetButtonsFull(frame.Buttons);
            target.SetAxisValue(Xbox360Axis.LeftThumbX, frame.LX); target.SetAxisValue(Xbox360Axis.LeftThumbY, frame.LY);
            target.SetAxisValue(Xbox360Axis.RightThumbX, frame.RX); target.SetAxisValue(Xbox360Axis.RightThumbY, frame.RY);
            target.SetSliderValue(Xbox360Slider.LeftTrigger, frame.LT); target.SetSliderValue(Xbox360Slider.RightTrigger, frame.RT);
            target.SubmitReport();
        }
        public void Dispose() { lock (commands) { Stop(); watchdog?.Dispose(); watchdog = null; } }
    }
}
