// SPDX-License-Identifier: AGPL-3.0-only
// Separately supervised firmware-mode cycle. No PnP disable/reset calls.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    internal sealed class ControllerModeRecovery : IPlayerOneModeRecovery
    {
        internal const string JournalFileName = "controller-mode-recovery.json";
        internal const string LastResultFileName = "controller-mode-last-result.json";
        private readonly string directory;
        private Store cachedStore;
        private Store store => cachedStore ?? (cachedStore = new Store(directory));
        private string token;
        private long createdUtc;
        internal ControllerModeRecovery(string directory) { this.directory = directory; }

        ControllerModeState IPlayerOneModeRecovery.Inspect() => ReadCurrent();
        void IPlayerOneModeRecovery.EnterDirectInput(string receiver, string location) => EnterDirectInput(receiver, location);
        void IPlayerOneModeRecovery.CheckActive() => CheckActive();
        void IPlayerOneModeRecovery.Restore() => Restore();
        void IPlayerOneModeRecovery.CompleteRecovery() => CompleteRecovery();

        internal void EnterDirectInput() => EnterDirectInput(null, null);
        private void EnterDirectInput(string expectedReceiver, string expectedLocation)
        {
            AssertNoOtherRecovery(directory);
            PlayerOneApplications.EnsureModeAllowed();
            DeviceIdentity machine = Machine();
            var initial = ReadCurrent();
            if ((expectedReceiver == null) != (expectedLocation == null) || (expectedReceiver != null &&
                (!string.Equals(initial.Receiver, expectedReceiver, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(initial.Location, expectedLocation, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Cambió el receptor antes de preparar el jugador 1; no se cambió su modo.");
            using (var owner = Process.GetCurrentProcess())
            {
                var lease = new ControllerModeLease { Token = Guid.NewGuid().ToString("N"), Sid = store.Sid,
                    OriginalReceiver = initial.Receiver, OriginalLocation = initial.Location, OwnerPid = owner.Id, OwnerStarted = owner.StartTime.ToUniversalTime().Ticks,
                    CreatedUtc = DateTime.UtcNow.Ticks, Phase = "Armed" };
                lease.ExpiresUtc = lease.CreatedUtc + TimeSpan.FromMilliseconds(ControllerModeRecoveryRules.LeaseMilliseconds).Ticks;
                ControllerModeRecoveryRules.Arm(store, new RemoteDevice(), lease, store.Sid, machine);
                lease = store.Load();
                token = lease.Token; createdUtc = lease.CreatedUtc;
                store.Record(lease);
            }
            // The watchdog owns every writer. No native mutation can precede readiness.
            try { StartWatch(true); WaitFor("Temporary", 15000); CheckActive(); }
            catch { try { Restore(); } catch { } throw; }
        }
        internal void CheckActive()
        {
            var lease = Current();
            if (DateTime.UtcNow.Ticks < lease.CreatedUtc || DateTime.UtcNow.Ticks >= lease.ExpiresUtc ||
                !SameProcess(lease.WatchPid, lease.WatchStarted) || lease.Phase == "Recovering")
                throw new InvalidOperationException("La recuperación independiente del mando terminó o venció; vuelve a iniciar la preparación.");
        }
        // Re-enable, but retain the original journal and its independent deadline until final checks complete.
        internal void Restore()
        {
            try
            {
                var lease = store.Load();
                if (lease == null) { if (token != null) RequireCompletedRetirement(); return; }
                Remember(lease);
                if (!SameProcess(lease.WatchPid, lease.WatchStarted)) StartWatch(false);
                using (var restore = OpenEvent("Restore")) restore.Set();
                WaitFor("Restored", 15000, true);
            }
            catch (Exception) when (CompletedRetirement())
            {
                // The supervisor may retire the journal and close events between
                // our snapshot and OpenExisting/Set. Its confirmed recovery wins.
            }
        }
        internal void CompleteRecovery()
        {
            try
            {
                var lease = store.Load();
                if (lease == null) { if (token != null) RequireCompletedRetirement(); return; }
                Remember(lease);
                if (!SameProcess(lease.WatchPid, lease.WatchStarted))
                {
                    Restore(); lease = store.Load();
                    if (lease == null) { RequireCompletedRetirement(); return; }
                    Remember(lease);
                }
                if (lease.Phase != "Restored") throw new InvalidOperationException("Primero rehabilita el mando físico.");
                using (var complete = OpenEvent("Complete")) complete.Set();
                var timer = Stopwatch.StartNew();
                while (true)
                {
                    var current = store.Load();
                    if (current == null) { RequireCompletedRetirement(); return; }
                    ControllerModeRecoveryRules.Validate(current, store.Sid, token);
                    if (timer.ElapsedMilliseconds >= 12000) throw new TimeoutException("La confirmación de recuperación sigue pendiente; se conserva su respaldo.");
                    Thread.Sleep(50);
                }
            }
            catch (Exception) when (CompletedRetirement())
            {
                // Do not report pending recovery after the same operation was
                // independently verified, recorded and retired by its supervisor.
            }
        }
        private void Remember(ControllerModeLease lease)
        {
            ControllerModeRecoveryRules.Validate(lease, store.Sid, token);
            if (createdUtc != 0 && createdUtc != lease.CreatedUtc)
                throw new InvalidDataException("Cambió el inicio de la recuperación del mando; se conserva su respaldo.");
            token = lease.Token; createdUtc = lease.CreatedUtc;
        }
        private bool CompletedRetirement() => token != null && createdUtc > 0 && store.IsCompletedAfterRetirement(token, createdUtc);
        private void RequireCompletedRetirement()
        {
            if (!CompletedRetirement())
                throw new InvalidOperationException("No se confirmó la recuperación completada del mando. "
                    + ControllerModeRecoveryRules.InterruptedMessage(store.LoadOutcome(), store.Sid, token));
        }
        internal static void RecoverPending(string directory)
        {
            var recovery = new ControllerModeRecovery(directory);
            recovery.Restore(); recovery.CompleteRecovery();
        }
        private ControllerModeLease Current()
        {
            var lease = store.Load();
            return ControllerModeRecoveryRules.RequireCurrent(lease, store.Sid, token, lease == null ? store.LoadOutcome() : null);
        }
        private string EventName(string suffix) => EventName(token, suffix);
        private static string EventName(string operation, string suffix) => @"Local\LegionTweaks.ModeRecovery." + operation + "." + suffix;
        private EventWaitHandle OpenEvent(string suffix) => EventWaitHandle.OpenExisting(EventName(suffix));
        private void StartWatch(bool begin)
        {
            var lease = Current();
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName("Ready")))
            using (var start = new EventWaitHandle(false, EventResetMode.ManualReset, EventName("Begin")))
            using (var restore = new EventWaitHandle(false, EventResetMode.ManualReset, EventName("Restore")))
            using (var complete = new EventWaitHandle(false, EventResetMode.ManualReset, EventName("Complete")))
            {
                ready.Reset(); start.Reset(); restore.Reset(); complete.Reset();
                if (!begin) restore.Set();
                using (var watch = Start("--controller-mode-watch " + token + " " + EventName("Ready")))
                {
                    if (!ready.WaitOne(5000)) throw new TimeoutException("No se inició el supervisor del modo; no se autorizó el cambio temporal.");
                    lease = Current();
                    if (lease.WatchPid != watch.Id || lease.WatchStarted != watch.StartTime.ToUniversalTime().Ticks || watch.HasExited)
                        throw new InvalidOperationException("No se confirmó la identidad del supervisor de reconexión.");
                    if (begin) start.Set();
                }
            }
        }
        private void WaitFor(string phase, int milliseconds, bool allowRecovered = false)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var lease = store.Load();
                if (lease == null && allowRecovered)
                {
                    if (CompletedRetirement()) return;
                }
                lease = ControllerModeRecoveryRules.RequireCurrent(lease, store.Sid, token, lease == null ? store.LoadOutcome() : null);
                if (lease.Phase == phase) return;
                if (!allowRecovered && lease.Phase == "Recovering")
                    throw new InvalidOperationException(ControllerModeRecoveryRules.InterruptedMessage(store.LoadOutcome(), store.Sid, token));
                if (!SameProcess(lease.WatchPid, lease.WatchStarted)) throw new InvalidOperationException("El supervisor terminó; se conserva la recuperación pendiente del mando.");
                if (timer.ElapsedMilliseconds >= milliseconds) throw new TimeoutException("La reconexión del mando no se confirmó a tiempo; su supervisor mantiene la recuperación.");
                Thread.Sleep(50);
            }
        }
        private static Process Start(string arguments, bool captureError = false, bool captureOutput = false) => Process.Start(new ProcessStartInfo {
            FileName = Process.GetCurrentProcess().MainModule.FileName, Arguments = arguments,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = captureError, RedirectStandardOutput = captureOutput });

        private static DeviceIdentity Machine()
        {
            using (var hardware = new LenovoHardware()) {
                if (!hardware.Identity.IsSupported) throw new InvalidOperationException("Modelo o CPU no admitidos para el modo del mando.");
                return hardware.Identity;
            }
        }

        private static void AssertNoOtherRecovery(string directory)
        {
            foreach (string name in new[] { "controller-lease.json", PhysicalControllerRecovery.JournalFileName, JournalFileName }) {
                try { File.GetAttributes(Path.Combine(directory, name)); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                throw new InvalidOperationException("Devuelve el mando físico y resuelve cualquier recuperación pendiente antes del ensayo de modo.");
            }
        }

        internal static ControllerModeState ReadCurrent()
        {
            using (var child = Start("--controller-mode-query", true, true)) {
                var output = ReadWorkerError(child.StandardOutput);
                var error = ReadWorkerError(child.StandardError);
                try {
                    if (!child.WaitForExit(10000)) throw new TimeoutException("La consulta del modo excedió su límite.");
                    if (child.ExitCode != 0) throw new InvalidOperationException("No se pudo consultar el modo." + WorkerError(error));
                    if (!output.Wait(1000)) throw new InvalidOperationException("La consulta no entregó su respuesta.");
                    return Protocol.Decode<ControllerModeState>(output.Result.Trim('\ufeff', '\r', '\n', ' '));
                }
                finally { if (!child.HasExited) { child.Kill(); if (!child.WaitForExit(2000)) throw new InvalidOperationException("El lector del modo no confirmó su salida."); } }
            }
        }

        internal static int QueryWorker(string directory)
        {
            using (var deadline = new Timer(_ => Environment.Exit(124), null, 8000, Timeout.Infinite))
            {
            Machine();
            var lease = new Store(directory).Load();
            var state = new NativeControllerModeDevice().Inspect(lease == null ? null : lease.OriginalReceiver, lease == null ? null : lease.OriginalLocation);
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(Protocol.Encode(state));
            return 0;
            }
        }

        private sealed class RemoteDevice : IControllerModeDevice
        {
            public ControllerModeState Inspect(string originalReceiver, string location)
            {
                var state = ReadCurrent();
                if (!string.Equals(state.Location, location, StringComparison.OrdinalIgnoreCase)
                    || (state.Mode == 1 ? !string.Equals(state.Receiver, originalReceiver, StringComparison.OrdinalIgnoreCase)
                        : !ControllerModeProtocol.IsTemporaryReceiver(state.Receiver)))
                    throw new InvalidDataException("Cambió el receptor durante la consulta del modo original.");
                return state;
            }
            public void ChangeMode(string originalReceiver, string location, int mode) { throw new InvalidOperationException("Este adaptador solo consulta el modo."); }
        }

        internal static void TestCycle(string directory)
        {
            // First hardware trials use the installed helper so reboot/update/uninstall
            // all understand the same recovery journal. No development executable may
            // leave a journal that the installed version cannot recover.
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LegionTweaks-Z1E", "LegionTweaks.Helper.exe");
            using (var self = Process.GetCurrentProcess())
                if (!string.Equals(self.MainModule.FileName, installed, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Instala este helper antes de ensayar un cambio de modo.");
            using (var owner = new Mutex(false, @"Global\LegionTweaks.ControllerOwner")) {
                bool owns = false;
                try {
                    try { owns = owner.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new InvalidOperationException("Otra sesión está usando el mando.");
                    var port = new CyclePort(directory);
                    ControllerModeCycle.Run(port);
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.WriteLine(Protocol.Encode(new CycleResult { OriginalMode = 1, TemporaryMode = 2,
                        OriginalRecovered = true, TemporarySlotsFree = true, BeforeSlots = port.BeforeSlots, AfterSlots = port.AfterSlots }));
                }
                finally { if (owns) owner.ReleaseMutex(); }
            }
        }

        internal static int RestorePhysicalPlayerOne(string directory)
        {
            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LegionTweaks-Z1E", "LegionTweaks.Helper.exe");
            using (var self = Process.GetCurrentProcess())
                if (!string.Equals(self.MainModule.FileName, installed, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Instala este helper antes de recuperar el puesto del mando físico.");
            using (var owner = new Mutex(false, @"Global\LegionTweaks.ControllerOwner")) {
                bool owns = false;
                try {
                    try { owns = owner.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) throw new InvalidOperationException("Otra sesión está usando el mando.");
                    return PhysicalPlayerOneReturn.Run(new PhysicalReturnPort(directory));
                }
                finally { if (owns) owner.ReleaseMutex(); }
            }
        }

        private sealed class PhysicalReturnPort : IPhysicalPlayerOneReturnPort
        {
            private readonly CyclePort cycle;
            internal PhysicalReturnPort(string directory) { cycle = new CyclePort(directory); }
            public void CheckInitial() => cycle.CheckInitial(false);
            public bool[] ReadSlots()
            { using (var stop = new ManualResetEvent(false)) return XInputSlotProbe.Read(stop, () => { }); }
            public void Cycle() => ControllerModeCycle.Run(cycle);
        }

        [System.Runtime.Serialization.DataContract]
        private sealed class CycleResult
        {
            [System.Runtime.Serialization.DataMember] public int OriginalMode;
            [System.Runtime.Serialization.DataMember] public int TemporaryMode;
            [System.Runtime.Serialization.DataMember] public bool OriginalRecovered;
            [System.Runtime.Serialization.DataMember] public bool TemporarySlotsFree;
            [System.Runtime.Serialization.DataMember] public bool[] BeforeSlots;
            [System.Runtime.Serialization.DataMember] public bool[] AfterSlots;
        }

        private sealed class CyclePort : IControllerModeCyclePort
        {
            private readonly string directory;
            private readonly ControllerModeRecovery recovery;
            private string original, location;
            internal bool[] BeforeSlots, AfterSlots;
            internal CyclePort(string directory) { this.directory = directory; recovery = new ControllerModeRecovery(directory); }
            private static bool[] Slots()
            {
                using (var stop = new ManualResetEvent(false)) return XInputSlotProbe.Read(stop, () => { });
            }
            public void CheckInitial()
            {
                CheckInitial(true);
            }
            internal void CheckInitial(bool requireClosedGames)
            {
                AssertNoOtherRecovery(directory);
                if (requireClosedGames) PlayerOneApplications.EnsureModeAllowed();
                var state = ReadCurrent();
                if (state.Mode != 1 || !ControllerModeProtocol.IsOriginalReceiver(state.Receiver)) throw new InvalidOperationException("Se necesita el mando en su modo original y FPS OFF.");
                original = state.Receiver; location = state.Location;
                var physical = ControllerDevices.XusbInstances();
                if (physical.Length != 1 || !PlayerOnePort.IsPhysicalEndpoint(physical[0])
                    || !string.Equals(NativeControllerModeDevice.RootForEndpoint(physical[0]), original, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Se necesita un único mando físico identificado.");
                BeforeSlots = Slots();
                if (BeforeSlots.Count(x => x) != 1) throw new InvalidOperationException("Los puestos XInput no coinciden con el mando físico único.");
            }
            public void EnterDirectInput() { recovery.EnterDirectInput(); }
            public void VerifyTemporary()
            {
                recovery.CheckActive();
                var state = ReadCurrent();
                if (state.Mode != 2 || !ControllerModeProtocol.IsTemporaryReceiver(state.Receiver) || !string.Equals(state.Location, location, StringComparison.OrdinalIgnoreCase)
                    || ControllerDevices.XusbInstances().Length != 0 || Slots().Any(x => x))
                    throw new InvalidOperationException("DirectInput no confirmó los puestos XInput libres.");
                recovery.CheckActive();
            }
            public void RestoreOriginal() { recovery.Restore(); }
            public void VerifyOriginal()
            {
                var state = ReadCurrent();
                if (state.Mode != 1 || !string.Equals(state.Receiver, original, StringComparison.OrdinalIgnoreCase) || !string.Equals(state.Location, location, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("No se confirmó el modo original del mando.");
                AfterSlots = Slots();
                if (AfterSlots.Count(x => x) != 1) throw new InvalidOperationException("No se confirmó un único mando XInput recuperado.");
            }
            public void CompleteRecovery() { recovery.CompleteRecovery(); }
        }
        private static bool SameProcess(int pid, long started)
        {
            if (pid <= 0 || started <= 0) return false;
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited &&
                process.StartTime.ToUniversalTime().Ticks == started && string.Equals(process.MainModule.FileName,
                    Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        // These entry points accept only a protected journal token, never a device ID or output path.
        internal static int Watch(string directory, string operation, string readyName)
        {
            var store = new Store(directory); var lease = store.Load();
            ControllerModeRecoveryRules.Validate(lease, store.Sid, operation);
            if (readyName != EventName(operation, "Ready")) return 1;
            using (var gate = new Mutex(false, @"Global\LegionTweaks.ModeSupervisor." + operation))
            {
                bool owns = false;
                try
                {
                    try { owns = gate.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) return 1;
                    if (SameProcess(lease.WatchPid, lease.WatchStarted)) return 1;
                    // A replacement supervisor first terminates any recorded worker.
                    if (!StopRecordedWorker(lease)) return 1;
                    bool mayDisable = lease.Phase == "Armed" && SameProcess(lease.OwnerPid, lease.OwnerStarted) &&
                        DateTime.UtcNow.Ticks >= lease.CreatedUtc && DateTime.UtcNow.Ticks < lease.ExpiresUtc;
                    using (var self = Process.GetCurrentProcess())
                    {
                        lease.WatchPid = self.Id; lease.WatchStarted = self.StartTime.ToUniversalTime().Ticks;
                        if (!mayDisable) lease.Phase = "Recovering";
                        lease.WorkerPid = 0; lease.WorkerStarted = 0; store.Save(lease);
                    }
                    using (var begin = EventWaitHandle.OpenExisting(EventName(operation, "Begin")))
                    using (var restore = EventWaitHandle.OpenExisting(EventName(operation, "Restore")))
                    using (var complete = EventWaitHandle.OpenExisting(EventName(operation, "Complete")))
                    using (var ready = EventWaitHandle.OpenExisting(readyName))
                    {
                        ready.Set();
                        var age = Stopwatch.StartNew(); // Monotonic fallback even if UTC moves backwards.
                        bool enabled = false;
                        try
                        {
                            if (!mayDisable || restore.WaitOne(0)) { Enable(store, lease, false); enabled = true; }
                            else
                            {
                                // Missing handshake or dead owner can only request recovery.
                                if (WaitHandle.WaitAny(new WaitHandle[] { begin, restore }, 5000) != 0 ||
                                    !SameProcess(lease.OwnerPid, lease.OwnerStarted))
                                {
                                    store.Record(lease, "handshake", "No se recibió la autorización de inicio o el helper dejó de estar activo.");
                                    return RecoverAndFinish(store, lease);
                                }
                                if (!RunWorker(store, lease, "switch", age, true)) return RecoverAndFinish(store, lease);
                                lease.Phase = "Temporary"; store.Save(lease);
                            }
                            while (true)
                            {
                                if (!SameProcess(lease.OwnerPid, lease.OwnerStarted) || Expired(lease, age))
                                {
                                    store.Record(lease, "supervisor", "El helper terminó o venció el límite independiente de 30 segundos.");
                                    return RecoverAndFinish(store, lease);
                                }
                                if (!enabled && restore.WaitOne(0)) { Enable(store, lease, false); enabled = true; }
                                if (enabled && complete.WaitOne(0))
                                {
                                    if (!RunWorker(store, lease, "verify", age, false)) return RecoverAndFinish(store, lease);
                                    store.Record(lease, recovered: true, completed: true);
                                    store.Delete(operation); return 0;
                                }
                                Thread.Sleep(50);
                            }
                        }
                        catch (Exception ex)
                        {
                            store.Record(lease, "supervisor", ex.Message);
                            return RecoverAndFinish(store, lease);
                        }
                    }
                }
                finally { if (owns) gate.ReleaseMutex(); }
            }
        }
        private static bool Expired(ControllerModeLease lease, Stopwatch age) =>
            age.ElapsedMilliseconds >= ControllerModeRecoveryRules.LeaseMilliseconds || DateTime.UtcNow.Ticks < lease.CreatedUtc || DateTime.UtcNow.Ticks >= lease.ExpiresUtc;
        private static void Enable(Store store, ControllerModeLease lease, bool recovery)
        {
            lease.Phase = "Recovering"; store.Save(lease);
            if (!RunWorker(store, lease, "restore", Stopwatch.StartNew(), false))
                throw new InvalidOperationException("La rehabilitación del mando requiere otro intento; su respaldo se conserva.");
            lease.Phase = "Restored"; store.Save(lease);
        }
        private static int RecoverAndFinish(Store store, ControllerModeLease lease)
        {
            // No ControllerOwner/HidHide/native-call mutex is required for recovery.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!StopRecordedWorker(lease)) { Thread.Sleep(500); continue; }
                    Enable(store, lease, true);
                    // Terminating the client is not proof of native RPC cancellation. Observe
                    // several fresh started states before retiring recovery ownership.
                    for (int confirm = 0; confirm < 2; confirm++)
                    {
                        Thread.Sleep(1000);
                        if (!RunWorker(store, lease, "verify", Stopwatch.StartNew(), false))
                            throw new InvalidOperationException("El estado rehabilitado del mando aún no es estable.");
                    }
                    store.Record(lease, recovered: true, completed: true);
                    store.Delete(lease.Token); return 0;
                }
                catch (Exception ex) { store.Record(lease, "recovery", ex.Message); Thread.Sleep(500); }
            }
            return 1; // Leave durable intent when unplugged, missing, or the driver still cannot recover.
        }
        private static bool RunWorker(Store store, ControllerModeLease lease, string mode, Stopwatch age, bool watchOwner)
        {
            using (var child = Start("--controller-mode-worker " + mode + " " + lease.Token, true))
            {
                var error = ReadWorkerError(child.StandardError);
                lease.WorkerPid = child.Id; lease.WorkerStarted = child.StartTime.ToUniversalTime().Ticks;
                if (mode == "switch") lease.Phase = "Switching";
                store.Save(lease);
                var timer = Stopwatch.StartNew();
                while (!child.WaitForExit(50))
                {
                    if (timer.ElapsedMilliseconds >= 12000 || (watchOwner &&
                        (Expired(lease, age) || !SameProcess(lease.OwnerPid, lease.OwnerStarted) || IsRestoreRequested(lease.Token))))
                    {
                        try { child.Kill(); } catch (InvalidOperationException) { }
                        // End the client process before starting an enable worker. A driver/RPC may
                        // still finish later; recovery therefore confirms a stable started state.
                        if (!child.WaitForExit(2000))
                        {
                            store.Record(lease, mode, "El proceso de modo no confirmó su terminación tras exceder el tiempo permitido.");
                            return false;
                        }
                        store.Record(lease, mode, "La operación de modo se interrumpió por tiempo de espera, cancelación o cierre del helper." + WorkerError(error), child.ExitCode);
                        if (mode == "switch") lease.Phase = "Recovering";
                        lease.WorkerPid = 0; lease.WorkerStarted = 0; store.Save(lease); return false;
                    }
                }
                bool success = child.ExitCode == 0;
                if (!success) store.Record(lease, mode, "El proceso de modo terminó con código " + child.ExitCode + "." + WorkerError(error), child.ExitCode);
                if (mode == "switch") lease.Phase = success ? "Temporary" : "Recovering";
                lease.WorkerPid = 0; lease.WorkerStarted = 0; store.Save(lease); return success;
            }
        }
        private static string WorkerError(Task<string> read)
        {
            try { return read.Wait(1000) && !string.IsNullOrWhiteSpace(read.Result) ? " " + read.Result.Trim() : ""; }
            catch { return " No se pudo leer el diagnóstico del proceso de modo."; }
        }
        private static async Task<string> ReadWorkerError(StreamReader reader)
        {
            var message = new StringBuilder(); var buffer = new char[256]; int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                if (message.Length < 2048) message.Append(buffer, 0, Math.Min(count, 2048 - message.Length));
            return message.ToString();
        }
        private static bool IsRestoreRequested(string operation)
        { using (var restore = EventWaitHandle.OpenExisting(EventName(operation, "Restore"))) return restore.WaitOne(0); }
        private static bool StopRecordedWorker(ControllerModeLease lease)
        {
            if (lease.WorkerPid == 0) return true;
            Process process;
            try { process = Process.GetProcessById(lease.WorkerPid); }
            catch (ArgumentException) { return true; }
            using (var worker = process)
            {
                if (worker.HasExited || worker.StartTime.ToUniversalTime().Ticks != lease.WorkerStarted) return true;
                if (!string.Equals(worker.MainModule.FileName, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("No se confirmó la identidad del proceso de modo; se conserva el respaldo.");
                // Access/inspection failures must retain the journal, never guess that a worker died.
                try { worker.Kill(); } catch (InvalidOperationException) { }
                return worker.WaitForExit(2000);
            }
        }
        internal static int Worker(string directory, string mode, string operation)
        {
            if (mode != "switch" && mode != "restore" && mode != "verify") return 1;
            // Also bound orphaned workers if their supervising process is terminated.
            using (var deadline = new Timer(_ => Environment.Exit(124), null, 15000, Timeout.Infinite))
            {
            var store = new Store(directory); var timer = Stopwatch.StartNew(); ControllerModeLease lease;
            using (var self = Process.GetCurrentProcess())
            {
                // The coordinator publishes worker PID/start before authorizing a native call.
                while (true)
                {
                    lease = store.Load(); ControllerModeRecoveryRules.Validate(lease, store.Sid, operation);
                    if (lease.WorkerPid == self.Id && lease.WorkerStarted == self.StartTime.ToUniversalTime().Ticks) break;
                    if (timer.ElapsedMilliseconds >= 5000) return 1; Thread.Sleep(25);
                }
            }
            if (!SameProcess(lease.WatchPid, lease.WatchStarted)) return 1;
            var machine = Machine();
            var original = lease;
            var device = new NativeControllerModeDevice(requested => {
                var current = store.Load(); ControllerModeRecoveryRules.Validate(current, store.Sid, operation);
                ControllerModeRecoveryRules.RequireSameOriginal(original, current);
                using (var self = Process.GetCurrentProcess())
                    if (current.WorkerPid != self.Id || current.WorkerStarted != self.StartTime.ToUniversalTime().Ticks)
                        throw new InvalidOperationException("Cambió el propietario del proceso de modo.");
                if (!SameProcess(current.WatchPid, current.WatchStarted)) throw new InvalidOperationException("El supervisor de modo no está activo.");
                if (requested == 2) {
                    PlayerOneApplications.EnsureModeAllowed();
                    if (mode != "switch" || current.Phase != "Switching" || IsRestoreRequested(operation)
                        || !SameProcess(current.OwnerPid, current.OwnerStarted) || timer.ElapsedMilliseconds >= 12000
                        || DateTime.UtcNow.Ticks < current.CreatedUtc || DateTime.UtcNow.Ticks >= current.ExpiresUtc)
                        throw new InvalidOperationException("El cambio temporal de modo ya no está autorizado.");
                }
                else if (requested != 1 || mode != "restore" || current.Phase != "Recovering")
                    throw new InvalidOperationException("La restauración del modo no está autorizada.");
            });
            if (mode == "switch")
            {
                PlayerOneApplications.EnsureModeAllowed();
                ControllerModeRecoveryRules.SwitchToDirectInput(store, device, store.Sid, operation, machine, () => DateTime.UtcNow.Ticks);
            }
            else if (mode == "restore")
            {
                ControllerModeRecoveryRules.Restore(store, device, store.Sid, operation, machine);
            }
            else
            {
                ControllerModeRecoveryRules.VerifyRestored(store, device, store.Sid, operation, machine);
                var physical = ControllerDevices.XusbInstances().Where(PlayerOnePort.IsPhysicalEndpoint).ToArray();
                if (physical.Length != 1 || !string.Equals(NativeControllerModeDevice.RootForEndpoint(physical[0]), lease.OriginalReceiver, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("No se confirmó la función XInput del receptor original tras recuperar el modo.");
            }
            return 0;
            }
        }
        private sealed class Store : IControllerModeJournal
        {
            private readonly string path;
            private readonly string resultPath;
            internal readonly string Sid;
            internal Store(string directory)
            {
                Sid = WindowsIdentity.GetCurrent().User.Value;
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionTweaks", Sid);
                if (!string.Equals(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("La recuperación solo admite la carpeta protegida del usuario actual.");
                for (var item = new DirectoryInfo(directory); item != null && item.Exists; item = item.Parent)
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("La carpeta de recuperación no puede ser un enlace.");
                path = Path.Combine(directory, JournalFileName);
                resultPath = Path.Combine(directory, LastResultFileName);
            }
            internal ControllerModeOutcome LoadOutcome()
            {
                try
                {
                    if (!File.Exists(resultPath) || (File.GetAttributes(resultPath) & FileAttributes.ReparsePoint) != 0
                        || new FileInfo(resultPath).Length > 16384) return null;
                    using (var file = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(file, Encoding.UTF8))
                        return Protocol.Decode<ControllerModeOutcome>(reader.ReadToEnd());
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                catch (System.Runtime.Serialization.SerializationException) { return null; }
            }
            internal void Record(ControllerModeLease lease, string stage = null, string failure = null,
                int? exitCode = null, bool recovered = false, bool completed = false)
            {
                // Diagnostic storage never authorizes a transition and must not prevent recovery.
                try { Locked(() => {
                    if (File.Exists(resultPath) && (File.GetAttributes(resultPath) & FileAttributes.ReparsePoint) != 0) return;
                    var result = ControllerModeRecoveryRules.RecordOutcome(LoadOutcome(), lease, Sid, DateTime.UtcNow.Ticks,
                        stage, failure, exitCode, recovered, completed);
                    string temporary = resultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(result));
                            file.Write(bytes, 0, bytes.Length); file.Flush(true);
                        }
                        if (File.Exists(resultPath)) File.Replace(temporary, resultPath, null); else File.Move(temporary, resultPath);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (TimeoutException) { }
            }
            public ControllerModeLease Load()
            {
                ControllerModeLease lease = null;
                Locked(() => lease = ReadLease());
                return lease;
            }
            internal bool IsCompletedAfterRetirement(string operation, long started)
            {
                bool completed = false;
                // Creation, retirement and the outcome snapshot share this lock.
                // Never accept a past outcome if any current journal is present.
                Locked(() => completed = ControllerModeRecoveryRules.IsCompletedAfterRetirement(ReadLease(), LoadOutcome(), Sid, operation, started));
                return completed;
            }
            private ControllerModeLease ReadLease()
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                // File.Exists also returns false for access errors, which must not
                // be mistaken for a retired journal when settling recovery races.
                if ((attributes & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16384)
                    throw new InvalidDataException("Respaldo de reconexión inválido.");
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file, Encoding.UTF8))
                { var lease = Protocol.Decode<ControllerModeLease>(reader.ReadToEnd()); ControllerModeRecoveryRules.Validate(lease, Sid); return lease; }
            }
            public void Create(ControllerModeLease lease) => Locked(() => {
                ControllerModeRecoveryRules.Validate(lease, Sid);
                AssertNoOtherRecovery(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Write(file, lease);
                    File.Move(temporary, path); // Fails if any journal already exists.
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            internal void Save(ControllerModeLease lease) => Locked(() => {
                var current = ReadLease(); ControllerModeRecoveryRules.Validate(current, Sid, lease.Token); ControllerModeRecoveryRules.Validate(lease, Sid);
                ControllerModeRecoveryRules.RequireSameOriginal(current, lease);
                if (!ControllerModeRecoveryRules.CanTransitionPhase(current.Phase, lease.Phase))
                    throw new InvalidOperationException("La recuperación ya comenzó; no se autoriza otra transición.");
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Write(file, lease);
                    File.Replace(temporary, path, null);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            internal void Delete(string operation) => Locked(() => {
                var lease = ReadLease(); ControllerModeRecoveryRules.Validate(lease, Sid, operation);
                if (lease.Phase != "Restored" || lease.WorkerPid != 0) throw new InvalidOperationException("Recuperación del mando sin confirmar.");
                if (!ControllerModeRecoveryRules.IsCompletedAfterRetirement(null, LoadOutcome(), Sid, operation, lease.CreatedUtc))
                    throw new InvalidOperationException("No se pudo conservar el resultado de la recuperación; se mantiene el respaldo.");
                File.Delete(path);
            });
            private static void Write(FileStream file, ControllerModeLease lease)
            { var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(lease)); file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            private void Locked(Action action)
            {
                using (var gate = new Mutex(false, @"Global\LegionTweaks.ModeRecoveryJournal." + Sid))
                {
                    bool owns = false;
                    try { try { owns = gate.WaitOne(5000); } catch (AbandonedMutexException) { owns = true; }
                        if (!owns) throw new TimeoutException("El respaldo de reconexión está ocupado."); action(); }
                    finally { if (owns) gate.ReleaseMutex(); }
                }
            }
        }
    }
}

