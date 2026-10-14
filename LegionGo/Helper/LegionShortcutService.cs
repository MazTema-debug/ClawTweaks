// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class LegionShortcutService : ILegionShortcuts, IDisposable
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly LegionShortcutStatus status = new LegionShortcutStatus();
        private int revision;
        private bool suspended, disposed;
        internal LegionShortcutService(string directory)
        {
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
        public LegionShortcutStatus GetStatus() { lock (gate) return Protocol.Decode<LegionShortcutStatus>(Protocol.Encode(status)); }
        private static void Validate(int mode) { if (mode < 0 || mode > 3) throw new ArgumentException("Atajo Legion inválido."); }
        public void Configure(LegionShortcutMode mode)
        {
            Validate((int)mode);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(LegionShortcutService));
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, Protocol.Encode((int)mode));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                status.Mode = mode; status.Error = null; revision++;
                ClearInput(); changed.Set();
            }
        }
        internal void Suspend(bool value)
        {
            lock (gate) { if (disposed) return; suspended = value; revision++; ClearInput(); changed.Set(); }
        }
        private void ClearInput() { status.Connected = status.Left = status.Right = false; }
        private void Run()
        {
            var clock = Stopwatch.StartNew();
            while (!stop.WaitOne(0))
            {
                LegionShortcutMode mode; int generation; bool paused;
                lock (gate) { mode = status.Mode; generation = revision; paused = suspended; }
                if (mode == LegionShortcutMode.Disabled || paused)
                {
                    lock (gate) { ClearInput(); status.Message = paused ? "Atajo en pausa durante la suspensión." : "Acceso por botones Legion desactivado."; }
                    WaitHandle.WaitAny(new WaitHandle[] { stop, changed }); continue;
                }
                var trigger = new LegionShortcutGate();
                try
                {
                    using (var reader = LegionHidReader.Open())
                    {
                        long lastValid = clock.ElapsedMilliseconds;
                        lock (gate) { status.Message = "Esperando datos de los botones Legion…"; status.Error = null; }
                        while (!stop.WaitOne(0))
                        {
                            lock (gate) if (generation != revision) break;
                            var report = reader.Read(stop, changed);
                            int buttons;
                            if (!LegionButtonReport.TryDecode(report, out buttons))
                            {
                                if (clock.ElapsedMilliseconds - lastValid > 3000)
                                    throw new InvalidOperationException("Sin informes válidos de botones Legion. Se reintentará; no se ha cambiado el firmware.");
                                continue;
                            }
                            lastValid = clock.ElapsedMilliseconds;
                            lock (gate)
                            {
                                if (generation != revision || stop.WaitOne(0)) break;
                                status.Connected = true; status.Left = (buttons & 1) != 0; status.Right = (buttons & 2) != 0;
                                status.Reports++; status.Message = "Botones Legion conectados. Atajo disponible con Game Bar cerrada.";
                                if (trigger.Observe(mode, buttons, lastValid))
                                {
                                    try { GameBarShortcut.Toggle(); status.Activations++; status.LastActivationUtc = DateTime.UtcNow.ToString("O"); status.Error = null; }
                                    catch (Exception ex) { status.Error = ex.Message; }
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { lock (gate) if (generation == revision) { status.Error = ex.Message; status.Message = "Esperando conexión de botones Legion."; } }
                lock (gate) ClearInput();
                // Immediate reconfiguration; back off only on a device/read failure.
                lock (gate) if (generation != revision) continue;
                WaitHandle.WaitAny(new WaitHandle[] { stop, changed }, 2000);
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; stop.Set(); changed.Set(); }
            // The read loop cancels and completes its I/O before freeing its buffers.
            if (worker.Join(5000)) { stop.Dispose(); changed.Dispose(); }
        }
    }
}
