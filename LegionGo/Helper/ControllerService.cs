// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace LegionTweaks
{
    internal sealed class ControllerService : IControllerService, IPlayerOneControllerService, IAutomaticPlayerOneControllerService, IModePlayerOneControllerService, IBusyPlayerOneControllerService, IStickMouseControllerService, ISplitControllerService, IGeneralSplitControllerService, IGameBarControllerInput, IPhysicalControllerReturn
    {
        private readonly object gate = new object();
        private readonly object commands = new object();
        private readonly HidHideLease lease;
        private readonly PhysicalControllerRecovery deviceRecovery;
        private readonly string directory;
        private readonly StickMouseService mouse;
        private readonly ILegionControllerButtons legionButtons;
        private readonly ILegionGamepadInput gamepadInput;
        private readonly GameBarInputLease gameBarInput = new GameBarInputLease();
        private readonly IGameBarForeground gameBarForeground;
        private ControllerLayout layout = new ControllerLayout();
        private ControllerStatus status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true, SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, SupportsSplitController = true, SupportsGeneralSplitController = true, Message = "Mando físico. Aplica un perfil para activar la salida virtual." };
        private Thread worker;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly ControllerPowerState power = new ControllerPowerState();
        private Process watchdog;
        private int generation;
        private int feedback; // low/high motors, exchanged atomically with the driver's callback
        private bool automaticPreparation, returningPhysical;
        private IXbox360Controller connectedTarget;
        internal event Action<ControllerStatus> StateChanged;
        private void PublishState() { try { StateChanged?.Invoke(GetStatus()); } catch { } }
        internal ControllerService(string directory, ILegionControllerButtons legionButtons = null, IGameBarForeground gameBarForeground = null)
        {
            this.directory = directory; this.legionButtons = legionButtons; gamepadInput = legionButtons as ILegionGamepadInput;
            this.gameBarForeground = gameBarForeground ?? new NativeGameBarForeground();
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
        public void ObserveGameBarInput(GameBarInputContext context)
        { lock (gate) gameBarInput.Observe(context, DateTime.UtcNow.Ticks); }
        public StickMouseStatus GetMouseStatus() => mouse.GetStatus();
        internal void SetPowerSuspended(bool value)
        {
            if (!power.SetSuspended(value)) return; // Ignore duplicate automatic/user resumes.
            lock (gate)
            {
                status.PowerEvent = value ? "suspend" : "resume";
                status.PowerEventAtUtc = DateTime.UtcNow.ToString("O");
                if (value) gameBarInput.Reset();
                if (value && !status.Active) { status.PhysicalSlot = null; status.SlotCheckedAtUtc = null; }
                // An unfinished preparation is canceled, never restarted on wake.
                if (value && status.PreparingPlayerOne) stop.Set();
            }
            if (value) mouse.SetAvailable(false);
        }
        public void SetMouse(bool enabled, int sensitivity, int deadzone)
        {
            lock (gate) if (enabled && status.TwoPlayers)
                throw new InvalidOperationException("El mouse no está disponible en dos jugadores: el stick derecho pertenece al jugador 2.");
            lock (gate) if (enabled && (!status.Active || !status.Connected || status.PreparingPlayerOne || worker?.IsAlive != true))
                throw new InvalidOperationException("Activa primero el Xbox virtual y termina la preparación del jugador 1 para usar el mouse.");
            // Persistence must not delay Stop. The mouse service revalidates its
            // availability and generation after saving before it can turn ON.
            mouse.Configure(enabled, sensitivity, deadzone);
        }
        public void Preflight(ControllerLayout next)
        {
            EnsureNoPhysicalReturn();
            next.Validate();
            if (power.Read().Suspended) throw new InvalidOperationException("Espera a que Windows termine de reanudar antes de activar el mando.");
            lock (gate) if (status.PreparingPlayerOne) throw new InvalidOperationException("Termina o cancela la preparación del jugador 1 antes de aplicar otro perfil.");
            if (!next.Enabled) return;
            lock (gate) if (status.Active && status.TwoPlayers)
                throw new InvalidOperationException("Devuelve el mando físico antes de aplicar un perfil editable durante dos jugadores.");
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
                EnsureNoPhysicalReturn();
                if (!next.Enabled) { Stop(); return; }
                lock (gate)
                {
                    if (status.PreparingPlayerOne) throw new InvalidOperationException("Termina o cancela la preparación del jugador 1 antes de aplicar otro perfil.");
                    if (status.Active && status.TwoPlayers) throw new InvalidOperationException("Devuelve el mando físico antes de aplicar un perfil editable durante dos jugadores.");
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
                lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true, SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, SupportsSplitController = true, SupportsGeneralSplitController = true, Message = "Conectando mando virtual…" }; }
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
                EnsureNoPhysicalReturn();
                PlayerOnePort.EnsureApplicationsClosed(); // Reject before disconnecting an active session.
                mouse.Disable();
                lock (gate) if (status.PreparingPlayerOne) throw new InvalidOperationException("La preparación ya está en curso.");
                Stop();
                try
                {
                    legionButtons?.SetPreparationPaused(true);
                    Preflight(next); EnsureWatchdog();
                    stop.Reset(); Interlocked.Exchange(ref feedback, 0);
                    lock (gate) { layout = next.Copy(); status = new ControllerStatus { SupportsPlayerOnePreparation = true, SupportsBusyReaderReconnection = true, SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, SupportsSplitController = true, SupportsGeneralSplitController = true, PreparingPlayerOne = true,
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
        public void PrepareSplitControllers() => PreparePlayerOneAutomatic(SplitControllerProfile.CreateLayout(), false, true, true);
        public void PrepareGeneralSplitControllers() => PreparePlayerOneAutomatic(SplitControllerProfile.CreateLayout(), false, true, true, SplitControllerPreset.General);
        private void PreparePlayerOneAutomatic(ControllerLayout next, bool allowBusyReaders, bool modeBased = false, bool twoPlayers = false, SplitControllerPreset splitPreset = SplitControllerPreset.Isaac)
        {
            next.Validate();
            if (!next.Enabled) throw new ArgumentException("Selecciona un perfil guardado con mando virtual.");
            lock (commands)
            {
                EnsureNoPhysicalReturn();
                if (modeBased) PlayerOneApplications.EnsureModeAllowed(); else PlayerOnePort.EnsureApplicationsClosed();
                mouse.Disable();
                IXbox360Controller currentTarget;
                lock (gate) currentTarget = status.Active && worker?.IsAlive == true ? connectedTarget : null;
                if (currentTarget != null) RefreshSlot(currentTarget);
                lock (gate)
                {
                    if (status.PreparingPlayerOne) throw new InvalidOperationException("La preparación ya está en curso.");
                    if (!twoPlayers && !status.TwoPlayers && status.Active && worker?.IsAlive == true && currentTarget != null
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
                        SupportsAutomaticPlayerOnePreparation = true, SupportsModePlayerOnePreparation = true, SupportsSplitController = true, SupportsGeneralSplitController = true, PreparingPlayerOne = true, TwoPlayers = twoPlayers, GeneralSplit = twoPlayers && splitPreset == SplitControllerPreset.General,
                        Message = twoPlayers ? "Reservando jugadores 1 y 2. Mantén los mandos acoplados y FPS en OFF."
                            : modeBased ? "Preparando jugador 1 mediante cambio temporal de modo. Deja FPS en OFF."
                            : "Preparando jugador 1 con reconexión automática. Deja FPS en OFF." }; automaticPreparation = true; }
                    worker = new Thread(() => Run(null, true, true, allowBusyReaders, trace, modeBased, twoPlayers, splitPreset)) { IsBackground = true, Name = "LegionTweaks.PlayerOneAutomatic" };
                    worker.Start();
                }
                catch (Exception error)
                {
                    trace.Record(ControllerPreparationStage.OperationFailed);
                    trace.RecordFailure(error);
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
        private void EnsureNoPhysicalReturn()
        {
            lock (gate) if (returningPhysical)
                throw new InvalidOperationException("Espera a que termine la devolución del mando físico.");
        }
        public void ReturnPhysical()
        {
            lock (commands) {
                lock (gate) if (returningPhysical) return;
                bool preparing;
                lock (gate) preparing = status.PreparingPlayerOne;
                Stop();
                // Canceling the human FPS assistant must retain its original
                // recovery instructions, never start an additional mode change.
                if (preparing) return;
                lock (gate) {
                    returningPhysical = true; status.RecoveringInput = true;
                    status.PhysicalSlot = null;
                    status.Message = "Comprobando el puesto del mando físico. Espera antes de volver a activarlo…";
                }
                worker = new Thread(RunPhysicalReturn) { IsBackground = true, Name = "LegionTweaks.PhysicalReturn" };
                try { worker.Start(); }
                catch { lock (gate) { returningPhysical = false; status.RecoveringInput = false; } throw; }
            }
        }
        private void RunPhysicalReturn()
        {
            try {
                // No active target or hide lease remains. The established mode
                // coordinator checks games, identity and its recovery supervisor.
                legionButtons?.SetPreparationPaused(true);
                int slot = ControllerModeRecovery.RestorePhysicalPlayerOne(directory);
                lock (gate) {
                    status.PhysicalSlot = slot; status.Error = null;
                    status.SlotCheckedAtUtc = DateTime.UtcNow.ToString("O");
                    status.Message = "Mando físico disponible.";
                }
            }
            catch (Exception error) {
                lock (gate) {
                    status.PhysicalSlot = null; status.Error = error.Message;
                    status.Message = File.Exists(Path.Combine(directory, ControllerModeRecovery.JournalFileName))
                        ? "Recuperación del modo físico pendiente. Se conserva su respaldo."
                        : "Mando físico devuelto; su puesto no quedó confirmado.";
                }
            }
            finally {
                ResumeLegionInput();
                lock (gate) { returningPhysical = false; status.RecoveringInput = false; }
                PublishState();
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
                status.Suspended = status.RecoveringInput = status.WaitingForNeutral = false;
                status.Input = new GamepadFrame(); status.Output = status.SecondOutput = new GamepadFrame(); status.VirtualSlot = status.SecondVirtualSlot = null; status.PhysicalSlot = null; status.SecondSlotError = null; status.TwoPlayers = status.GeneralSplit = false; status.SlotCheckedAtUtc = status.SlotError = null; status.Error = null;
                automaticPreparation = false;
                status.DirectGameBarInput = false;
                status.Message = preparing ? automatic ? "Preparación cancelada. Mando físico recuperado."
                    : "Preparación cancelada. Devuelve FPS a OFF para usar el mando físico." : "Mando físico disponible."; }
        }
        private void Run(TaskCompletionSource<bool> ready, bool preparePlayerOne, bool automatic = false, bool allowBusyReaders = false, ControllerPreparationTrace trace = null, bool modeBased = false, bool twoPlayers = false, SplitControllerPreset splitPreset = SplitControllerPreset.Isaac)
        {
            trace?.Record(ControllerPreparationStage.RunEntered);
            bool owns = false, runningSession = false;
            using (var owner = new Mutex(false, @"Global\LegionTweaks.ControllerOwner"))
            {
                trace?.Record(ControllerPreparationStage.BeforeReaderCreation);
                var source = new SdlController();
                trace?.Record(ControllerPreparationStage.AfterReaderCreation);
                ViGEmClient client = null; IXbox360Controller target = null, secondTarget = null;
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
                    if (twoPlayers) { secondTarget = client.CreateXbox360Controller(); secondTarget.AutoSubmitReport = false; }
                    trace?.Record(ControllerPreparationStage.AfterTargetCreation);
                    if (!twoPlayers) target.FeedbackReceived += (sender, args) => Interlocked.Exchange(ref feedback, args.LargeMotor | (args.SmallMotor << 8));
                    Action neutral = () => { Submit(target, new GamepadFrame()); if (secondTarget != null) Submit(secondTarget, new GamepadFrame()); };
                    Func<int?[]> slots = () => {
                        RefreshSlots(target, secondTarget);
                        lock (gate) return twoPlayers ? new[] { status.VirtualSlot, status.SecondVirtualSlot } : new[] { status.VirtualSlot };
                    };
                    string device, protectedDevice = null;
                    bool physicalProtected = false;
                    GamepadFrame first;
                    if (preparePlayerOne)
                    {
                        preparationPort = new PlayerOnePort(stop, () => {
                                target.Connect(); Submit(target, new GamepadFrame());
                                if (secondTarget != null) {
                                    preparationPort.CheckBudget(); modePort?.CheckBudget();
                                    if (watchdog == null || watchdog.HasExited) throw new InvalidOperationException("El supervisor no está disponible para conectar el segundo virtual.");
                                    secondTarget.Connect(); Submit(secondTarget, new GamepadFrame());
                                }
                            },
                            () => slots()[0],
                            () => { if (watchdog == null || watchdog.HasExited) throw new InvalidOperationException("El supervisor del mando no está disponible."); }, automatic, modeBased);
                        if (automatic)
                        {
                            device = ControllerDevices.FindPhysicalInstance();
                            if (modeBased)
                            {
                                string originalDevice = device;
                                modePort = new ModePlayerOnePort(preparationPort, new ControllerModeRecovery(directory),
                                    ControllerDevices.FindPhysicalInstance, NativeControllerModeDevice.RootForEndpoint, twoPlayers ? 2 : 1, slots,
                                    () => {
                                        PlayerOneApplications.EnsureModeAllowed();
                                        trace?.Record(ControllerPreparationStage.BeforeHidHide);
                                        lease.Activate(originalDevice);
                                        physicalProtected = true; protectedDevice = originalDevice;
                                        trace?.Record(ControllerPreparationStage.AfterHidHide);
                                    });
                                AutomaticPlayerOnePreparation.Run(modePort, AutomaticPreparationProgress, twoPlayers ? 2 : 1);
                            }
                            else
                            {
                                automaticPort = new AutomaticPlayerOnePort(preparationPort, deviceRecovery, device, allowBusyReaders, trace);
                                AutomaticPlayerOnePreparation.Run(automaticPort, AutomaticPreparationProgress);
                            }
                        }
                        else PlayerOnePreparation.Run(preparationPort, PreparationProgress);
                        if (modeBased) PlayerOneApplications.EnsureModeAllowed(); else PlayerOnePort.EnsureApplicationsClosed();
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
                    if (preparePlayerOne) VerifyPrepared(preparationPort, automatic, secondTarget);
                    if (physicalProtected) {
                        if (!string.Equals(device, protectedDevice, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Cambió la identidad del mando físico al volver del modo temporal.");
                        lease.VerifyActive(device);
                    }
                    else {
                        trace?.Record(ControllerPreparationStage.BeforeHidHide);
                        lease.Activate(device);
                        trace?.Record(ControllerPreparationStage.AfterHidHide);
                    }
                    if (!source.Read(out first)) throw new InvalidOperationException("Se perdió el acceso al mando físico al activar HidHide.");
                    if (preparePlayerOne) VerifyPrepared(preparationPort, automatic, secondTarget);
                    automaticPort?.CheckBudget();
                    modePort?.CheckBudget();
                    RefreshSlots(target, secondTarget);
                    if (preparePlayerOne) preparationPort.CheckBudget();
                    legionButtons?.SetPreparationPaused(false);
                    gamepadInput?.SetGamepadInputEnabled(true);
                    lock (gate) { if (preparePlayerOne && status.VirtualSlot != 0) throw new InvalidOperationException("No se confirmó el virtual como jugador 1. Se devolverá el mando físico.");
                        if (twoPlayers && status.SecondVirtualSlot != 1) throw new InvalidOperationException("No se confirmó el segundo virtual como jugador 2. Se devolverá el mando físico.");
                        legionButtons?.SetControllerInputEnabled(twoPlayers || HasLegionMappings(layout));
                        connectedTarget = target;
                        status.Active = true; status.Hidden = true; status.Connected = true; status.PreparingPlayerOne = false;
                        status.WaitingForNeutral = twoPlayers;
                        status.Message = twoPlayers ? "Dos Xbox virtuales activos: izquierda jugador 1, derecha jugador 2. Abre el juego después."
                            : "Xbox 360 virtual activo. Abre el juego después de activar el mando."; }
                    mouse.SetAvailable(!twoPlayers); // Availability never re-enables the explicitly disabled mode.
                    runningSession = true;
                    PublishState();
                    ready?.TrySetResult(true);
                    var clock = Stopwatch.StartNew(); long chordSince = -1, lastRumble = -1000, lastSlot = 0; int revision = -1;
                    var inputRoute = new ControllerInputRouteGate();
                    long missingDirectSince = -1;
                    int? retainedSlot, retainedSecondSlot;
                    lock (gate) { retainedSlot = status.VirtualSlot; retainedSecondSlot = status.SecondVirtualSlot; }
                    Func<bool> directEvidenceRequired = () => {
                        if (gamepadInput == null) return false;
                        bool native = gameBarForeground.IsForeground(), direct;
                        lock (gate) direct = gameBarInput.UsesDirectInput(DateTime.UtcNow.Ticks, native);
                        return direct || inputRoute.NeedsDirectNeutral(direct);
                    };
                    var readiness = new ControllerSourceReadiness(
                        () => {
                            source.Dispose(); source = new SdlController();
                            try { source.Open(); return true; }
                            catch (InvalidOperationException) { source.Dispose(); return false; }
                        },
                        () => { GamepadFrame fresh; return source.Read(out fresh); },
                        () => {
                            ResumeLegionInput();
                            lock (gate) {
                                legionButtons?.SetControllerInputEnabled(twoPlayers || HasLegionMappings(layout));
                                gamepadInput?.SetGamepadInputEnabled(true);
                            }
                        },
                        directEvidenceRequired,
                        () => { GamepadFrame fresh; return gamepadInput != null && gamepadInput.TryReadGamepad(out fresh); },
                        () => source.Dispose());
                    var resume = new ControllerResume(power, new ResumePort(
                        () => {
                            readiness.Reset();
                            missingDirectSince = -1;
                            mouse.SetAvailable(false); DisableLegionInput();
                            Interlocked.Exchange(ref feedback, 0);
                            neutral();
                            source.Dispose();
                            lock (gate) { status.Connected = false; status.DirectGameBarInput = false; status.Input = status.Output = status.SecondOutput = new GamepadFrame();
                                status.VirtualSlot = status.SecondVirtualSlot = null; status.SecondSlotError = null; status.SlotCheckedAtUtc = status.SlotError = null; }
                        },
                        () => {
                            if (stop.WaitOne(0)) return false;
                            if (watchdog == null || watchdog.HasExited) throw new InvalidOperationException("El supervisor del mando no está disponible durante la recuperación.");
                            var devices = ControllerDevices.XusbInstances().Where(ControllerDevices.IsLegionInstance).ToArray();
                            if (devices.Length == 0) return false;
                            if (devices.Length != 1 || !string.Equals(devices[0], device, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Cambió la identidad del mando físico durante la recuperación. Se devolverá el mando físico.");
                            // Reuse the target, lease and layout. Never cycle the receiver's
                            // mode or reconnect ViGEm while a game may still be open.
                            RefreshSlots(target, secondTarget);
                            lock (gate) if (!status.VirtualSlot.HasValue || (twoPlayers && !status.SecondVirtualSlot.HasValue)) return false;
                            lock (gate) if (status.VirtualSlot != retainedSlot || status.SecondVirtualSlot != retainedSecondSlot)
                                throw new InvalidOperationException("El virtual cambió de puesto durante la recuperación. Se devolverá el mando físico.");
                            return readiness.TryOpen();
                        },
                        (suspended, kind) => {
                            lock (gate) { status.Suspended = suspended; status.RecoveringInput = !suspended;
                                status.WaitingForNeutral = true;
                                status.Message = suspended ? "Virtual en reposo durante la suspensión. Se conserva su conexión."
                                    : kind == ControllerRecoveryKind.Reconnect ? "Esperando que regrese la entrada del mando. Se conserva el virtual hasta 20 segundos…"
                                    : "Recuperando la entrada física tras la pausa…"; }
                        },
                        kind => {
                            Interlocked.Exchange(ref feedback, 0);
                            lock (gate) { status.Connected = true; status.Suspended = status.RecoveringInput = false;
                                if (kind == ControllerRecoveryKind.Reconnect) status.ReconnectCount++; else status.ResumeCount++;
                                status.LastRecoveryKind = kind == ControllerRecoveryKind.Reconnect ? "reconnect"
                                    : kind == ControllerRecoveryKind.SamplingGap ? "sample_gap" : "resume";
                                status.Message = "Entrada recuperada. Suelta los botones y centra los sticks para continuar."; }
                            mouse.SetAvailable(!twoPlayers);
                        }), clock.ElapsedMilliseconds, () => stop.WaitOne(0), () => clock.ElapsedMilliseconds, twoPlayers);
                    while (!stop.WaitOne(4))
                    {
                        if (!resume.Step(clock.ElapsedMilliseconds))
                        { chordSince = -1; revision = -1; lastRumble = clock.ElapsedMilliseconds; stop.WaitOne(100); continue; }
                        GamepadFrame input;
                        if (!source.Read(out input))
                        {
                            if (resume.ReadWasInterrupted(clock.ElapsedMilliseconds)) continue;
                            resume.SourceDisconnected(clock.ElapsedMilliseconds);
                            continue;
                        }
                        if (resume.ReadWasInterrupted(clock.ElapsedMilliseconds)) continue;
                        bool direct, nativeHost = gameBarForeground.IsForeground();
                        lock (gate) {
                            long now = DateTime.UtcNow.Ticks;
                            direct = gamepadInput != null && gameBarInput.UsesDirectInput(now, nativeHost);
                            status.DirectGameBarInput = direct;
                        }
                        GamepadFrame directFrame = new GamepadFrame();
                        bool directAvailable = gamepadInput != null && gamepadInput.TryReadGamepad(out directFrame);
                        bool available = true;
                        if (direct || inputRoute.NeedsDirectNeutral(direct)) {
                            if (gamepadInput.GamepadInputPaused) missingDirectSince = -1;
                            else if (!directAvailable) {
                                if (missingDirectSince < 0) missingDirectSince = clock.ElapsedMilliseconds;
                                if (clock.ElapsedMilliseconds - missingDirectSince >= 3000) {
                                    // The HID collection can return later than SDL after
                                    // a reconnect. Reuse the bounded source-recovery path,
                                    // retaining neutral targets and the same identity.
                                    missingDirectSince = -1;
                                    resume.SourceDisconnected(clock.ElapsedMilliseconds);
                                    continue;
                                }
                            }
                            else missingDirectSince = -1;
                        }
                        else missingDirectSince = -1;
                        if (direct) { input = directFrame; available = directAvailable; }
                        if (available && (!(direct || inputRoute.NeedsDirectNeutral(direct)) || directAvailable))
                            resume.ObserveFreshInput(clock.ElapsedMilliseconds);
                        input.LegionButtons = legionButtons?.GetControllerButtons() ?? 0;
                        directFrame.LegionButtons = input.LegionButtons;
                        // Emergency release must also work while ordinary output
                        // is waiting for neutral after wake.
                        if (input.EmergencyChord)
                        {
                            if (chordSince < 0) chordSince = clock.ElapsedMilliseconds;
                            if (clock.ElapsedMilliseconds - chordSince >= 2000)
                            { lock (gate) status.Message = "Salida virtual detenida por la combinación de emergencia."; break; }
                        }
                        else chordSince = -1;
                        ControllerLayout current; int nextRevision;
                        lock (gate) { current = layout; nextRevision = generation; }
                        if (clock.ElapsedMilliseconds - lastSlot >= 1000)
                        {
                            RefreshSlots(target, secondTarget); lastSlot = clock.ElapsedMilliseconds;
                            if (preparePlayerOne && (watchdog == null || watchdog.HasExited))
                                throw new InvalidOperationException("El supervisor dejó de estar disponible. Se devolverá el mando físico.");
                            lock (gate) if (preparePlayerOne && (status.VirtualSlot != 0 || (twoPlayers && status.SecondVirtualSlot != 1)))
                                throw new InvalidOperationException("No se confirmaron los puestos virtuales previstos. Se devolverá el mando físico.");
                        }
                        if (!inputRoute.Accept(direct, available, input, current, directAvailable ? (GamepadFrame?)directFrame : null)) {
                            neutral(); Interlocked.Exchange(ref feedback, 0);
                            lock (gate) { status.Input = input; status.Output = status.SecondOutput = new GamepadFrame(); status.WaitingForNeutral = true;
                                status.Message = available ? "Suelta los botones y centra los sticks para cambiar la lectura del mando."
                                    : "Esperando una lectura directa reciente del mando durante Game Bar…"; }
                            continue;
                        }
                        if (!resume.AcceptInput(input, current))
                        {
                            Interlocked.Exchange(ref feedback, 0);
                            lock (gate) { status.Input = input; status.Output = status.SecondOutput = new GamepadFrame(); status.WaitingForNeutral = true; }
                            continue;
                        }
                        lock (gate) if (status.WaitingForNeutral)
                        { status.WaitingForNeutral = false; status.Message = twoPlayers ? "Dos Xbox virtuales activos. Entrada física recuperada."
                            : "Xbox 360 virtual activo. Entrada física recuperada."; }
                        if (revision != nextRevision) { neutral(); revision = nextRevision; }
                        GamepadFrame output, secondOutput = new GamepadFrame();
                        if (twoPlayers) SplitControllerProfile.Transform(input, splitPreset, out output, out secondOutput);
                        else output = current.Transform(mouse.Process(input));
                        Submit(target, output); if (secondTarget != null) Submit(secondTarget, secondOutput);
                        if (!twoPlayers && clock.ElapsedMilliseconds - lastRumble >= 100)
                        { int motors = Volatile.Read(ref feedback); source.Rumble((byte)motors, (byte)(motors >> 8), current.Rumble); lastRumble = clock.ElapsedMilliseconds; }
                        lock (gate) { status.Input = input; status.Output = output; status.SecondOutput = secondOutput; status.Frames++; }
                    }
                }
                catch (Exception ex) { trace?.Record(ControllerPreparationStage.OperationFailed); trace?.RecordFailure(ex); lock (gate) { status.Error = ex.Message;
                    status.Message = runningSession ? "Sesión virtual detenida. Se recuperará el mando físico." : automatic ? "Preparación automática detenida. Se recuperará el mando físico."
                        : preparePlayerOne ? "Preparación o sesión del jugador 1 detenida. Devuelve FPS a OFF si sigue activado." : "No se pudo mantener la salida virtual."; } }
                finally
                {
                    mouse.SetAvailable(false);
                    DisableLegionInput();
                    lock (gate) connectedTarget = null;
                    trace?.Record(ControllerPreparationStage.BeforeTargetCleanup);
                    if (target != null) { try { Submit(target, new GamepadFrame()); } catch { } try { target.Disconnect(); } catch { } }
                    if (secondTarget != null) { try { Submit(secondTarget, new GamepadFrame()); } catch { } try { secondTarget.Disconnect(); } catch { } }
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
                        status.DirectGameBarInput = false;
                        status.Suspended = status.RecoveringInput = status.WaitingForNeutral = false;
                        if (owns && recovered && status.Error != null && (automatic || runningSession)) status.Message = runningSession
                            ? "Sesión virtual detenida. Recuperación del mando físico completada."
                            : "Preparación automática detenida. Recuperación del mando físico completada.";
                        status.Input = new GamepadFrame(); status.Output = status.SecondOutput = new GamepadFrame(); status.VirtualSlot = status.SecondVirtualSlot = null;
                        status.SecondSlotError = null; status.TwoPlayers = status.GeneralSplit = false; status.SlotCheckedAtUtc = status.SlotError = null; }
                    PublishState();
                    if (owns) owner.ReleaseMutex();
                    ready?.TrySetResult(false);
                    trace?.Record(ControllerPreparationStage.Finished);
                    trace?.Dispose();
                }
            }
        }
        private sealed class ResumePort : IControllerResumePort
        {
            private readonly Action pause;
            private readonly Action<ControllerRecoveryKind> recovered;
            private readonly Func<bool> open;
            private readonly Action<bool, ControllerRecoveryKind> waiting;
            internal ResumePort(Action pause, Func<bool> open, Action<bool, ControllerRecoveryKind> waiting, Action<ControllerRecoveryKind> recovered)
            { this.pause = pause; this.open = open; this.waiting = waiting; this.recovered = recovered; }
            public void Pause() => pause();
            public bool TryOpen() => open();
            public void Waiting(bool value, ControllerRecoveryKind kind) => waiting(value, kind);
            public void Recovered(ControllerRecoveryKind kind) => recovered(kind);
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
            try { gamepadInput?.SetGamepadInputEnabled(false); } catch { }
            try { legionButtons?.SetControllerInputEnabled(false); } catch { }
            try { (legionButtons as ILegionButtonCapture)?.CancelCapture(); } catch { }
        }
        private void ResumeLegionInput()
        {
            try { legionButtons?.SetPreparationPaused(false); } catch { }
        }
        private void AutomaticPreparationProgress(PlayerOneStage stage)
        {
            lock (gate) if (status.TwoPlayers) {
                status.Message = stage == PlayerOneStage.WaitingForFreeSlot ? "Liberando temporalmente los puestos para jugadores 1 y 2…"
                    : stage == PlayerOneStage.Connecting ? "Conectando los dos Xbox virtuales…"
                    : stage == PlayerOneStage.WaitingForPhysical ? "Jugadores 1 y 2 reservados. Recuperando el mando físico…"
                    : "Comprobando ambos puestos y la entrada física…";
                return;
            }
            string message = stage == PlayerOneStage.WaitingForFreeSlot ? "Reconectando temporalmente el mando Lenovo para liberar jugador 1…"
                : stage == PlayerOneStage.WaitingForPhysical ? "Jugador 1 reservado. Recuperando la entrada del mando físico…"
                : stage == PlayerOneStage.Connecting ? "Conectando el virtual en jugador 1…"
                : "Comprobando la entrada física y la salida virtual en jugador 1…";
            lock (gate) status.Message = message;
        }
        private static void VerifyPrepared(PlayerOnePort port, bool automatic, IXbox360Controller second = null)
        {
            var state = port.Inspect();
            int targets = second == null ? 1 : 2;
            int connected = 0;
            foreach (bool value in state.ConnectedSlots) if (value) connected++;
            if (port.ReadVirtualSlot() != 0 || !state.ConnectedSlots[0] || connected != targets + 1
                || state.PhysicalDevices != 1 || state.OtherDevices != targets
                || (second != null && (second.UserIndex != 1 || !state.ConnectedSlots[1])))
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
        private void RefreshSlots(IXbox360Controller target, IXbox360Controller second)
        {
            RefreshSlot(target);
            int? slot = null; string error = null;
            if (second != null)
                try { int value = second.UserIndex; if (value >= 0 && value < 4) slot = value; else error = "El segundo virtual devolvió un puesto fuera de rango."; }
                catch { error = "No se confirmó el puesto del segundo virtual."; }
            lock (gate) { status.SecondVirtualSlot = slot; status.SecondSlotError = error; }
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
