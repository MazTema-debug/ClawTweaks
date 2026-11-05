// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    [DataContract]
    internal sealed class FpsY3LeaseRecord
    {
        [DataMember] internal string Sid, Token, Phase, SourcePath, Error;
        [DataMember] internal int OwnerPid, WatchPid;
        [DataMember] internal long OwnerStarted, WatchStarted, CreatedUtc, ExpiresUtc;
        [DataMember] internal FpsY3NativeOriginal Original;
        internal void Validate(string sid)
        {
            Guid token;
            if (Sid != sid || !Guid.TryParseExact(Token, "N", out token) || token == Guid.Empty || OwnerPid <= 0 || OwnerStarted <= 0
                || CreatedUtc <= 0 || CreatedUtc > DateTime.MaxValue.Ticks - TimeSpan.FromMilliseconds(FpsY3TrialRules.DurationMilliseconds).Ticks
                || ExpiresUtc != CreatedUtc + TimeSpan.FromMilliseconds(FpsY3TrialRules.DurationMilliseconds).Ticks
                || WatchPid < 0 || WatchStarted < 0 || (WatchPid == 0) != (WatchStarted == 0)
                || SourcePath == null || SourcePath.Length > 1024 || !SourcePath.StartsWith(@"\\?\hid#vid_17ef&pid_61ee&mi_02#", StringComparison.OrdinalIgnoreCase)
                || Array.IndexOf(new[] { "Preparing", "Original", "Applying", "Muted", "Restoring", "Restored" }, Phase) < 0)
                throw new InvalidDataException("Diario de Y3 inválido; se conserva para recuperación.");
            if (Original != null) { Original.Validate(); if (!string.Equals(SourcePath, Original.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Cambió el original de Y3."); }
            else if (Phase != "Preparing" && Phase != "Restoring" && Phase != "Restored") throw new InvalidDataException("Falta el respaldo de Y3.");
        }
        internal void RequireSuccessor(FpsY3LeaseRecord next)
        {
            next.Validate(Sid);
            bool sameIdentity = Sid == next.Sid && Token == next.Token && SourcePath == next.SourcePath
                && OwnerPid == next.OwnerPid && OwnerStarted == next.OwnerStarted
                && CreatedUtc == next.CreatedUtc && ExpiresUtc == next.ExpiresUtc;
            bool sameOriginal = Original == null || Protocol.Encode(Original) == Protocol.Encode(next.Original);
            bool phaseAllowed = Phase == next.Phase
                || Phase == "Preparing" && next.Phase == "Original"
                || Phase == "Original" && next.Phase == "Applying"
                || Phase == "Applying" && next.Phase == "Muted"
                || Phase != "Restored" && next.Phase == "Restoring"
                || Phase == "Restoring" && next.Phase == "Restored";
            bool captureAllowed = Original != null || next.Original == null || Phase == "Preparing" && next.Phase == "Original";
            if (!sameIdentity || !sameOriginal || !phaseAllowed || !captureAllowed)
                throw new InvalidOperationException("La identidad y el original Y3 son inmutables; no se puede retroceder su recuperación.");
        }
    }

    // Parent owns no HID handle. All potentially blocking native operations run
    // in bounded children; the independent watcher can recover without killing
    // the ordinary helper or interrupting its unrelated work.
    internal sealed class FpsY3Lease : IFpsY3Lease
    {
        private readonly Store store;
        internal FpsY3Lease(string directory) { store = new Store(directory); }
        public bool Pending { get { var r = store.Load(); return r != null && r.Phase != "Restored"; } }
        public bool Muted {
            get { var r = store.Load(); long now = DateTime.UtcNow.Ticks; return r != null && r.Phase == "Muted"
                && now >= r.CreatedUtc && now < r.ExpiresUtc && Alive(r.WatchPid, r.WatchStarted) && !store.Stopped(r.Token); }
        }
        public string DevicePath => store.Load()?.Original?.Path;
        public void Begin(string devicePath)
        {
            RequireNoOtherWriters();
            store.RequirePhysicalReturned();
            var previous = store.Load();
            if (previous != null && previous.Phase != "Restored") throw new InvalidOperationException("Queda una devolución de Y3 pendiente.");
            using (var owner = Process.GetCurrentProcess()) {
                long created = DateTime.UtcNow.Ticks;
                var r = new FpsY3LeaseRecord { Sid = store.Sid, Token = Guid.NewGuid().ToString("N"), Phase = "Preparing",
                    SourcePath = devicePath, OwnerPid = owner.Id, OwnerStarted = owner.StartTime.ToUniversalTime().Ticks,
                    CreatedUtc = created, ExpiresUtc = created + TimeSpan.FromMilliseconds(FpsY3TrialRules.DurationMilliseconds).Ticks };
                store.Create(r);
                RunIo(store, "capture", r.Token);
                StartWatch(store, r.Token); Wait(store, r.Token, value => Alive(value.WatchPid, value.WatchStarted), 4000);
                RunIo(store, "mute", r.Token);
                if (!Muted) throw new InvalidOperationException("No se confirmó la sesión neutralizada de Y3.");
            }
        }
        public void Verify()
        {
            if (!Muted) throw new InvalidOperationException("La neutralización de Y3 dejó de estar disponible.");
            var r = store.Load(); RunIo(store, "verify", r.Token);
            if (!Muted) throw new InvalidOperationException("La sesión Y3 terminó durante su comprobación.");
        }
        public void Restore()
        {
            var r = store.Load(); if (r == null || r.Phase == "Restored") return;
            store.Stop(r.Token);
            if (!Alive(r.WatchPid, r.WatchStarted)) StartWatch(store, r.Token);
            Wait(store, r.Token, value => value.Phase == "Restored", 18000);
        }
        internal static void RecoverPending(string directory)
        { var lease = new FpsY3Lease(directory); if (lease.Pending) lease.Restore(); }

        private static void Wait(Store store, string token, Func<FpsY3LeaseRecord, bool> done, int timeout)
        {
            var timer = Stopwatch.StartNew();
            while (true) {
                var r = store.Require(token); if (done(r)) return;
                if (timer.ElapsedMilliseconds >= timeout) throw new TimeoutException("Devolución de Y3 pendiente. Mantén FPS ON y vuelve a pulsar Devolver Y3.");
                Thread.Sleep(50);
            }
        }
        private static ProcessStartInfo Child(string mode, string operation, string token)
        {
            Guid parsed;
            if (!Guid.TryParseExact(token, "N", out parsed) || parsed == Guid.Empty) throw new ArgumentException("Token Y3 inválido.");
            return new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                mode + " " + operation + " " + token) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        }
        private static void StartWatch(Store store, string token)
        {
            var info = Child("--fps-y3-watch", "recover", token);
            // The watcher outlives its owner; do not tie its stdio to a pipe
            // that disappears when the owner exits or disposes this handle.
            info.RedirectStandardOutput = info.RedirectStandardError = false;
            using (var child = Process.Start(info)) { }
        }
        private static void RunIo(Store store, string operation, string token)
        {
            using (var child = Process.Start(Child("--fps-y3-io", operation, token))) {
                var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
                try {
                    if (!child.WaitForExit(8500)) throw new TimeoutException("La operación nativa Y3 excedió su plazo.");
                    if (child.ExitCode != 0) throw new InvalidOperationException(store.Require(token).Error ?? "No se confirmó la operación nativa Y3.");
                }
                finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(1000); } }
                // Observing both tasks prevents an abandoned redirected pipe.
                if (stdout.IsCompleted) { var ignored = stdout.Result; }
                if (stderr.IsCompleted) { var ignored = stderr.Result; }
            }
        }
        private static bool Alive(int pid, long started)
        {
            if (pid <= 0 || started <= 0) return false;
            try { using (var p = Process.GetProcessById(pid)) return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == started
                && string.Equals(p.MainModule.FileName, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        private static void RequireNoOtherWriters()
        {
            foreach (string name in new[] { "LegionSpace", "LSDaemon", "HandheldCompanion" })
                foreach (var p in Process.GetProcessesByName(name))
                    using (p) throw new InvalidOperationException("Cierra " + name + " antes de usar la prueba nativa Y3.");
        }
        internal static int Watch(string directory, string operation, string token)
        {
            if (operation != "recover") throw new ArgumentException("Supervisor Y3 inválido.");
            var store = new Store(directory);
            using (var gate = new Mutex(false, @"Global\LegionTweaks.FpsY3.Watch." + store.Sid)) {
                bool owns = false;
                try {
                    try { owns = gate.WaitOne(0); } catch (AbandonedMutexException) { owns = true; }
                    if (!owns) return 0;
                    var r = store.Require(token);
                    using (var current = Process.GetCurrentProcess()) store.Update(token, value => {
                        value.WatchPid = current.Id; value.WatchStarted = current.StartTime.ToUniversalTime().Ticks;
                    });
                    var monotonic = Stopwatch.StartNew();
                    while (true) {
                        r = store.Require(token); long now = DateTime.UtcNow.Ticks;
                        if (r.Phase == "Restored") return 0;
                        if (store.Stopped(token) || !Alive(r.OwnerPid, r.OwnerStarted) || now < r.CreatedUtc || now >= r.ExpiresUtc
                            || monotonic.ElapsedMilliseconds >= FpsY3TrialRules.DurationMilliseconds) break;
                        Thread.Sleep(100);
                    }
                    var recovery = Stopwatch.StartNew();
                    do {
                        try { RunIo(store, "restore", token); if (store.Require(token).Phase == "Restored") return 0; }
                        catch (Exception ex) { store.Update(token, value => value.Error = ex.Message); }
                        Thread.Sleep(500);
                    } while (recovery.ElapsedMilliseconds < 30000);
                    return 1;
                }
                finally { if (owns) gate.ReleaseMutex(); }
            }
        }
        internal static int Io(string directory, string operation, string token)
        {
            if (Array.IndexOf(new[] { "capture", "mute", "verify", "restore" }, operation) < 0) throw new ArgumentException("Operación Y3 inválida.");
            using (var timeout = new Timer(_ => Environment.Exit(124), null, 7500, Timeout.Infinite)) {
                var store = new Store(directory);
                using (var gate = new Mutex(false, @"Global\LegionTweaks.FpsY3.IO." + store.Sid)) {
                    bool owns = false;
                    try {
                        try { owns = gate.WaitOne(1000); } catch (AbandonedMutexException) { owns = true; }
                        if (!owns) throw new TimeoutException("Hay otra operación nativa Y3 activa.");
                        var r = store.Require(token);
                        Action authorize = () => {
                            RequireNoOtherWriters();
                            var fresh = store.Require(token); long now = DateTime.UtcNow.Ticks;
                            if (store.Stopped(token) || now < fresh.CreatedUtc || now >= fresh.ExpiresUtc
                                || !Alive(fresh.OwnerPid, fresh.OwnerStarted) || !Alive(fresh.WatchPid, fresh.WatchStarted))
                                throw new InvalidOperationException("La sesión Y3 terminó antes de escribir.");
                        };
                        if (operation == "capture") {
                            if (r.Phase != "Preparing" || r.Original != null || !Alive(r.OwnerPid, r.OwnerStarted)) throw new InvalidOperationException("Captura Y3 fuera de fase.");
                            var original = FpsY3NativeOriginal.Capture(r.SourcePath);
                            store.Update(token, value => { value.Original = original; value.Phase = "Original"; value.Error = null; }); return 0;
                        }
                        if (operation == "restore") {
                            if (r.Phase == "Restored") return 0;
                            store.Update(token, value => value.Phase = "Restoring");
                            r.Original?.Execute("restore", () => { RequireNoOtherWriters(); store.Require(token); });
                            store.Update(token, value => { value.Phase = "Restored"; value.Error = null; }); return 0;
                        }
                        authorize();
                        if (r.Original == null || operation == "mute" && r.Phase != "Original" || operation == "verify" && r.Phase != "Muted")
                            throw new InvalidOperationException("Operación Y3 fuera de fase.");
                        if (operation == "mute") store.Update(token, value => value.Phase = "Applying");
                        r.Original.Execute(operation, authorize);
                        if (operation == "mute") store.Update(token, value => { value.Phase = "Muted"; value.Error = null; });
                        return 0;
                    }
                    catch (Exception ex) {
                        try { store.Update(token, value => value.Error = ex.Message); } catch { }
                        return 1;
                    }
                    finally { if (owns) gate.ReleaseMutex(); }
                }
            }
        }

        private sealed class Store
        {
            private const string Leaf = "fps-y3-trial.json";
            private readonly string directory, path;
            private readonly RemapProfileStorage protection;
            internal readonly string Sid = WindowsIdentity.GetCurrent().User.Value;
            internal Store(string directory) { this.directory = directory; path = Path.Combine(directory, Leaf); protection = new RemapProfileStorage(directory); }
            private FpsY3LeaseRecord Read()
            {
                protection.RequireProtectedLeaf(Leaf);
                try {
                    using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    using (var reader = new StreamReader(f, Encoding.UTF8)) {
                        if (f.Length <= 0 || f.Length > 16384) throw new InvalidDataException("Diario Y3 fuera de tamaño.");
                        var r = Protocol.Decode<FpsY3LeaseRecord>(reader.ReadToEnd()); if (r == null) throw new InvalidDataException("Diario Y3 vacío."); r.Validate(Sid); return r;
                    }
                }
                catch (FileNotFoundException) { return null; }
            }
            internal FpsY3LeaseRecord Load() { FpsY3LeaseRecord value = null; Locked(() => value = Read()); return value; }
            internal FpsY3LeaseRecord Require(string token) {
                var r = Load(); if (r == null || !string.Equals(r.Token, token, StringComparison.Ordinal)) throw new InvalidOperationException("Cambió la sesión Y3."); return r;
            }
            internal void Create(FpsY3LeaseRecord r) => Locked(() => {
                r.Validate(Sid); var prior = Read(); if (prior != null && prior.Phase != "Restored") throw new InvalidOperationException("Hay un original Y3 pendiente."); Write(r);
            });
            internal void Update(string token, Action<FpsY3LeaseRecord> change) => Locked(() => {
                var r = Read(); if (r == null || r.Token != token) throw new InvalidOperationException("Cambió el diario Y3.");
                var before = Protocol.Decode<FpsY3LeaseRecord>(Protocol.Encode(r));
                change(r); before.RequireSuccessor(r);
                Write(r);
            });
            internal void RequirePhysicalReturned()
            {
                foreach (string leaf in new[] { "controller-lease.json", "controller-device-recovery.json", "controller-mode-recovery.json" }) {
                    protection.RequireProtectedLeaf(leaf);
                    try { File.GetAttributes(Path.Combine(directory, leaf)); }
                    catch (FileNotFoundException) { continue; }
                    throw new InvalidOperationException("Devuelve el físico y resuelve su recuperación pendiente antes de probar Y3.");
                }
            }
            private void Write(FpsY3LeaseRecord r)
            {
                string temp = Leaf + "." + Guid.NewGuid().ToString("N") + ".tmp";
                protection.RequireProtectedLeaf(temp);
                var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(r));
                using (var f = new FileStream(Path.Combine(directory, temp), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { f.Write(bytes, 0, bytes.Length); f.Flush(true); }
                protection.RequireProtectedLeaf(temp); protection.RequireProtectedLeaf(Leaf);
                if (File.Exists(path)) File.Replace(Path.Combine(directory, temp), path, null); else File.Move(Path.Combine(directory, temp), path);
            }
            private string StopLeaf(string token) {
                Guid parsed; if (!Guid.TryParseExact(token, "N", out parsed)) throw new InvalidDataException("Token Y3 inválido."); return "fps-y3-stop-" + token + ".signal";
            }
            internal bool Stopped(string token) {
                string leaf = StopLeaf(token); protection.RequireProtectedLeaf(leaf);
                try { File.GetAttributes(Path.Combine(directory, leaf)); return true; } catch (FileNotFoundException) { return false; }
            }
            internal void Stop(string token) {
                string leaf = StopLeaf(token); protection.RequireProtectedLeaf(leaf);
                using (var f = new FileStream(Path.Combine(directory, leaf), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)) f.Flush(true);
            }
            private void Locked(Action action)
            {
                using (var gate = new Mutex(false, @"Global\LegionTweaks.FpsY3.Journal." + Sid)) {
                    bool owns = false; try { try { owns = gate.WaitOne(2000); } catch (AbandonedMutexException) { owns = true; }
                        if (!owns) throw new TimeoutException("El diario Y3 está ocupado."); action();
                    } finally { if (owns) gate.ReleaseMutex(); }
                }
            }
        }
    }
}
