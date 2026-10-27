// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class LegionShortcutService : ILegionShortcuts, ILegionControllerButtons, ILegionGamepadInput, ILegionButtonCapture, IFpsButtonKeys, IDisposable
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly ManualResetEvent readerClosed = new ManualResetEvent(true);
        private readonly Thread worker;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Func<long> milliseconds;
        private readonly Func<Action<string>, ILegionButtonReader> openReader;
        private readonly Action toggleGameBar;
        private readonly LegionShortcutStatus status = new LegionShortcutStatus { SupportsButtonMapping = true,
            SupportedButtons = LegionButtonReport.SupportedControllerButtons };
        private readonly LegionButtonInputGate controllerGate = new LegionButtonInputGate();
        private readonly LegionGamepadCache gamepadCache = new LegionGamepadCache();
        private readonly LegionShortcutGate shortcutGate = new LegionShortcutGate();
        private readonly FpsButtonKeys fpsKeys = new FpsButtonKeys();
        private readonly IFpsKeyOutput fpsOutput;
        private readonly FpsButtonKeysStatus fpsStatus = new FpsButtonKeysStatus { Message = "Botones FPS desactivados." };
        private LegionButtonCaptureSession capture;
        private int revision;
        private bool suspended, preparationPaused, controllerInputEnabled, gamepadInputEnabled, disposed;
        internal LegionShortcutService(string directory) : this(directory, null, null, null) { }
        // Injection keeps lifecycle/concurrency tests independent of real HID or SendInput.
        internal LegionShortcutService(string directory, Func<long> milliseconds,
            Func<Action<string>, ILegionButtonReader> openReader, Action toggleGameBar, IFpsKeyOutput fpsOutput = null)
        {
            this.milliseconds = milliseconds ?? (() => clock.ElapsedMilliseconds);
            this.openReader = openReader ?? (diagnostic => new LegionButtonReader(diagnostic));
            this.toggleGameBar = toggleGameBar ?? GameBarShortcut.Toggle;
            this.fpsOutput = fpsOutput ?? new FpsKeyOutput();
            path = Path.Combine(directory, "legion-shortcut.json");
            status.Message = "Acceso por botones Legion desactivado.";
            try
            {
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 64) throw new InvalidDataException();
                    int mode = Protocol.Decode<int>(File.ReadAllText(path));
                    Validate(mode); status.Mode = (LegionShortcutMode)mode;
                }
            }
            catch { status.Error = "No se pudo leer el atajo guardado. Elige una opción y guárdala de nuevo."; }
            worker = new Thread(Run) { IsBackground = true, Name = "LegionTweaks.LegionButtons" }; worker.Start();
        }
        public LegionShortcutStatus GetStatus()
        {
            lock (gate)
            {
                long now = milliseconds(); UpdateCapture(now);
                status.Capture = capture?.GetStatus(now);
                status.ReservedButtons = LegionButtonInputGate.ReservedButtons(status.Mode);
                status.ControllerInputEnabled = controllerInputEnabled;
                status.PreparationPaused = preparationPaused;
                status.ControllerButtons = ReadControllerButtons(now);
                fpsStatus.Enabled = fpsKeys.Enabled; fpsStatus.WaitingForRelease = fpsKeys.WaitingForRelease;
                status.FpsKeys = fpsStatus;
                return Protocol.Decode<LegionShortcutStatus>(Protocol.Encode(status));
            }
        }
        private static void Validate(int mode) { if (mode < 0 || mode > 3) throw new ArgumentException("Atajo Legion inválido."); }
        public void SetFpsKeys(bool enabled)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (enabled && (suspended || preparationPaused || controllerInputEnabled || gamepadInputEnabled || capture?.Active == true))
                    throw new InvalidOperationException("Termina la captura, preparación o sesión virtual antes de activar los botones FPS.");
                if (!enabled) StopFpsLocked("Botones FPS desactivados.");
                else
                {
                    if (!fpsOutput.ReleaseOwned()) throw new InvalidOperationException("Queda una tecla FPS pendiente de liberar.");
                    fpsKeys.Configure(true); fpsStatus.Error = null;
                    fpsStatus.Message = "Buscando FPS. Suelta Y3 y M3 para empezar (Insert / Escape).";
                }
                revision++; changed.Set();
            }
        }
        private void StopFpsLocked(string message, string error = null)
        {
            fpsKeys.Configure(false); fpsStatus.Enabled = fpsStatus.WaitingForRelease = false;
            fpsStatus.Message = message; fpsStatus.Error = error;
            try { if (!fpsOutput.ReleaseOwned()) fpsStatus.Error = (error ?? "") + " Queda una tecla FPS pendiente de liberar."; }
            catch (Exception ex) { fpsStatus.Error = (error ?? "") + " " + ex.Message; }
        }
        private void ObserveFpsLocked(ushort product, byte[] report, long now)
        {
            if (!fpsKeys.Enabled) return;
            int buttons;
            if (!FpsButtonKeys.TryDecode(product, report, out buttons))
            { StopFpsLocked("Botones FPS desactivados al perder su lectura. Actívalos de nuevo con FPS ON."); return; }
            try
            {
                var desktop = fpsOutput.Inspect();
                // Discard a source observation that became old during native inspection.
                long inspected = milliseconds();
                bool fresh = inspected >= now && inspected - now <= FpsButtonKeys.FreshMilliseconds;
                int actions = fpsKeys.Observe(product, report, inspected, desktop.Available && fresh, desktop.Foreground);
                foreach (int action in new[] { FpsButtonKeys.InsertAction, FpsButtonKeys.EscapeAction })
                {
                    if ((actions & action) == 0) continue;
                    if (!fpsOutput.Pulse(action == FpsButtonKeys.InsertAction ? 0x2d : 0x1b, desktop))
                    { fpsKeys.Reset(); break; }
                    if (action == FpsButtonKeys.InsertAction) fpsStatus.InsertSent++; else fpsStatus.EscapeSent++;
                }
                fpsStatus.Message = fpsKeys.WaitingForRelease
                    ? "Suelta Y3/M3 y los modificadores para rearmar los botones FPS."
                    : "Botones FPS activos: Y3 → Insert · M3 → Escape (una pulsación por presión).";
            }
            catch (Exception ex) { StopFpsLocked("Botones FPS desactivados por un error de teclado.", ex.Message); }
        }
        public void Configure(LegionShortcutMode mode)
        {
            Validate((int)mode);
            lock (gate)
            {
                ThrowIfDisposed();
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, Protocol.Encode((int)mode));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                CancelCaptureLocked(); status.Mode = mode; status.Error = null; revision++;
                ClearInput(); status.Diagnostic = null; changed.Set();
            }
        }
        public void SetControllerInputEnabled(bool enabled)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (enabled && controllerInputEnabled) return;
                if (enabled && fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados al usar una sesión virtual.");
                bool wasReading = ShouldRead();
                if (!enabled) CancelCaptureLocked();
                controllerInputEnabled = enabled; controllerGate.Reset();
                bool willRead = ShouldRead();
                if (wasReading && !willRead) { revision++; changed.Set(); }
                else if (!wasReading && willRead) changed.Set();
            }
        }
        public void ResetControllerInput() { lock (gate) controllerGate.Reset(); }
        public void SetGamepadInputEnabled(bool enabled)
        {
            lock (gate) {
                ThrowIfDisposed();
                if (enabled == gamepadInputEnabled) return;
                if (enabled && fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados al usar una sesión virtual.");
                bool before = ShouldRead();
                gamepadInputEnabled = enabled; gamepadCache.Reset();
                bool after = ShouldRead();
                if (before && !after) { revision++; changed.Set(); }
                else if (!before && after) changed.Set();
            }
        }
        public bool GamepadInputPaused
        { get { lock (gate) return suspended || preparationPaused || capture?.Active == true; } }
        public bool TryReadGamepad(out GamepadFrame frame)
        {
            lock (gate) {
                frame = new GamepadFrame();
                return !disposed && gamepadInputEnabled && !suspended && !preparationPaused && capture?.Active != true
                    && gamepadCache.TryRead(milliseconds(), out frame);
            }
        }
        public byte GetControllerButtons()
        {
            lock (gate)
            {
                if (disposed) return 0;
                long now = milliseconds(); UpdateCapture(now); return ReadControllerButtons(now);
            }
        }
        private byte ReadControllerButtons(long now) => controllerInputEnabled && !suspended && !preparationPaused && capture?.Active != true
            ? controllerGate.Read(now, status.Mode) : (byte)0;
        public void SetPreparationPaused(bool paused)
        {
            lock (gate)
            {
                ThrowIfDisposed(); preparationPaused = paused;
                if (paused && fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados durante la preparación.");
                if (paused) CancelCaptureLocked("Captura cancelada para preparar el jugador 1.");
                revision++; ClearInput(); changed.Set();
            }
            // Includes a pending enumeration/open, not just the blocking ReadFile.
            // No CM operation may start unless this ownership acknowledgement succeeds.
            if (paused && !readerClosed.WaitOne(2000))
                throw new TimeoutException("El lector de botones no confirmó su cierre. Se abortó la preparación del jugador 1.");
        }
        public void StartCapture(int buttonIndex, int seconds)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (preparationPaused || suspended) throw new InvalidOperationException("Termina la preparación o suspensión antes de identificar botones.");
                if (capture?.Active == true) throw new InvalidOperationException("Ya hay una captura de botones en curso.");
                bool wasReading = ShouldRead();
                if (fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados para identificar botones.");
                capture = new LegionButtonCaptureSession(buttonIndex, seconds, milliseconds());
                ClearInput(); status.Error = null;
                if (!wasReading) changed.Set();
            }
        }
        public void CancelCapture()
        {
            lock (gate)
            {
                ThrowIfDisposed(); CancelCaptureLocked();
                if (!ShouldRead()) { revision++; changed.Set(); }
            }
        }
        private void CancelCaptureLocked(string message = "Captura cancelada. Suelta los botones para rearmar las acciones.")
        {
            capture?.Cancel(message); controllerGate.Reset(); gamepadCache.Reset(); shortcutGate.Reset();
        }
        private void UpdateCapture(long now)
        {
            if (capture?.Tick(now) != true) return;
            controllerGate.Reset(); shortcutGate.Reset();
            if (!ShouldRead()) { revision++; changed.Set(); }
        }
        internal void Suspend(bool value)
        {
            lock (gate)
            {
                if (disposed) return;
                suspended = value;
                if (value && fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados durante la suspensión.");
                if (value) CancelCaptureLocked("Captura cancelada durante la suspensión.");
                revision++; ClearInput(); changed.Set();
            }
        }
        private bool ShouldRead() => !suspended && !preparationPaused
            && (status.Mode != LegionShortcutMode.Disabled || controllerInputEnabled || gamepadInputEnabled || capture?.Active == true || fpsKeys.Enabled);
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(LegionShortcutService)); }
        private void ClearInput()
        {
            status.Connected = status.Left = status.Right = false;
            status.ControllerButtons = 0; controllerGate.Reset(); gamepadCache.Reset(); shortcutGate.Reset();
            fpsKeys.Reset();
        }
        private void Run()
        {
            while (!stop.WaitOne(0))
            {
                int generation;
                bool needed;
                lock (gate)
                {
                    UpdateCapture(milliseconds()); generation = revision; needed = ShouldRead();
                    if (needed) { changed.Reset(); readerClosed.Reset(); }
                    else
                    {
                        ClearInput(); status.Message = preparationPaused ? "Botones en pausa durante la preparación del jugador 1."
                            : suspended ? "Botones en pausa durante la suspensión." : "Acceso por botones Legion desactivado.";
                    }
                }
                if (!needed) { WaitHandle.WaitAny(new WaitHandle[] { stop, changed }); continue; }
                try
                {
                    lock (gate) { status.Diagnostic = null; status.Message = "Buscando interfaz de botones Legion…"; }
                    using (var reader = openReader(value =>
                    {
                        lock (gate) if (generation == revision) status.Diagnostic = value;
                    }))
                    {
                        ushort product = (reader as IFpsButtonReader)?.Product ?? 0;
                        lock (gate) if (generation == revision && fpsKeys.Enabled && product != 0x61ee)
                            StopFpsLocked("Se necesita FPS ON en el mando original para activar estos botones.");
                        long lastValid = milliseconds();
                        lock (gate) { status.Message = "Esperando datos de los botones Legion…"; status.Error = null; }
                        while (!stop.WaitOne(0))
                        {
                            lock (gate) if (generation != revision || !ShouldRead()) break;
                            var report = reader.Read(stop, changed);
                            long now = milliseconds();
                            lock (gate)
                            {
                                UpdateCapture(now);
                                if (generation != revision || stop.WaitOne(0) || !ShouldRead()) break;
                                ObserveFpsLocked(product, report, now);
                                int buttons;
                                if (!LegionButtonReport.TryDecode(report, out buttons))
                                {
                                    if (now - lastValid > 3000)
                                        throw new InvalidOperationException("Sin informes válidos de botones Legion. No se ha cambiado el firmware.");
                                    continue;
                                }
                                lastValid = now;
                                if (gamepadInputEnabled && capture?.Active != true) gamepadCache.Observe(report, now);
                                status.Connected = true; status.Left = (buttons & 1) != 0; status.Right = (buttons & 2) != 0;
                                status.Reports++; status.Message = "Botones Legion conectados. Game Bar tiene prioridad sobre las asignaciones del perfil.";
                                if (capture?.Active == true)
                                {
                                    capture.Observe(report, now);
                                    if (!capture.Active) { controllerGate.Reset(); shortcutGate.Reset(); }
                                    continue;
                                }
                                int controllerButtons;
                                if (controllerInputEnabled && LegionButtonReport.TryDecodeControllerButtons(report, out controllerButtons))
                                    controllerGate.Observe(controllerButtons, now);
                                if (shortcutGate.Observe(status.Mode, buttons, now))
                                {
                                    try { toggleGameBar(); status.Activations++; status.LastActivationUtc = DateTime.UtcNow.ToString("O"); status.Error = null; }
                                    catch (Exception ex) { status.Error = ex.Message; }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    lock (gate) if (generation == revision)
                    {
                        if (fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados al perder la conexión.", ex.Message);
                        CancelCaptureLocked("Captura detenida al perder la lectura HID. No se cambió el firmware.");
                        status.Error = ex.Message; status.Message = "Esperando conexión de botones Legion.";
                    }
                }
                finally { lock (gate) { if (generation == revision && fpsKeys.Enabled) StopFpsLocked("Botones FPS desactivados al cerrar su lectura."); ClearInput(); } readerClosed.Set(); }
                // Immediate reconfiguration; back off only on a device/read failure.
                lock (gate) if (generation != revision) continue;
                WaitHandle.WaitAny(new WaitHandle[] { stop, changed }, 2000);
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                StopFpsLocked("Botones FPS desactivados al cerrar el helper.");
                disposed = true; controllerInputEnabled = false; CancelCaptureLocked(); ClearInput(); stop.Set(); changed.Set();
            }
            // A timed-out driver operation keeps its synchronization objects alive.
            if (worker.Join(5000)) { stop.Dispose(); changed.Dispose(); readerClosed.Dispose(); }
        }
    }
}
