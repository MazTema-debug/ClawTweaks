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
            internal IntPtr Handle;
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
            var desktop = new DesktopView { Window = window, View = ApplicationView.GetForCurrentView(), Handle = handle };
            desktopView = desktop;
            window.Closed += (s, e) => {
                if (Interlocked.CompareExchange(ref desktopView, null, desktop) == desktop) traySession?.Dispose();
            };
            SystemNavigationManagerPreview.GetForCurrentView().CloseRequested += (s, e) => {
                // Do not intercept a widget, a programmatic consolidation, or an
                // ordinary close if no functioning tray has acknowledged readiness.
                if (!desktop.State.InterceptClose) return;
                e.Handled = true;
                // The decision is synchronous. Do not hold the system close
                // deferral while asking Windows to consolidate that same view.
                _ = HideDesktop(desktop);
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
        private async Task<bool> RestoreDesktop()
        {
            var desktop = desktopView;
            if (desktop == null) return false;
            return await OnDesktop(desktop, async () => {
                bool shown = await ApplicationViewSwitcher.TryShowAsStandaloneAsync(desktop.View.Id);
                if (shown) { desktop.State.Restored(); desktop.Window.Activate(); }
                return shown;
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
        private async Task<bool> HideDesktop(DesktopView desktop)
        {
            if (desktop != desktopView || !desktop.State.BeginHide()) return false;
            bool hidden = false;
            try { hidden = await desktop.View.TryConsolidateAsync(); }
            catch (Exception) { hidden = false; }
            finally { desktop.State.EndHide(hidden); }
            if (!hidden) {
                desktop.State.Disconnected();
                var page = (desktop.Window.Content as Frame)?.Content as MainPage;
                page?.ReportTrayError("No se pudo ocultar la ventana en la bandeja. Cerrar conserva su funcionamiento normal.");
            }
            // Losing the tray while consolidation is awaiting Windows must not
            // strand a window. Restore it, preserving the helper/controller.
            if (hidden && !desktop.State.Ready && !desktop.State.Exiting) await RestoreDesktop();
            return hidden;
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
                            if (operation == "Ready") { desktop.State.Connected(); return true; }
                            if (operation == "Open") return await app.RestoreDesktop();
                            if (operation == "Minimized") return await app.HideDesktop(desktop);
                            if (operation == "Exit") { desktop.State.Exit(); return true; }
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
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
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
