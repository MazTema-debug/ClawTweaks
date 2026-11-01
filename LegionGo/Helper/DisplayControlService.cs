// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class DisplayStore
    {
        internal const string PendingName = "display-change.json";
        private readonly string directory;
        private volatile bool knownResult;
        internal DisplayStore(string directory) { this.directory = directory; }
        private string PathFor(string name)
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("La carpeta protegida de pantalla no está disponible.");
            string path = Path.Combine(directory, name);
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("El respaldo de pantalla no puede ser un enlace.");
            }
            catch (FileNotFoundException) { }
            return path;
        }
        private T Read<T>(string name) where T : class
        {
            string path = PathFor(name);
            try
            {
                // Readers must allow atomic replacement by the independent worker.
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (file.Length > Protocol.MaxMessageLength) throw new InvalidDataException("El registro de pantalla excede el límite.");
                    using (var reader = new StreamReader(file, Encoding.UTF8)) return Protocol.Decode<T>(reader.ReadToEnd());
                }
            }
            catch (FileNotFoundException) { return null; }
        }
        private void Write<T>(string name, T value, bool createOnly)
        {
            string path = PathFor(name);
            byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Encode(value));
            if (bytes.Length > Protocol.MaxMessageLength) throw new InvalidDataException("El registro de pantalla excede el límite.");
            string temporary = PathFor(name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (createOnly || !File.Exists(path)) File.Move(temporary, path);
                else File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal DisplayPlan Plan()
        {
            var plan = Read<DisplayPlan>(PendingName);
            if (plan != null) DisplayTrialRules.ValidatePlan(plan);
            return plan;
        }
        internal DisplayTrialResult Result()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    var result = Read<DisplayTrialResult>("display-last-result.json");
                    if (result != null) { knownResult = true; return result; }
                    if (!knownResult) return null;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33) { }
                // ReplaceFile may expose a brief missing-name window to a fresh open.
                Thread.Sleep(5);
            }
            throw new IOException("No se pudo leer el resultado de pantalla; vuelve a consultar.");
        }
        internal void Arm(DisplayPlan plan) { DisplayTrialRules.ValidatePlan(plan); Write(PendingName, plan, true); }
        internal void Save(DisplayTrialResult result) { Write("display-last-result.json", result, false); knownResult = true; }
        private static string DecisionName(string token)
        {
            Guid parsed;
            if (!Guid.TryParseExact(token, "N", out parsed)) throw new ArgumentException("La prueba de pantalla no es válida.");
            return "display-decision-" + token + ".json";
        }
        internal string Decision(string token) => Read<string>(DecisionName(token));
        internal void Decide(string token, string decision)
        {
            string old = Decision(token);
            if (old != null)
            {
                if (old != decision) throw new InvalidOperationException("La decisión anterior todavía se está procesando.");
                return;
            }
            Write(DecisionName(token), decision, true);
        }
        internal void Retire(string token)
        {
            var plan = Plan(); var result = Result();
            if (plan?.Token != token || result?.Token != token || result.Pending) throw new InvalidOperationException("La recuperación de pantalla aún no está confirmada.");
            File.Delete(PathFor(PendingName));
            string decision = PathFor(DecisionName(token));
            if (File.Exists(decision)) File.Delete(decision);
        }
        internal static bool Alive(int pid, long started)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return false; }
        }
    }
    internal sealed class DisplayControlService : IDisplayControl
    {
        private readonly object gate = new object();
        private readonly DisplayStore store;
        private readonly IDisplayBackend backend;
        private readonly Action<string, bool> launch;
        private readonly Func<long> utc;
        private DisplaySnapshot snapshot;
        private string snapshotId;
        private long snapshotAt;
        private DisplayTrialResult resolutionResult;
        private bool disposed;
        public bool Supported { get; }
        internal DisplayControlService(string directory, bool supported, IDisplayBackend backend = null,
            Action<string, bool> launch = null, Func<long> utc = null)
        {
            store = new DisplayStore(directory); Supported = supported;
            this.backend = backend ?? new NativeDisplayBackend(); this.utc = utc ?? (() => DateTime.UtcNow.Ticks);
            this.launch = launch ?? Launch;
            // Construction/startup never enumerates or applies a new display mode.
        }
        public DisplayStatus Inspect()
        {
            lock (gate)
            {
                RequireSupported();
                snapshot = backend.Query(); snapshotAt = utc(); snapshotId = Guid.NewGuid().ToString("N");
                return Status();
            }
        }
        public DisplayStatus ApplyResolution(DisplayRequest request)
        {
            lock (gate)
            {
                RequireSupported();
                if (request == null || request.Mode != null || request.TrialToken != null
                    || !DisplayResolution.Options(null).Any(x => x.Height == request.ResolutionHeight))
                    throw new ArgumentException("Selecciona 800p, 1000p, 1200p o 1600p, sin cambiar la frecuencia.");
                if (store.Plan() != null) throw new InvalidOperationException("Resuelve el cambio de pantalla anterior antes de seleccionar otra resolución.");
                if (snapshot == null || request.SnapshotId == null || request.SnapshotId != snapshotId)
                    throw new ArgumentException("Actualiza la consulta de pantalla antes de seleccionar una resolución.");
                var original = snapshot;
                snapshotId = null;
                try
                {
                    // The last query identifies the intended screen; re-read its modes and actual Hz on every selection.
                    var fresh = backend.Query();
                    if (!original.SameDevice(fresh) || !fresh.Info.CanChange)
                        throw new InvalidOperationException(fresh.Info.Restriction ?? "Cambió la pantalla desde la consulta; vuelve a consultarla.");
                    var resolution = DisplayResolution.Options(fresh.Info).Single(x => x.Height == request.ResolutionHeight);
                    if (!resolution.IsAvailable)
                        throw new InvalidOperationException("Windows no ofrece esa resolución a los " + fresh.Info.Current.Frequency + " Hz actuales.");
                    var target = resolution.Mode;
                    if (!target.Same(fresh.Info.Current))
                    {
                        backend.Test(fresh, target);
                        var tested = backend.Query();
                        if (!fresh.SameOriginal(tested) || !tested.Info.CanChange
                            || !(tested.Info.Modes ?? new DisplayMode[0]).Any(x => target.Same(x)))
                            throw new InvalidOperationException("Cambió la pantalla antes de aplicar; vuelve a consultarla.");
                        backend.Apply(fresh, target);
                    }
                    snapshot = backend.Query();
                    if (!fresh.SameDevice(snapshot) || !target.Same(snapshot.Info.Current))
                        throw new InvalidOperationException("La lectura de Windows no coincide con la resolución solicitada.");
                    resolutionResult = new DisplayTrialResult { Phase = "Applied", Message = "Última selección verificada: " + snapshot.Info.Current + "." };
                }
                catch (Exception ex)
                {
                    resolutionResult = new DisplayTrialResult { Phase = "Failed", Error = ex.Message,
                        Message = "No se pudo verificar el cambio. No se ha solicitado una reversión automática." };
                    // Report the real state after any failure, including partial driver writes; never restore here.
                    snapshot = null;
                    try { snapshot = backend.Query(); }
                    catch (Exception readError) { resolutionResult.Error += " · No se pudo leer la pantalla: " + readError.Message; }
                }
                if (snapshot != null) { snapshotAt = utc(); snapshotId = Guid.NewGuid().ToString("N"); }
                return Status();
            }
        }
        public DisplayStatus Preview(DisplayRequest request)
        {
            lock (gate)
            {
                RequireSupported();
                if (store.Plan() != null) throw new InvalidOperationException("Hay una prueba o recuperación de pantalla pendiente.");
                if (request?.Mode == null || request.TrialToken != null || request.ResolutionHeight != 0 || request.SnapshotId == null || request.SnapshotId != snapshotId
                    || snapshot == null || utc() - snapshotAt < 0 || utc() - snapshotAt > TimeSpan.FromSeconds(30).Ticks)
                    throw new ArgumentException("Consulta los modos de pantalla de nuevo antes de probar.");
                if (!snapshot.Info.CanChange || !(snapshot.Info.Modes ?? new DisplayMode[0]).Any(x => x.Same(request.Mode)))
                    throw new ArgumentException("Selecciona un modo compatible de la última consulta.");
                if (snapshot.Info.Current.Same(request.Mode)) throw new ArgumentException("Ese modo ya está activo.");
                snapshotId = null;
                var fresh = backend.Query();
                if (!snapshot.SameOriginal(fresh) || !fresh.Info.CanChange) throw new InvalidOperationException("Cambió la pantalla desde la consulta; vuelve a consultarla.");
                DisplayPlan plan;
                using (var owner = Process.GetCurrentProcess())
                    plan = new DisplayPlan { Token = Guid.NewGuid().ToString("N"), OwnerPid = owner.Id, OwnerStarted = owner.StartTime.ToUniversalTime().Ticks,
                        CreatedUtc = utc(), Original = fresh, Target = request.Mode.Copy() };
                store.Arm(plan); // Durable original precedes process creation and every native write.
                resolutionResult = null;
                try { launch(plan.Token, false); }
                catch (Exception ex) { store.Save(new DisplayTrialResult { Token = plan.Token, Pending = true, Phase = "RecoveryPending", Error = ex.Message,
                    Message = "No se pudo iniciar el supervisor; se conserva el respaldo." }); }
                return Status();
            }
        }
        public DisplayStatus Decide(string token, bool confirm)
        {
            lock (gate)
            {
                RequireSupported();
                var plan = store.Plan(); var result = store.Result();
                if (plan == null)
                {
                    if (result?.Token == token) return Status();
                    throw new InvalidOperationException("La prueba de pantalla ya no existe.");
                }
                if (plan.Token != token) throw new InvalidOperationException("La decisión pertenece a otra prueba.");
                if (confirm && (result?.Token != token || result.Phase != "AwaitingConfirmation" || utc() >= result.DeadlineUtc
                    || !DisplayStore.Alive(result.WorkerPid, result.WorkerStarted)))
                    throw new InvalidOperationException("La confirmación venció o el supervisor no está disponible. Se intentará recuperar el modo anterior.");
                store.Decide(token, confirm ? "confirm" : "revert");
                return Status(); // Only the worker acknowledges a confirmed mode.
            }
        }
        public DisplayStatus Recover()
        {
            lock (gate)
            {
                RequireSupported();
                var plan = store.Plan(); var result = store.Result();
                if (plan != null && !(result?.Token == plan.Token && DisplayStore.Alive(result.WorkerPid, result.WorkerStarted)))
                    launch(plan.Token, true);
                return Status();
            }
        }
        public void CancelPending()
        {
            lock (gate)
            {
                var plan = store.Plan();
                if (plan != null && store.Decision(plan.Token) == null) store.Decide(plan.Token, "revert");
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; try { CancelPending(); } finally { disposed = true; } }
        }
        private void RequireSupported()
        {
            if (!Supported || disposed) throw new InvalidOperationException("Actualiza o inicia el helper compatible para gestionar la pantalla.");
        }
        private DisplayStatus Status()
        {
            var plan = store.Plan(); var result = plan == null && resolutionResult != null ? resolutionResult : store.Result();
            bool pending = plan != null;
            bool matching = result != null && (!pending || result.Token == plan.Token);
            long deadline = matching ? result.DeadlineUtc : 0;
            string phase = matching ? result.Phase : pending ? "Preparing" : "Idle";
            bool orphan = pending && utc() - plan.CreatedUtc > TimeSpan.FromSeconds(5).Ticks
                && (!matching || !DisplayStore.Alive(result.WorkerPid, result.WorkerStarted));
            return new DisplayStatus { Supported = Supported, Info = snapshot == null ? null : Protocol.Decode<DisplayInfo>(Protocol.Encode(snapshot.Info)),
                SnapshotId = pending ? null : snapshotId, TrialToken = pending ? plan.Token : result?.Token, Phase = phase,
                Pending = pending, RecoveryPending = pending && (phase == "RecoveryPending" || orphan),
                RemainingSeconds = pending && deadline > 0 ? (int)Math.Max(0, Math.Min(15, Math.Ceiling((deadline - utc()) / (double)TimeSpan.TicksPerSecond))) : 0,
                DeadlineUtc = deadline > 0 ? new DateTime(deadline, DateTimeKind.Utc).ToString("o") : null,
                Message = matching ? result.Message : pending ? "Preparando la prueba supervisada." : "Consulta de pantalla de solo lectura.", Error = matching ? result.Error : null };
        }
        private static void Launch(string token, bool recover)
        {
            using (var process = Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LegionTweaks.Helper.exe"),
                (recover ? "--display-recover " : "--display-trial ") + token) { UseShellExecute = false, CreateNoWindow = true }))
                if (process == null) throw new InvalidOperationException("No se inició el supervisor de pantalla.");
        }
        internal static int Worker(string directory, string token, bool recover)
        {
            Guid parsed;
            if (!Guid.TryParseExact(token, "N", out parsed)) throw new ArgumentException("Prueba de pantalla inválida.");
            string sid = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(false, "Local\\LegionTweaks.Display." + sid))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 2;
                try
                {
                    var store = new DisplayStore(directory); var plan = store.Plan();
                    if (plan == null || plan.Token != token) return 2;
                    var port = new WorkerPort(store, plan);
                    store.Save(port.Stamp(new DisplayTrialResult { Token = token, Pending = true, Phase = recover ? "Reverting" : "Preparing",
                        Message = recover ? "Comprobando recuperación pendiente." : "Supervisor de pantalla iniciado." }));
                    var result = DisplayTrialRules.Run(plan, new NativeDisplayBackend(), port, recover);
                    if (!result.Pending) store.Retire(token);
                    return result.Pending ? 1 : 0;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private sealed class WorkerPort : IDisplayTrialPort
        {
            private readonly DisplayStore store;
            private readonly DisplayPlan plan;
            private readonly Stopwatch clock = Stopwatch.StartNew();
            private readonly int pid;
            private readonly long started;
            internal WorkerPort(DisplayStore store, DisplayPlan plan)
            {
                this.store = store; this.plan = plan;
                using (var process = Process.GetCurrentProcess()) { pid = process.Id; started = process.StartTime.ToUniversalTime().Ticks; }
            }
            public long ElapsedMilliseconds => clock.ElapsedMilliseconds;
            public long UtcTicks => DateTime.UtcNow.Ticks;
            public bool OwnerAlive => DisplayStore.Alive(plan.OwnerPid, plan.OwnerStarted);
            public bool Interactive => NativeDisplayBackend.Interactive();
            public string Decision => store.Decision(plan.Token);
            public void Wait() => Thread.Sleep(100);
            public void Save(DisplayTrialResult result) => store.Save(Stamp(result));
            internal DisplayTrialResult Stamp(DisplayTrialResult result) { result.WorkerPid = pid; result.WorkerStarted = started; return result; }
        }
    }
}
