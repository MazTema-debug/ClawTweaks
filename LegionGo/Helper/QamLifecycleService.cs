// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Threading;

namespace LegionTweaks
{
    internal interface IQamRuntime
    {
        int SessionId { get; }
        QamInstanceResponse EnsureRunning();
        QamInstanceResponse Command(string operation, DesktopViewRegistration view, int clientPid);
        bool IsRunning(int pid);
        bool IsDesktopWindow(long window, int pid);
        void RegisterDesktop(DesktopViewRegistration view, int pid);
    }
    internal sealed class QamLifecycleService : IQamLifecycle
    {
        private readonly object stateGate = new object(), operationGate = new object();
        private readonly IQamPreferenceStore store;
        private readonly IQamRuntime runtime;
        private readonly IQamShortcutControl buttons;
        private readonly bool supported;
        private QamPreferences preferences = new QamPreferences { ToggleMode = LegionShortcutMode.Disabled };
        private bool known, suspended, disposed;
        private string error, message = "Menú rápido sin iniciar.";
        private QamInstanceResponse instance;
        private DesktopViewRegistration desktop;
        private int desktopPid, generation, pending, attempts;
        private Timer retry;
        internal QamLifecycleService(bool supported, IQamPreferenceStore store, IQamRuntime runtime, IQamShortcutControl buttons)
        {
            this.supported = supported; this.store = store; this.runtime = runtime; this.buttons = buttons;
            if (!supported) return;
            try { preferences = store.Load(); preferences.Validate(); known = true; }
            catch (Exception ex) { error = "Preferencia sin confirmar; se conserva el archivo. " + ex.Message; }
            buttons?.SetMenuActions(() => QueueAction("toggle"), () => QueueAction("desktop_toggle"));
            if (known && buttons != null) {
                try { buttons.ConfigureMenuShortcuts(preferences.ToggleMode, preferences.DesktopToggleMode, null); }
                catch (Exception ex) { error = ex.Message; } // Existing Game Bar assignment is never overwritten.
            }
        }
        public QamLifecycleStatus GetStatus()
        {
            QamLifecycleStatus result;
            DesktopViewRegistration registered; int registeredPid;
            lock (stateGate) result = new QamLifecycleStatus { Supported = supported, PreferenceKnown = known,
                StartWithLegionTweaks = known && preferences.StartWithLegionTweaks, ToggleMode = preferences.ToggleMode,
                DesktopToggleMode = preferences.DesktopToggleMode, SessionId = runtime.SessionId,
                Running = instance?.Running == true, Busy = Volatile.Read(ref pending) != 0, Visible = instance?.Visible == true, ProcessId = instance?.ProcessId ?? 0,
                Message = message, Error = error, DesktopRegistered = desktop != null };
            lock (stateGate) { registered = desktop; registeredPid = desktopPid; }
            result.EffectiveToggleMode = buttons?.QamShortcut ?? LegionShortcutMode.Disabled;
            result.EffectiveDesktopToggleMode = buttons?.DesktopShortcut ?? LegionShortcutMode.Disabled;
            if (result.PreferenceKnown && (result.ToggleMode != result.EffectiveToggleMode || result.DesktopToggleMode != result.EffectiveDesktopToggleMode))
                result.Error = (string.IsNullOrEmpty(result.Error) ? "" : result.Error + "\n")
                    + "Hay una asignación guardada sin activar. Elige gestos distintos de Game Bar y guarda la configuración de menús.";
            // No pipe, WMI or launch during the widget's ordinary status polling.
            if (result.Running && !runtime.IsRunning(result.ProcessId)) result.Running = result.Visible = false;
            result.DesktopRegistered = registered != null && runtime.IsDesktopWindow(registered.Window, registeredPid);
            return result;
        }
        public QamLifecycleStatus Configure(bool startup, LegionShortcutMode mode, LegionShortcutMode desktopMode)
        {
            RequireReady();
            if (buttons == null) throw new InvalidOperationException("El helper no ofrece el configurador de botones.");
            var next = new QamPreferences { StartWithLegionTweaks = startup, ToggleMode = mode, DesktopToggleMode = desktopMode }; next.Validate();
            lock (operationGate) {
                buttons.ConfigureMenuShortcuts(mode, desktopMode, () => store.Save(next));
                lock (stateGate) {
                    preferences = next; known = true; error = null;
                    message = "Configuración guardada. Los botones quedan listos; el inicio automático se aplica en el próximo arranque de LegionTweaks.";
                }
            }
            return GetStatus();
        }
        public void RegisterDesktop(DesktopViewRegistration view, int clientPid)
        {
            RequireReady();
            if (view?.Valid != true || clientPid <= 0 || !runtime.IsDesktopWindow(view.Window, clientPid))
                throw new InvalidOperationException("No se confirmó la ventana de escritorio de esta sesión.");
            runtime.RegisterDesktop(view, clientPid);
            lock (stateGate) { desktop = new DesktopViewRegistration { Window = view.Window, ViewId = view.ViewId }; desktopPid = clientPid; }
        }
        public QamLifecycleStatus Open()
        {
            RequireReady(); Execute("open", null); return GetStatus();
        }
        private void RequireReady()
        {
            lock (stateGate) {
                if (disposed) throw new ObjectDisposedException(nameof(QamLifecycleService));
                if (!supported || runtime.SessionId <= 0) throw new InvalidOperationException("El menú requiere el modelo compatible y la sesión interactiva del usuario.");
                if (suspended) throw new InvalidOperationException("Espera a que termine la reanudación del equipo.");
            }
        }
        private void Execute(string operation, int? expectedGeneration, long? observed = null)
        {
            lock (operationGate) {
                RequireReady();
                if (observed.HasValue && (Stopwatch.GetTimestamp() - observed.Value) * 1000.0 / Stopwatch.Frequency > 400) return;
                DesktopViewRegistration view; int pid;
                lock (stateGate) {
                    if (expectedGeneration.HasValue && generation != expectedGeneration.Value) return;
                    if (operation == "ensure" && (!known || !preferences.StartWithLegionTweaks)) return;
                    view = desktop; pid = desktopPid;
                }
                if (view != null && !runtime.IsDesktopWindow(view.Window, pid)) { view = null; pid = 0; }
                var result = operation == "ensure" ? runtime.EnsureRunning()
                    : runtime.Command(operation, operation == "desktop_toggle" ? view : null, operation == "desktop_toggle" ? pid : 0);
                lock (stateGate) {
                    instance = result; message = result.Message; error = result.Success ? null : result.Message;
                }
                if (!result.Success) throw new InvalidOperationException(result.Message);
            }
        }
        private void QueueAction(string operation)
        {
            int epoch;
            lock (stateGate) { if (disposed || suspended || !supported) return; epoch = generation; }
            if (Interlocked.Exchange(ref pending, 1) != 0) return;
            long observed = Stopwatch.GetTimestamp();
            ThreadPool.QueueUserWorkItem(_ => {
                try {
                    if ((Stopwatch.GetTimestamp() - observed) * 1000.0 / Stopwatch.Frequency <= 400) Execute(operation, epoch, observed);
                }
                catch (Exception ex) { lock (stateGate) { if (!disposed) { error = ex.Message; message = "No se confirmó la acción; no se repitió."; } } }
                finally { Volatile.Write(ref pending, 0); }
            });
        }
        public void RunAtStartup()
        {
            lock (stateGate) {
                if (disposed || !supported || !known || !preferences.StartWithLegionTweaks || runtime.SessionId <= 0) return;
                attempts = 0;
                retry?.Dispose(); retry = new Timer(_ => Warm(), null, 0, 5000);
            }
        }
        private void Warm()
        {
            int epoch;
            lock (stateGate) {
                if (disposed || suspended || !known || !preferences.StartWithLegionTweaks || attempts >= 12) { retry?.Dispose(); retry = null; return; }
                epoch = generation;
            }
            if (Interlocked.Exchange(ref pending, 1) != 0) return;
            try {
                lock (stateGate) attempts++;
                Execute("ensure", epoch);
                lock (stateGate) { if (generation == epoch) { retry?.Dispose(); retry = null; } }
            }
            catch (Exception ex) { lock (stateGate) { if (generation == epoch && !disposed && !suspended) { error = ex.Message; message = "Inicio del menú pendiente; puedes abrirlo desde la app."; } } }
            finally { Volatile.Write(ref pending, 0); }
        }
        public void Suspend() { lock (stateGate) { suspended = true; generation++; retry?.Dispose(); retry = null; } }
        public void Resume() { lock (stateGate) { suspended = false; generation++; } RunAtStartup(); }
        public void Dispose()
        {
            lock (stateGate) { if (disposed) return; disposed = true; generation++; retry?.Dispose(); retry = null; }
            buttons?.SetMenuActions(null, null); // QAM has its own lifetime and survives a helper restart.
        }
    }
}
