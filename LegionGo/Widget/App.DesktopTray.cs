// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.UI.Core;
using Windows.UI.Core.Preview;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    sealed partial class App
    {
        private sealed class DesktopView
        {
            internal Window Window;
            internal ApplicationView View;
            internal SystemNavigationManagerPreview CloseManager;
            internal IntPtr Handle;
            internal Task<bool> HideOperation;
            internal readonly DesktopWindowQueue Visibility = new DesktopWindowQueue();
            internal readonly DesktopTrayState State = new DesktopTrayState();
        }
        private DesktopView desktopView;
        private TraySession traySession;
        private Task trayLaunch;

        private void RegisterDesktop()
        {
            if (desktopView != null && desktopView.Window == Window.Current) return;
            var window = Window.Current;
            IntPtr handle;
            try { ((ICoreWindowInterop)(object)window.CoreWindow).get_WindowHandle(out handle); }
            catch (Exception) { return; } // Normal desktop close remains available.
            var desktop = new DesktopView { Window = window, View = ApplicationView.GetForCurrentView(), Handle = handle,
                CloseManager = SystemNavigationManagerPreview.GetForCurrentView() };
            desktopView = desktop;
            TrayLifecycleTrace.Write("desktop_registered", desktop.State);
            window.Closed += (s, e) => {
                TrayLifecycleTrace.Write("desktop_closed", desktop.State);
                if (Interlocked.CompareExchange(ref desktopView, null, desktop) == desktop) traySession?.Dispose();
            };
            desktop.CloseManager.CloseRequested += (s, e) => {
                TrayLifecycleTrace.Write("desktop_close_requested", desktop.State);
                // Only the desktop participates. Coalesce repeated X presses while
                // its frame is being hidden; ordinary close remains if no tray is ready.
                if (!desktop.State.InterceptClose) return;
                e.Handled = true;
                TrayLifecycleTrace.Write("desktop_close_queued", desktop.State);
                // Finish the native close decision before hiding the frame.
                // Keep the manager alive and queue on the view's dispatcher rather
                // than reentering window management from its CloseRequested event.
                _ = OnDesktop(desktop, () => HideDesktop(desktop));
            };
            _ = EnsureTray();
        }
        private Task EnsureTray()
        {
            if (traySession != null || (trayLaunch != null && !trayLaunch.IsCompleted)) return trayLaunch ?? Task.CompletedTask;
            return trayLaunch = LaunchTray();
        }
        private async Task LaunchTray()
        {
            try { await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync(); }
            catch (Exception) {
                var page = (desktopView?.Window.Content as Frame)?.Content as MainPage;
                page?.ReportTrayError("La bandeja no está disponible. Cerrar conserva su funcionamiento normal.");
            }
        }
        private async Task<bool> RestoreDesktop(bool waitForHide = true, bool nativeOnly = false)
        {
            var desktop = desktopView;
            if (desktop == null) return false;
            return await OnDesktop(desktop, async () => {
                if (waitForHide && desktop.HideOperation != null && !desktop.HideOperation.IsCompleted) await desktop.HideOperation;
                return await desktop.Visibility.RunAsync(async () => {
                    bool shellRestored = traySession != null ? await traySession.WindowAction("RestoreDesktop") : !nativeOnly;
                    // Tray Open restores without activation. Only the companion
                    // that received the click may then request foreground, if its
                    // input gesture is still current after the asynchronous reply.
                    bool shown = !nativeOnly && await ApplicationViewSwitcher.TryShowAsStandaloneAsync(desktop.View.Id);
                    if (traySession != null) shown = await traySession.WindowAction("DesktopVisible");
                    if (shown) {
                        desktop.State.Restored();
                        if (!nativeOnly) desktop.Window.Activate();
                        ((desktop.Window.Content as Frame)?.Content as MainPage)?.RefreshDesktopTrayVisibility();
                    }
                    TrayLifecycleTrace.Write(shown && shellRestored ? "desktop_restored" : "desktop_restore_failed", desktop.State);
                    return shown && shellRestored;
                });
            });
        }
        private static async Task<bool> OnDesktop(DesktopView desktop, Func<Task<bool>> action)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try {
                bool accepted = await desktop.Window.Dispatcher.TryRunAsync(CoreDispatcherPriority.Normal, async () => {
                    try { result.TrySetResult(await action()); }
                    catch (Exception) { result.TrySetResult(false); }
                });
                if (!accepted) result.TrySetResult(false);
            } catch (Exception) { result.TrySetResult(false); }
            return await result.Task;
        }
        private Task<bool> HideDesktop(DesktopView desktop)
        {
            if (desktop != desktopView) return Task.FromResult(false);
            // A cached Hidden flag does not prove that Shell still has no taskbar
            // entry. Revalidate a repeated close/minimize through the companion.
            if (desktop.HideOperation != null && !desktop.HideOperation.IsCompleted) return desktop.HideOperation;
            return desktop.HideOperation = HideDesktopCore(desktop);
        }
        private async Task<bool> HideDesktopCore(DesktopView desktop)
        {
            bool hidden = await desktop.Visibility.RunAsync(async () => {
                if (desktop != desktopView || !desktop.State.BeginHide()) return false;
                TrayLifecycleTrace.Write("hide_requested", desktop.State);
                bool applied = false;
                try { applied = traySession != null && await traySession.WindowAction("HideDesktop"); }
                catch (Exception) { applied = false; }
                finally { desktop.State.EndHide(applied); }
                TrayLifecycleTrace.Write(applied ? "hide_completed" : "hide_failed", desktop.State);
                ((desktop.Window.Content as Frame)?.Content as MainPage)?.RefreshDesktopTrayVisibility();
                if (!applied) {
                    desktop.State.Disconnected();
                    var page = (desktop.Window.Content as Frame)?.Content as MainPage;
                    page?.ReportTrayError("No se pudo ocultar la ventana en la bandeja. Cerrar conserva su funcionamiento normal.");
                }
                return applied;
            });
            // Losing the tray while frame management is awaiting Windows must not
            // strand a window. Restore it, preserving the helper/controller.
            // This recovery is part of HideOperation itself; it must not await
            // that same task. Other restore requests still wait for hiding.
            if (hidden && !desktop.State.Ready && !desktop.State.Exiting) await RestoreDesktop(false);
            return hidden;
        }
        internal bool IsDesktopInTray(Window window)
        {
            var desktop = desktopView;
            return desktop != null && desktop.Window == window && (desktop.State.Hidden || desktop.State.Hiding);
        }
        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            var details = args.TaskInstance.TriggerDetails as AppServiceTriggerDetails;
            if (details == null || details.Name != DesktopTrayProtocol.ServiceName
                || details.CallerPackageFamilyName != Package.Current.Id.FamilyName) return;
            var session = new TraySession(this, details.AppServiceConnection, args.TaskInstance);
            if (Interlocked.CompareExchange(ref traySession, session, null) != null) session.Dispose();
            else session.Attach(args.TaskInstance);
        }
        private sealed class TraySession : IDisposable
        {
            private readonly App app;
            private readonly AppServiceConnection connection;
            private BackgroundTaskDeferral lifetime;
            private int disposed;
            internal TraySession(App app, AppServiceConnection connection, IBackgroundTaskInstance task)
            {
                this.app = app; this.connection = connection;
                lifetime = task.GetDeferral();
            }
            internal void Attach(IBackgroundTaskInstance task)
            {
                connection.RequestReceived += Received;
                connection.ServiceClosed += (s, e) => Dispose();
                task.Canceled += (s, e) => Dispose();
            }
            private async void Received(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
            {
                var deferral = args.GetDeferral();
                try {
                    object version, command = null;
                    bool valid = args.Request.Message.TryGetValue("Version", out version) && version is int && (int)version == DesktopTrayProtocol.Version
                        && args.Request.Message.TryGetValue("Command", out command) && command is string;
                    var desktop = app.desktopView;
                    var reply = new ValueSet { ["Success"] = false };
                    if (valid && desktop != null && app.traySession == this && Volatile.Read(ref disposed) == 0) {
                        string operation = (string)command;
                        bool ok = await OnDesktop(desktop, async () => {
                            if (operation == "Ready") { desktop.State.Connected(); TrayLifecycleTrace.Write("tray_ready", desktop.State); return true; }
                            if (operation == "Open") return await app.RestoreDesktop(nativeOnly: true);
                            if (operation == "Minimized") { TrayLifecycleTrace.Write("minimized_frame", desktop.State); return await app.HideDesktop(desktop); }
                            if (operation == "Exit") {
                                TrayLifecycleTrace.Write("exit_requested", desktop.State);
                                if (!await app.CloseWidgetsForExit()) {
                                    TrayLifecycleTrace.Write("widgets_close_failed", desktop.State);
                                    var page = (desktop.Window.Content as Frame)?.Content as MainPage;
                                    page?.ReportTrayError("No se pudo cerrar el widget. La interfaz sigue abierta; puedes volver a intentar Salir.");
                                    return false;
                                }
                                desktop.State.Exit(); TrayLifecycleTrace.Write("exit_committed", desktop.State); return true;
                            }
                            return false;
                        });
                        reply["Success"] = ok;
                        if (ok && operation == "Ready") reply["Window"] = desktop.Handle.ToInt64();
                        await args.Request.SendResponseAsync(reply);
                        if (ok && operation == "Exit") {
                            await desktop.Window.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => app.Exit());
                            Dispose();
                        }
                    }
                    else await args.Request.SendResponseAsync(reply);
                }
                catch (Exception) { Dispose(); }
                finally { deferral.Complete(); }
            }
            internal async Task<bool> WindowAction(string command)
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                try {
                    var reply = await connection.SendMessageAsync(new ValueSet { ["Command"] = command, ["Version"] = DesktopTrayProtocol.Version });
                    object success;
                    return reply.Status == AppServiceResponseStatus.Success && reply.Message.TryGetValue("Success", out success) && success is bool && (bool)success;
                }
                catch (Exception) { return false; }
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                TrayLifecycleTrace.Write("tray_connection_closed");
                connection.RequestReceived -= Received;
                connection.Dispose();
                var hold = Interlocked.Exchange(ref lifetime, null);
                if (Interlocked.CompareExchange(ref app.traySession, null, this) != this) { hold?.Complete(); return; }
                var desktop = app.desktopView;
                if (desktop == null) { hold?.Complete(); return; }
                _ = RestoreAfterDisconnect(desktop, hold);
            }
            private async Task RestoreAfterDisconnect(DesktopView desktop, BackgroundTaskDeferral hold)
            {
                try {
                    await OnDesktop(desktop, async () => {
                        if (desktop.State.Disconnected()) return await app.RestoreDesktop();
                        return true;
                    });
                }
                finally { hold?.Complete(); }
            }
        }
        [ComImport, Guid("45D64A29-A63E-4CB6-B498-5781D298CB4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICoreWindowInterop
        {
            void get_WindowHandle(out IntPtr hwnd);
            void put_MessageHandled([MarshalAs(UnmanagedType.Bool)] bool handled);
        }
    }
}
