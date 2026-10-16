// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace LegionTweaks
{
    internal sealed class ControllerService : IControllerService, IPlayerOneControllerService
    {
        private readonly object gate = new object();
        private readonly object commands = new object();
        private readonly HidHideLease lease;
        private ControllerLayout layout = new ControllerLayout();
        private ControllerStatus status = new ControllerStatus { SupportsPlayerOnePreparation = true, Message = "Mando físico. Aplica un perfil para activar la salida virtual." };
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
            lock (gate) if (status.PreparingPlayerOne) throw new InvalidOperationException("Termina o cancela la preparación del jugador 1 antes de aplicar otro perfil.");
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
                {
                    if (status.PreparingPlayerOne) throw new InvalidOperationException("Termina o cancela la preparación del jugador 1 antes de aplicar otro perfil.");
                    if (status.Active && worker?.IsAlive == true) { layout = next.Copy(); generation++; return; }
                }
                Stop(); Preflight(next); EnsureWatchdog();
                stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                // A completion source remains safe if the caller times out before the worker.
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, Message = "Conectando mando virtual…" }; }
                worker = new Thread(() => Run(ready, false)) { IsBackground = true, Name = "LegionTweaks.Controller" };
                worker.Start();
                if (!ready.Task.Wait(15000)) { stop.Set(); throw new TimeoutException("La conexión del mando tardó demasiado. Pulsa Devolver mando físico."); }
                var result = GetStatus();
                if (!result.Active) { Stop(); throw new InvalidOperationException(result.Error ?? "No se pudo activar el mando virtual."); }
            }
        }
        public void PreparePlayerOne(ControllerLayout next)
        {
            next.Validate();
            if (!next.Enabled) throw new ArgumentException("Selecciona un perfil guardado con mando virtual.");
            lock (commands)
            {
                PlayerOnePort.EnsureApplicationsClosed(); // Reject before disconnecting an active session.
                lock (gate) if (status.PreparingPlayerOne) throw new InvalidOperationException("La preparación ya está en curso.");
                Stop(); Preflight(next); EnsureWatchdog();
                stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, PreparingPlayerOne = true,
                    Message = "Preparando jugador 1. Mantén los juegos y Steam cerrados." }; }
                worker = new Thread(() => Run(null, true)) { IsBackground = true, Name = "LegionTweaks.PlayerOne" };
                worker.Start(); // Human FPS steps run asynchronously; IPC remains available to cancel.
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
            bool preparing;
            lock (gate) preparing = status.PreparingPlayerOne;
            stop.Set();
            if (worker != null && worker.IsAlive && !worker.Join(5000)) throw new InvalidOperationException("El mando está tardando en detenerse; el respaldo de recuperación sigue disponible.");
            worker = null;
            Interlocked.Exchange(ref feedback, 0);
            lease.Restore();
            lock (gate) { layout = new ControllerLayout(); status.Active = false; status.Connected = false; status.Hidden = false; status.PreparingPlayerOne = false;
                status.Input = new GamepadFrame(); status.Output = new GamepadFrame(); status.VirtualSlot = null; status.SlotCheckedAtUtc = status.SlotError = null; status.Error = null;
                status.Message = preparing ? "Preparación cancelada. Devuelve FPS a OFF para usar el mando físico." : "Mando físico disponible."; }
        }
        private void Run(TaskCompletionSource<bool> ready, bool preparePlayerOne)
        {
            bool owns = false;
            using (var owner = new Mutex(false, @"Global\LegionTweaks.ControllerOwner"))
            {
                var source = new SdlController();
                ViGEmClient client = null; IXbox360Controller target = null;
                try
                {
                    PlayerOnePort preparationPort = null;
                    try { owns = owner.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new InvalidOperationException("Otra sesión de LegionTweaks está usando el mando.");
                    client = new ViGEmClient(); target = client.CreateXbox360Controller(); target.AutoSubmitReport = false;
                    target.FeedbackReceived += (sender, args) => Interlocked.Exchange(ref feedback, args.LargeMotor | (args.SmallMotor << 8));
                    string device;
                    GamepadFrame first;
                    if (preparePlayerOne)
                    {
                        preparationPort = new PlayerOnePort(stop, () => { target.Connect(); Submit(target, new GamepadFrame()); },
                            () => { RefreshSlot(target); lock (gate) return status.VirtualSlot; },
                            () => { if (watchdog == null || watchdog.HasExited) throw new InvalidOperationException("El supervisor no está disponible. Devuelve FPS a OFF."); });
                        PlayerOnePreparation.Run(preparationPort, PreparationProgress);
                        PlayerOnePort.EnsureApplicationsClosed();
                        device = ControllerDevices.FindPhysicalInstance();
                        source.Open(); // Enumerate from scratch after FPS OFF, while the final target holds P1.
                        if (!source.Read(out first)) throw new InvalidOperationException("El mando físico no responde después de volver a FPS OFF.");
                    }
                    else
                    {
                        device = ControllerDevices.FindPhysicalInstance();
                        source.Open();
                        if (!source.Read(out first)) throw new InvalidOperationException("El mando físico no responde.");
                        target.Connect(); Submit(target, new GamepadFrame());
                    }
                    if (stop.WaitOne(0)) return;
                    if (preparePlayerOne) VerifyPrepared(preparationPort);
                    lease.Activate(device);
                    if (!source.Read(out first)) throw new InvalidOperationException("Se perdió el acceso al mando físico al activar HidHide.");
                    if (preparePlayerOne) VerifyPrepared(preparationPort);
                    RefreshSlot(target);
                    if (preparePlayerOne) preparationPort.CheckBudget();
                    lock (gate) { if (preparePlayerOne && status.VirtualSlot != 0) throw new InvalidOperationException("No se confirmó el virtual como jugador 1. Se devolverá el mando físico.");
                        status.Active = true; status.Hidden = true; status.Connected = true; status.PreparingPlayerOne = false;
                        status.Message = "Xbox 360 virtual activo. Abre el juego después de activar el mando."; }
                    ready?.TrySetResult(true);
                    var clock = Stopwatch.StartNew(); long chordSince = -1, lastRumble = -1000, lastSlot = 0; int revision = -1;
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
                        if (clock.ElapsedMilliseconds - lastSlot >= 1000)
                        {
                            RefreshSlot(target); lastSlot = clock.ElapsedMilliseconds;
                            if (preparePlayerOne && (watchdog == null || watchdog.HasExited))
                                throw new InvalidOperationException("El supervisor dejó de estar disponible. Se devolverá el mando físico.");
                            lock (gate) if (preparePlayerOne && status.VirtualSlot != 0)
                                throw new InvalidOperationException("El virtual dejó de confirmar el jugador 1. Se devolverá el mando físico.");
                        }
                        lock (gate) { status.Input = input; status.Output = output; status.Frames++; }
                    }
                }
                catch (Exception ex) { lock (gate) { status.Error = ex.Message;
                    status.Message = preparePlayerOne ? "Preparación o sesión del jugador 1 detenida. Devuelve FPS a OFF si sigue activado." : "No se pudo mantener la salida virtual."; } }
                finally
                {
                    if (target != null) { try { Submit(target, new GamepadFrame()); } catch { } try { target.Disconnect(); } catch { } }
                    try { client?.Dispose(); } catch { }
                    try { source.Dispose(); } catch { }
                    // Restore physical visibility even on input/output failure; never silently discard the lease.
                    bool recovered = false;
                    try { if (owns) lease.Restore(); recovered = true; }
                    catch (Exception ex) { lock (gate) status.Error = (status.Error ?? "") + " Recuperación pendiente: " + ex.Message; }
                    lock (gate) { status.Active = false; status.Connected = false; status.Hidden = !recovered; status.PreparingPlayerOne = false; layout = new ControllerLayout();
                        status.Input = new GamepadFrame(); status.Output = new GamepadFrame(); status.VirtualSlot = null; status.SlotCheckedAtUtc = status.SlotError = null; }
                    if (owns) owner.ReleaseMutex();
                    ready?.TrySetResult(false);
                }
            }
        }
        private static void VerifyPrepared(PlayerOnePort port)
        {
            var state = port.Inspect();
            int connected = 0;
            foreach (bool value in state.ConnectedSlots) if (value) connected++;
            if (port.ReadVirtualSlot() != 0 || !state.ConnectedSlots[0] || connected != 2
                || state.PhysicalDevices != 1 || state.OtherDevices != 1)
                throw new InvalidOperationException("Cambió la conexión antes de activar el jugador 1. Devuelve FPS a OFF y repite con el juego cerrado.");
            port.CheckBudget();
        }
        private void PreparationProgress(PlayerOneStage stage)
        {
            string message = stage == PlayerOneStage.WaitingForFreeSlot ? "Paso 1: activa FPS con el interruptor del mando derecho, sin desacoplarlo. Mantén el juego cerrado."
                : stage == PlayerOneStage.WaitingForPhysical ? "Jugador 1 reservado. Paso 2: devuelve el interruptor FPS a OFF."
                : stage == PlayerOneStage.Connecting ? "Conectando el virtual en jugador 1…"
                : "Comprobando el mando físico y su salida en jugador 1…";
            lock (gate) status.Message = message;
        }
        private void RefreshSlot(IXbox360Controller target)
        {
            int? slot = null; string error = null;
            try { int value = target.UserIndex; if (value >= 0 && value < 4) slot = value; else error = "El driver devolvió un puesto XInput fuera de rango."; }
            catch { error = "El driver todavía no confirmó el puesto XInput."; }
            lock (gate) { status.VirtualSlot = slot; status.SlotError = error; status.SlotCheckedAtUtc = DateTime.UtcNow.ToString("O"); }
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
