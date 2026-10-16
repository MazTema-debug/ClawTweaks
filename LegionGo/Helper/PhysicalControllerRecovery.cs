// SPDX-License-Identifier: AGPL-3.0-only
// Microsoft Configuration Manager: exact MI_00 devnode only; disable never persists across reboot.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    internal sealed class PhysicalControllerRecovery
    {
        internal const string JournalFileName = "controller-device-recovery.json";
        internal const string LastResultFileName = "controller-device-last-result.json";
        private readonly string directory;
        private Store cachedStore;
        private Store store => cachedStore ?? (cachedStore = new Store(directory));
        private string token;
        internal PhysicalControllerRecovery(string directory) { this.directory = directory; }

        internal void DisablePhysical(string device, bool allowBusyReaders = false)
        {
            using (var owner = Process.GetCurrentProcess())
            {
                var lease = new PhysicalRecoveryLease { Token = Guid.NewGuid().ToString("N"), Sid = store.Sid,
                    Device = device, OwnerPid = owner.Id, OwnerStarted = owner.StartTime.ToUniversalTime().Ticks,
                    CreatedUtc = DateTime.UtcNow.Ticks, AllowBusyReaders = allowBusyReaders };
                lease.ExpiresUtc = lease.CreatedUtc + TimeSpan.FromMilliseconds(PhysicalRecoveryRules.LeaseMilliseconds).Ticks;
                PhysicalRecoveryRules.Arm(store, new NativeDevice(), lease, store.Sid);
                token = lease.Token;
                store.Record(lease);
            }
            // The watchdog owns all PNP workers. It is ready before any disable can run.
            StartWatch(true);
            try { WaitFor("Disabled", 12000); CheckActive(); }
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
            var lease = store.Load(); if (lease == null) return;
            PhysicalRecoveryRules.Validate(lease, store.Sid, token); token = lease.Token;
            if (!SameProcess(lease.WatchPid, lease.WatchStarted)) StartWatch(false);
            using (var restore = OpenEvent("Restore")) restore.Set();
            WaitFor("Enabled", 15000, true);
        }
        internal void CompleteRecovery()
        {
            var lease = store.Load(); if (lease == null) return;
            PhysicalRecoveryRules.Validate(lease, store.Sid, token); token = lease.Token;
            if (!SameProcess(lease.WatchPid, lease.WatchStarted))
            {
                Restore(); lease = store.Load();
                if (lease == null) return; // Independent recovery already confirmed and retired this operation.
                PhysicalRecoveryRules.Validate(lease, store.Sid, token);
            }
            if (lease.Phase != "Enabled") throw new InvalidOperationException("Primero rehabilita el mando físico.");
            using (var complete = OpenEvent("Complete")) complete.Set();
            var timer = Stopwatch.StartNew();
            while (store.Load() != null)
            {
                if (timer.ElapsedMilliseconds >= 12000) throw new TimeoutException("La confirmación de recuperación sigue pendiente; se conserva su respaldo.");
                Thread.Sleep(50);
            }
        }
        internal static void RecoverPending(string directory)
        {
            var recovery = new PhysicalControllerRecovery(directory);
            recovery.Restore(); recovery.CompleteRecovery();
        }
        private PhysicalRecoveryLease Current()
        {
            var lease = store.Load();
            return PhysicalRecoveryRules.RequireCurrent(lease, store.Sid, token, lease == null ? store.LoadOutcome() : null);
        }
        private string EventName(string suffix) => EventName(token, suffix);
        private static string EventName(string operation, string suffix) => @"Local\LegionTweaks.PhysicalRecovery." + operation + "." + suffix;
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
                using (var watch = Start("--controller-device-watch " + token + " " + EventName("Ready")))
                {
                    if (!ready.WaitOne(5000)) throw new TimeoutException("No se inició el supervisor de reconexión; no se autorizó deshabilitar el mando.");
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
                    var outcome = store.LoadOutcome();
                    if (PhysicalRecoveryRules.Matches(outcome, store.Sid, token) && outcome.Recovered) return;
                }
                lease = PhysicalRecoveryRules.RequireCurrent(lease, store.Sid, token, lease == null ? store.LoadOutcome() : null);
                if (lease.Phase == phase) return;
                if (!allowRecovered && lease.Phase == "Recovering")
                    throw new InvalidOperationException(PhysicalRecoveryRules.InterruptedMessage(store.LoadOutcome(), store.Sid, token));
                if (!SameProcess(lease.WatchPid, lease.WatchStarted)) throw new InvalidOperationException("El supervisor terminó; se conserva la recuperación pendiente del mando.");
                if (timer.ElapsedMilliseconds >= milliseconds) throw new TimeoutException("La reconexión del mando no se confirmó a tiempo; su supervisor mantiene la recuperación.");
                Thread.Sleep(50);
            }
        }
        private static Process Start(string arguments, bool captureError = false) => Process.Start(new ProcessStartInfo {
            FileName = Process.GetCurrentProcess().MainModule.FileName, Arguments = arguments,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = captureError });
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
            PhysicalRecoveryRules.Validate(lease, store.Sid, operation);
            if (readyName != EventName(operation, "Ready")) return 1;
            using (var gate = new Mutex(false, @"Global\LegionTweaks.PhysicalSupervisor." + operation))
            {
                bool owns = false;
                try
                {
                    try { owns = gate.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) return 1;
                    if (SameProcess(lease.WatchPid, lease.WatchStarted)) return 1;
                    // A replacement supervisor first terminates any recorded worker before enabling.
                    if (!StopRecordedWorker(lease)) return 1;
                    bool mayDisable = lease.Phase == "Armed" && SameProcess(lease.OwnerPid, lease.OwnerStarted) &&
                        DateTime.UtcNow.Ticks >= lease.CreatedUtc && DateTime.UtcNow.Ticks < lease.ExpiresUtc;
                    using (var self = Process.GetCurrentProcess())
                    {
                        lease.WatchPid = self.Id; lease.WatchStarted = self.StartTime.ToUniversalTime().Ticks;
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
                                lease.Phase = "Disabling"; store.Save(lease);
                                if (!RunWorker(store, lease, "disable", age, true)) return RecoverAndFinish(store, lease);
                                lease.Phase = "Disabled"; store.Save(lease);
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
        private static bool Expired(PhysicalRecoveryLease lease, Stopwatch age) =>
            age.ElapsedMilliseconds >= PhysicalRecoveryRules.LeaseMilliseconds || DateTime.UtcNow.Ticks < lease.CreatedUtc || DateTime.UtcNow.Ticks >= lease.ExpiresUtc;
        private static void Enable(Store store, PhysicalRecoveryLease lease, bool recovery)
        {
            lease.Phase = recovery ? "Recovering" : "Enabling"; store.Save(lease);
            if (!RunWorker(store, lease, "enable", Stopwatch.StartNew(), false))
                throw new InvalidOperationException("La rehabilitación del mando requiere otro intento; su respaldo se conserva.");
            lease.Phase = "Enabled"; store.Save(lease);
        }
        private static int RecoverAndFinish(Store store, PhysicalRecoveryLease lease)
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
        private static bool RunWorker(Store store, PhysicalRecoveryLease lease, string mode, Stopwatch age, bool watchOwner)
        {
            using (var child = Start("--controller-device-worker " + mode + " " + lease.Token, true))
            {
                var error = ReadWorkerError(child.StandardError);
                lease.WorkerPid = child.Id; lease.WorkerStarted = child.StartTime.ToUniversalTime().Ticks; store.Save(lease);
                var timer = Stopwatch.StartNew();
                while (!child.WaitForExit(50))
                {
                    if (timer.ElapsedMilliseconds >= 8000 || (watchOwner &&
                        (Expired(lease, age) || !SameProcess(lease.OwnerPid, lease.OwnerStarted) || IsRestoreRequested(lease.Token))))
                    {
                        try { child.Kill(); } catch (InvalidOperationException) { }
                        // End the client process before starting an enable worker. A driver/RPC may
                        // still finish later; recovery therefore confirms a stable started state.
                        if (!child.WaitForExit(2000))
                        {
                            store.Record(lease, mode, "El proceso PNP no confirmó su terminación tras exceder el tiempo permitido.");
                            return false;
                        }
                        store.Record(lease, mode, "La operación PNP se interrumpió por tiempo de espera, cancelación o cierre del helper." + WorkerError(error), child.ExitCode);
                        lease.WorkerPid = 0; lease.WorkerStarted = 0; store.Save(lease); return false;
                    }
                }
                bool success = child.ExitCode == 0;
                if (!success) store.Record(lease, mode, "El proceso PNP terminó con código " + child.ExitCode + "." + WorkerError(error), child.ExitCode);
                lease.WorkerPid = 0; lease.WorkerStarted = 0; store.Save(lease); return success;
            }
        }
        private static string WorkerError(Task<string> read)
        {
            try { return read.Wait(1000) && !string.IsNullOrWhiteSpace(read.Result) ? " " + read.Result.Trim() : ""; }
            catch { return " No se pudo leer el diagnóstico del proceso PNP."; }
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
        private static bool StopRecordedWorker(PhysicalRecoveryLease lease)
        {
            if (lease.WorkerPid == 0) return true;
            Process process;
            try { process = Process.GetProcessById(lease.WorkerPid); }
            catch (ArgumentException) { return true; }
            using (var worker = process)
            {
                if (worker.HasExited || worker.StartTime.ToUniversalTime().Ticks != lease.WorkerStarted) return true;
                if (!string.Equals(worker.MainModule.FileName, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("No se confirmó la identidad del proceso PNP; se conserva el respaldo.");
                // Access/inspection failures must retain the journal, never guess that a worker died.
                try { worker.Kill(); } catch (InvalidOperationException) { }
                return worker.WaitForExit(2000);
            }
        }
        internal static int Worker(string directory, string mode, string operation)
        {
            if (mode != "disable" && mode != "enable" && mode != "verify") return 1;
            var store = new Store(directory); var timer = Stopwatch.StartNew(); PhysicalRecoveryLease lease;
            using (var self = Process.GetCurrentProcess())
            {
                // The coordinator publishes worker PID/start before authorizing a native call.
                while (true)
                {
                    lease = store.Load(); PhysicalRecoveryRules.Validate(lease, store.Sid, operation);
                    if (lease.WorkerPid == self.Id && lease.WorkerStarted == self.StartTime.ToUniversalTime().Ticks) break;
                    if (timer.ElapsedMilliseconds >= 5000) return 1; Thread.Sleep(25);
                }
            }
            if (!SameProcess(lease.WatchPid, lease.WatchStarted)) return 1;
            var device = new NativeDevice(lease.AllowBusyReaders);
            if (mode == "disable")
            {
                PlayerOnePort.EnsureApplicationsClosed();
                PhysicalRecoveryRules.Disable(store, device, store.Sid, operation, DateTime.UtcNow.Ticks);
            }
            else if (mode == "enable")
            {
                PhysicalRecoveryRules.Enable(store, device, store.Sid, operation);
                device.ConfirmStarted(lease.Device);
            }
            else
            {
                PhysicalRecoveryRules.VerifyEnabled(store, device, store.Sid, operation);
                device.ConfirmStarted(lease.Device);
            }
            return 0;
        }
        private sealed class Store : IPhysicalRecoveryJournal
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
            internal PhysicalRecoveryOutcome LoadOutcome()
            {
                try
                {
                    if (!File.Exists(resultPath) || (File.GetAttributes(resultPath) & FileAttributes.ReparsePoint) != 0
                        || new FileInfo(resultPath).Length > 16384) return null;
                    using (var file = new FileStream(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(file, Encoding.UTF8))
                        return Protocol.Decode<PhysicalRecoveryOutcome>(reader.ReadToEnd());
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                catch (System.Runtime.Serialization.SerializationException) { return null; }
            }
            internal void Record(PhysicalRecoveryLease lease, string stage = null, string failure = null,
                int? exitCode = null, bool recovered = false, bool completed = false)
            {
                // Diagnostic storage never authorizes a transition and must not prevent recovery.
                try { Locked(() => {
                    if (File.Exists(resultPath) && (File.GetAttributes(resultPath) & FileAttributes.ReparsePoint) != 0) return;
                    var result = PhysicalRecoveryRules.RecordOutcome(LoadOutcome(), lease, Sid, DateTime.UtcNow.Ticks,
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
            public PhysicalRecoveryLease Load()
            {
                if (!File.Exists(path)) return null;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16384)
                    throw new InvalidDataException("Respaldo de reconexión inválido.");
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(file, Encoding.UTF8))
                { var lease = Protocol.Decode<PhysicalRecoveryLease>(reader.ReadToEnd()); PhysicalRecoveryRules.Validate(lease, Sid); return lease; }
            }
            public void Create(PhysicalRecoveryLease lease) => Locked(() => {
                PhysicalRecoveryRules.Validate(lease, Sid);
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Write(file, lease);
            });
            internal void Save(PhysicalRecoveryLease lease) => Locked(() => {
                var current = Load(); PhysicalRecoveryRules.Validate(current, Sid, lease.Token); PhysicalRecoveryRules.Validate(lease, Sid);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Write(file, lease);
                    File.Replace(temporary, path, null);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            });
            internal void Delete(string operation) => Locked(() => {
                var lease = Load(); PhysicalRecoveryRules.Validate(lease, Sid, operation);
                if (lease.Phase != "Enabled" || lease.WorkerPid != 0) throw new InvalidOperationException("Recuperación del mando sin confirmar.");
                File.Delete(path);
            });
            private static void Write(FileStream file, PhysicalRecoveryLease lease)
            { var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(lease)); file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            private void Locked(Action action)
            {
                using (var gate = new Mutex(false, @"Global\LegionTweaks.PhysicalRecoveryJournal." + Sid))
                {
                    bool owns = false;
                    try { try { owns = gate.WaitOne(5000); } catch (AbandonedMutexException) { owns = true; }
                        if (!owns) throw new TimeoutException("El respaldo de reconexión está ocupado."); action(); }
                    finally { if (owns) gate.ReleaseMutex(); }
                }
            }
        }
        private sealed class NativeDevice : IPhysicalRecoveryDevice
        {
            private readonly bool allowBusyReaders;
            internal NativeDevice(bool allowBusyReaders = false) { this.allowBusyReaders = allowBusyReaders; }
            public PhysicalDeviceState Inspect(string instance)
            {
                if (!PhysicalRecoveryRules.IsEndpoint(instance)) throw new InvalidOperationException("Función XInput no admitida.");
                uint node; Check(CM_Locate_DevNodeW(out node, instance, 0), "localizar");
                var id = new StringBuilder(1024); Check(CM_Get_Device_IDW(node, id, id.Capacity, 0), "identificar");
                if (!string.Equals(id.ToString(), instance, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cambió la identidad del mando.");
                uint type, size = 256; var data = new byte[size];
                Check(CM_Get_DevNode_Registry_PropertyW(node, 5, out type, data, ref size, 0), "consultar servicio");
                if (type != 1 || size < 2 || size > data.Length || (size & 1) != 0) throw new InvalidDataException("Servicio de dispositivo inválido.");
                string service = Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0');
                if (!string.Equals(service, "xusb22", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("El dispositivo no usa xusb22.");
                uint status, problem; Check(CM_Get_DevNode_Status(out status, out problem, node, 0), "consultar estado");
                return new PhysicalDeviceState { Device = id.ToString(), Service = service, Status = status, Problem = problem };
            }
            public void Disable(string instance)
            {
                Inspect(instance); uint node; Check(CM_Locate_DevNodeW(out node, instance, 0), "localizar");
                Check(CM_Disable_DevNode(node, allowBusyReaders ? PhysicalRecoveryRules.BusyReaderDisableFlags
                    : PhysicalRecoveryRules.NonPersistentDisableFlags), "desconectar temporalmente");
                Wait(instance, false);
            }
            public void Enable(string instance)
            {
                Inspect(instance); uint node; Check(CM_Locate_DevNodeW(out node, instance, 0), "localizar");
                Check(CM_Enable_DevNode(node, 0), "rehabilitar"); Wait(instance, true);
            }
            internal void ConfirmStarted(string instance)
            {
                var timer = Stopwatch.StartNew();
                do
                {
                    if (!PhysicalRecoveryRules.IsStarted(Inspect(instance)))
                        throw new InvalidOperationException("El mando físico no mantuvo un estado iniciado estable; se conserva su recuperación.");
                    Thread.Sleep(50);
                } while (timer.ElapsedMilliseconds < 750);
                if (!PhysicalRecoveryRules.IsStarted(Inspect(instance)))
                    throw new InvalidOperationException("El mando físico volvió a cambiar de estado; se conserva su recuperación.");
            }
            private void Wait(string instance, bool started)
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 6000)
                {
                    var state = Inspect(instance);
                    if (started ? PhysicalRecoveryRules.IsStarted(state) : (state.Status & PhysicalRecoveryRules.Started) == 0 &&
                        (state.Status & PhysicalRecoveryRules.HasProblem) != 0 && state.Problem == PhysicalRecoveryRules.DisabledProblem) return;
                    Thread.Sleep(50);
                }
                throw new TimeoutException("Windows no confirmó el estado del mando físico.");
            }
            private static void Check(uint result, string step)
            { if (result != 0) throw new InvalidOperationException("No se pudo " + step + " el mando (Configuration Manager 0x" + result.ToString("X") + "). Se conserva la recuperación pendiente."); }
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, int length, uint flags);
            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_DevNode_Registry_PropertyW(uint node, uint property, out uint type, byte[] buffer, ref uint length, uint flags);
            [DllImport("cfgmgr32.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
            [DllImport("cfgmgr32.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Disable_DevNode(uint node, uint flags);
            [DllImport("cfgmgr32.dll", ExactSpelling = true)]
            [DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern uint CM_Enable_DevNode(uint node, uint flags);
        }
    }
}
