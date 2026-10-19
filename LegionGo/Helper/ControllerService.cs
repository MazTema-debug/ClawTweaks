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
    internal sealed class ControllerService : IControllerService, IPlayerOneControllerService, IAutomaticPlayerOneControllerService, IModePlayerOneControllerService, IBusyPlayerOneControllerService, IStickMouseControllerService
    {
        private readonly object gate = new object();
        private readonly object commands = new object();
        private readonly HidHideLease lease;
        private readonly PhysicalControllerRecovery deviceRecovery;
        private readonly string directory;
        private readonly StickMouseService mouse;
        private readonly ILegionControllerButtons legionButtons;
        private ControllerLayout layout = new ControllerLayout();
        private ControllerStatus status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true, SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, Message = "Mando físico. Aplica un perfil para activar la salida virtual." };
        private Thread worker;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private Process watchdog;
        private int generation;
        private int feedback; // low/high motors, exchanged atomically with the driver's callback
        private bool automaticPreparation;
        private IXbox360Controller connectedTarget;
        internal ControllerService(string directory, ILegionControllerButtons legionButtons = null)
        {
            this.directory = directory; this.legionButtons = legionButtons;
            lease = new HidHideLease(directory); deviceRecovery = new PhysicalControllerRecovery(directory);
            mouse = new StickMouseService(directory);
        }
        private static bool HasLegionMappings(ControllerLayout value)
        {
            if (value?.LegionMap == null) return false;
            foreach (int target in value.LegionMap) if (target != 0) return true;
            return false;
        }
        public ControllerLayout Layout { get { lock (gate) return layout.Copy(); } }
        public ControllerStatus GetStatus() { lock (gate) return Protocol.Decode<ControllerStatus>(Protocol.Encode(status)); }
        public StickMouseStatus GetMouseStatus() => mouse.GetStatus();
        public void SetMouse(bool enabled, int sensitivity, int deadzone)
        {
            lock (gate) if (enabled && (!status.Active || !status.Connected || status.PreparingPlayerOne || worker?.IsAlive != true))
                throw new InvalidOperationException("Activa primero el Xbox virtual y termina la preparación del jugador 1 para usar el mouse.");
            // Persistence must not delay Stop. The mouse service revalidates its
            // availability and generation after saving before it can turn ON.
            mouse.Configure(enabled, sensitivity, deadzone);
        }
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
                    if (status.Active && worker?.IsAlive == true)
                    {
                        layout = next.Copy(); generation++;
                        legionButtons?.SetControllerInputEnabled(HasLegionMappings(layout));
                        return;
                    }
                }
                Stop(); Preflight(next); EnsureWatchdog();
                stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                // A completion source remains safe if the caller times out before the worker.
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true, SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, Message = "Conectando mando virtual…" }; }
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
                mouse.Disable();
                lock (gate) if (status.PreparingPlayerOne) throw new InvalidOperationException("La preparación ya está en curso.");
                Stop();
                try
                {
                    legionButtons?.SetPreparationPaused(true);
                    Preflight(next); EnsureWatchdog();
                    stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                    lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true, SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, PreparingPlayerOne = true,
                        Message = "Preparando jugador 1. Mantén los juegos y Steam cerrados." }; }
                    worker = new Thread(() => Run(null, true)) { IsBackground = true, Name = "LegionTweaks.PlayerOne" };
                    worker.Start(); // Human FPS steps run asynchronously; IPC remains available to cancel.
                }
                catch { ResumeLegionInput(); throw; }
            }
        }
        public void PreparePlayerOneAutomatic(ControllerLayout next) => PreparePlayerOneAutomatic(next, false);
        public void PreparePlayerOneMode(ControllerLayout next) => PreparePlayerOneAutomatic(next, false, true);
        public void PreparePlayerOneWithBusyReaders(ControllerLayout next) => PreparePlayerOneAutomatic(next, true);
        private void PreparePlayerOneAutomatic(ControllerLayout next, bool allowBusyReaders, bool modeBased = false)
        {
            next.Validate();
            if (!next.Enabled) throw new ArgumentException("Selecciona un perfil guardado con mando virtual.");
            lock (commands)
            {
                PlayerOnePort.EnsureApplicationsClosed();
                mouse.Disable();
                IXbox360Controller currentTarget;
                lock (gate) currentTarget = status.Active && worker?.IsAlive == true ? connectedTarget : null;
                if (currentTarget != null) RefreshSlot(currentTarget);
                lock (gate)
                {
                    if (status.PreparingPlayerOne) throw new InvalidOperationException("La preparación ya está en curso.");
                    if (status.Active && worker?.IsAlive == true && currentTarget != null
                        && ReferenceEquals(currentTarget, connectedTarget) && status.VirtualSlot == 0 && status.SlotError == null)
                    {
                        layout = next.Copy(); generation++;
                        legionButtons?.SetControllerInputEnabled(HasLegionMappings(layout));
                        status.Message = "El virtual ya está en jugador 1. Distribución actualizada sin reconectar.";
                        return;
                    }
                }
                var trace = ControllerPreparationTrace.Start(directory);
                trace.Record(ControllerPreparationStage.CommandEntered);
                try
                {
                    trace.Record(ControllerPreparationStage.BeforeStop);
                    Stop(); trace.Record(ControllerPreparationStage.AfterStop);
                    legionButtons?.SetPreparationPaused(true);
                    trace.Record(ControllerPreparationStage.BeforePreflight);
                    Preflight(next);
                    trace.Record(ControllerPreparationStage.AfterPreflight);
                    EnsureWatchdog();
                    stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                    lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true,
                        SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, PreparingPlayerOne = true,
                        Message = modeBased ? "Preparando jugador 1 mediante cambio temporal de modo. Deja FPS en OFF."
                            : "Preparando jugador 1 con reconexión automática. Deja FPS en OFF." }; automaticPreparation = true; }
                    worker = new Thread(() => Run(null, true, true, allowBusyReaders, trace, modeBased)) { IsBackground = true, Name = "LegionTweaks.PlayerOneAutomatic" };
                    worker.Start();
                }
                catch
                {
                    trace.Record(ControllerPreparationStage.OperationFailed);
                    ResumeLegionInput();
                    trace.Record(ControllerPreparationStage.Finished);
                    trace.Dispose();
                    throw;
                }
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
            mouse.SetAvailable(false);
            DisableLegionInput();
            bool preparing, automatic;
            lock (gate) { preparing = status.PreparingPlayerOne; automatic = automaticPreparation; }
            stop.Set();
            if (worker != null && worker.IsAlive && !worker.Join(5000)) throw new InvalidOperationException("El mando está tardando en detenerse; el respaldo de recuperación sigue disponible.");
            worker = null;
            Interlocked.Exchange(ref feedback, 0);
            RecoverPhysical();
            ResumeLegionInput();
            lock (gate) { layout = new ControllerLayout(); status.Active = false; status.Connected = false; status.Hidden = false; status.PreparingPlayerOne = false;
                status.Input = new GamepadFrame(); status.Output = new GamepadFrame(); status.VirtualSlot = null; status.SlotCheckedAtUtc = status.SlotError = null; status.Error = null;
                automaticPreparation = false;
                status.Message = preparing ? automatic ? "Preparación cancelada. Mando físico recuperado."
                    : "Preparación cancelada. Devuelve FPS a OFF para usar el mando físico." : "Mando físico disponible."; }
        }
        private void Run(TaskCompletionSource<bool> ready, bool preparePlayerOne, bool automatic = false, bool allowBusyReaders = false, ControllerPreparationTrace trace = null, bool modeBased = false)
        {
            trace?.Record(ControllerPreparationStage.RunEntered);
            bool owns = false;
            using (var owner = new Mutex(false, @"Global\LegionTweaks.ControllerOwner"))
            {
                trace?.Record(ControllerPreparationStage.BeforeReaderCreation);
                var source = new SdlController();
                trace?.Record(ControllerPreparationStage.AfterReaderCreation);
                ViGEmClient client = null; IXbox360Controller target = null;
                try
                {
                    PlayerOnePort preparationPort = null;
                    AutomaticPlayerOnePort automaticPort = null;
                    ModePlayerOnePort modePort = null;
                    try { owns = owner.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new InvalidOperationException("Otra sesión de LegionTweaks está usando el mando.");
                    trace?.Record(ControllerPreparationStage.BeforeVigemClient);
                    client = new ViGEmClient();
                    trace?.Record(ControllerPreparationStage.AfterVigemClient);
                    trace?.Record(ControllerPreparationStage.BeforeTargetCreation);
                    target = client.CreateXbox360Controller(); target.AutoSubmitReport = false;
                    trace?.Record(ControllerPreparationStage.AfterTargetCreation);
                    target.FeedbackReceived += (sender, args) => Interlocked.Exchange(ref feedback, args.LargeMotor | (args.SmallMotor << 8));
                    string device;
                    GamepadFrame first;
                    if (preparePlayerOne)
                    {
                        preparationPort = new PlayerOnePort(stop, () => { target.Connect(); Submit(target, new GamepadFrame()); },
                            () => { RefreshSlot(target); lock (gate) return status.VirtualSlot; },
                            () => { if (watchdog == null || watchdog.HasExited) throw new InvalidOperationException("El supervisor del mando no está disponible."); }, automatic);
                        if (automatic)
                        {
                            device = ControllerDevices.FindPhysicalInstance();
                            if (modeBased)
                            {
                                modePort = new ModePlayerOnePort(preparationPort, new ControllerModeRecovery(directory),
                                    ControllerDevices.FindPhysicalInstance, NativeControllerModeDevice.RootForEndpoint);
                                AutomaticPlayerOnePreparation.Run(modePort, AutomaticPreparationProgress);
                            }
                            else
                            {
                                automaticPort = new AutomaticPlayerOnePort(preparationPort, deviceRecovery, device, allowBusyReaders, trace);
                                AutomaticPlayerOnePreparation.Run(automaticPort, AutomaticPreparationProgress);
                            }
                        }
                        else PlayerOnePreparation.Run(preparationPort, PreparationProgress);
                        PlayerOnePort.EnsureApplicationsClosed();
                        device = ControllerDevices.FindPhysicalInstance();
                        trace?.Record(ControllerPreparationStage.BeforePhysicalOpen, openRequested: true);
                        source.Open(); // Enumerate again after reconnection while the final target holds P1.
                        trace?.Record(ControllerPreparationStage.PhysicalOpened, opened: true);
                        if (!source.Read(out first)) throw new InvalidOperationException("El mando físico no responde después de reconectarlo.");
                    }
                    else
                    {
                        device = ControllerDevices.FindPhysicalInstance();
                        source.Open();
                        if (!source.Read(out first)) throw new InvalidOperationException("El mando físico no responde.");
                        target.Connect(); Submit(target, new GamepadFrame());
                    }
                    if (stop.WaitOne(0)) return;
                    automaticPort?.CheckBudget();
                    modePort?.CheckBudget();
                    if (preparePlayerOne) VerifyPrepared(preparationPort, automatic);
                    trace?.Record(ControllerPreparationStage.BeforeHidHide);
                    lease.Activate(device);
                    trace?.Record(ControllerPreparationStage.AfterHidHide);
                    if (!source.Read(out first)) throw new InvalidOperationException("Se perdió el acceso al mando físico al activar HidHide.");
                    if (preparePlayerOne) VerifyPrepared(preparationPort, automatic);
                    automaticPort?.CheckBudget();
                    modePort?.CheckBudget();
                    RefreshSlot(target);
                    if (preparePlayerOne) preparationPort.CheckBudget();
                    legionButtons?.SetPreparationPaused(false);
                    lock (gate) { if (preparePlayerOne && status.VirtualSlot != 0) throw new InvalidOperationException("No se confirmó el virtual como jugador 1. Se devolverá el mando físico.");
                        legionButtons?.SetControllerInputEnabled(HasLegionMappings(layout));
                        connectedTarget = target;
                        status.Active = true; status.Hidden = true; status.Connected = true; status.PreparingPlayerOne = false;
                        status.Message = "Xbox 360 virtual activo. Abre el juego después de activar el mando."; }
                    mouse.SetAvailable(true); // Availability never re-enables the explicitly disabled mode.
                    ready?.TrySetResult(true);
                    var clock = Stopwatch.StartNew(); long chordSince = -1, lastRumble = -1000, lastSlot = 0; int revision = -1;
                    while (!stop.WaitOne(4))
                    {
                        GamepadFrame input;
                        if (!source.Read(out input)) throw new InvalidOperationException("Mando físico desconectado. Reconéctalo y aplica de nuevo el perfil.");
                        input.LegionButtons = legionButtons?.GetControllerButtons() ?? 0;
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
                        var output = current.Transform(mouse.Process(input)); Submit(target, output);
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
                catch (Exception ex) { trace?.Record(ControllerPreparationStage.OperationFailed); lock (gate) { status.Error = ex.Message;
                    status.Message = automatic ? "Preparación automática detenida. Se recuperará el mando físico."
                        : preparePlayerOne ? "Preparación o sesión del jugador 1 detenida. Devuelve FPS a OFF si sigue activado." : "No se pudo mantener la salida virtual."; } }
                finally
                {
                    mouse.SetAvailable(false);
                    DisableLegionInput();
                    lock (gate) connectedTarget = null;
                    trace?.Record(ControllerPreparationStage.BeforeTargetCleanup);
                    if (target != null) { try { Submit(target, new GamepadFrame()); } catch { } try { target.Disconnect(); } catch { } }
                    trace?.Record(ControllerPreparationStage.AfterTargetCleanup);
                    trace?.Record(ControllerPreparationStage.BeforeVigemDispose);
                    try { client?.Dispose(); } catch { }
                    trace?.Record(ControllerPreparationStage.AfterVigemDispose);
                    trace?.Record(ControllerPreparationStage.BeforeReaderDispose);
                    try { source.Dispose(); } catch { }
                    trace?.Record(ControllerPreparationStage.AfterReaderDispose);
                    // Restore physical visibility even on input/output failure; never silently discard the lease.
                    bool recovered = false;
                    trace?.Record(ControllerPreparationStage.BeforeRecovery);
                    try { if (owns) RecoverPhysical(); recovered = true; trace?.Record(ControllerPreparationStage.AfterRecovery); }
                    catch (Exception ex) { lock (gate) status.Error = (status.Error ?? "") + " Recuperación pendiente: " + ex.Message; }
                    if (recovered) ResumeLegionInput();
                    lock (gate) { status.Active = false; status.Connected = false; status.Hidden = !recovered; status.PreparingPlayerOne = false; layout = new ControllerLayout();
                        if (automatic && owns && recovered && status.Error != null) status.Message = "Preparación automática detenida. Recuperación del mando físico completada.";
                        status.Input = new GamepadFrame(); status.Output = new GamepadFrame(); status.VirtualSlot = null; status.SlotCheckedAtUtc = status.SlotError = null; }
                    if (owns) owner.ReleaseMutex();
                    ready?.TrySetResult(false);
                    trace?.Record(ControllerPreparationStage.Finished);
                    trace?.Dispose();
                }
            }
        }
        private void RecoverPhysical()
        {
            Exception failure = null;
            try { ControllerModeRecovery.RecoverPending(directory); }
            catch (Exception ex) { failure = ex; }
            try { deviceRecovery.Restore(); deviceRecovery.CompleteRecovery(); }
            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            try { lease.Restore(); }
            catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
            if (failure != null) throw new InvalidOperationException("No se confirmó toda la recuperación del mando físico. Se conservan los registros pendientes.", failure);
        }
        private void DisableLegionInput()
        {
            // Vendor-reader cleanup must never skip target or physical recovery.
            try { legionButtons?.SetControllerInputEnabled(false); } catch { }
            try { (legionButtons as ILegionButtonCapture)?.CancelCapture(); } catch { }
        }
        private void ResumeLegionInput()
        {
            try { legionButtons?.SetPreparationPaused(false); } catch { }
        }
        private void AutomaticPreparationProgress(PlayerOneStage stage)
        {
            string message = stage == PlayerOneStage.WaitingForFreeSlot ? "Reconectando temporalmente el mando Lenovo para liberar jugador 1…"
                : stage == PlayerOneStage.WaitingForPhysical ? "Jugador 1 reservado. Recuperando la entrada del mando físico…"
                : stage == PlayerOneStage.Connecting ? "Conectando el virtual en jugador 1…"
                : "Comprobando la entrada física y la salida virtual en jugador 1…";
            lock (gate) status.Message = message;
        }
        private static void VerifyPrepared(PlayerOnePort port, bool automatic)
        {
            var state = port.Inspect();
            int connected = 0;
            foreach (bool value in state.ConnectedSlots) if (value) connected++;
            if (port.ReadVirtualSlot() != 0 || !state.ConnectedSlots[0] || connected != 2
                || state.PhysicalDevices != 1 || state.OtherDevices != 1)
                throw new InvalidOperationException("Cambió la conexión antes de activar el jugador 1. "
                    + (automatic ? "Se recuperará el mando físico." : "Devuelve FPS a OFF y repite con el juego cerrado."));
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
