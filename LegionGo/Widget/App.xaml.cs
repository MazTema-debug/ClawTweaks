// SPDX-License-Identifier: AGPL-3.0-only
// Game Bar activation lifecycle adapted from ClawTweaks XboxGamingBar/App.xaml.cs.
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    sealed partial class App : Application
    {
        private sealed class WidgetView
        {
            internal Frame Frame;
            internal XboxGameBarWidget Widget;
            internal readonly TaskCompletionSource<bool> Closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly ConcurrentDictionary<Window, WidgetView> widgets = new ConcurrentDictionary<Window, WidgetView>();
        public App() { InitializeComponent(); TrayLifecycleTrace.Write("process_started"); }
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (args.PrelaunchActivated) return;
            TrayLifecycleTrace.Write("desktop_launch_requested");
            if (desktopView != null) {
                if (!await RestoreDesktop()) {
                    await desktopView.Window.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        var page = (desktopView.Window.Content as Frame)?.Content as MainPage;
                        page?.ReportDesktopLaunchError("Windows no pudo recuperar la ventana de escritorio.");
                    });
                }
                await EnsureTray(); return;
            }
            // A regular launch can target the main CoreWindow even when that
            // window is already owned by Game Bar. Never turn its hosted frame
            // into the desktop page or change the widget's window size.
            if (widgets.ContainsKey(Window.Current)) {
                try { await OpenDesktopView(); }
                catch (Exception ex) {
                    var page = (Window.Current.Content as Frame)?.Content as MainPage;
                    page?.ReportDesktopLaunchError(ex.Message);
                }
                return;
            }
            ShowDesktop();
        }
        private void ShowDesktop()
        {
            var frame = Window.Current.Content as Frame ?? new Frame();
            Window.Current.Content = frame;
            if (frame.Content == null && !frame.Navigate(typeof(MainPage)))
                throw new InvalidOperationException("No se pudo cargar la ventana de escritorio.");
            ApplicationView.GetForCurrentView().TryResizeView(new Size(500, 860));
            Window.Current.Activate();
            RegisterDesktop();
        }
        private async Task OpenDesktopView()
        {
            var view = CoreApplication.CreateNewView();
            int id = 0;
            await view.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                ShowDesktop();
                id = ApplicationView.GetForCurrentView().Id;
            });
            if (!await ApplicationViewSwitcher.TryShowAsStandaloneAsync(id))
                throw new InvalidOperationException("Windows no pudo mostrar la ventana de escritorio.");
        }
        protected override void OnActivated(IActivatedEventArgs args)
        {
            var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            if (widgetArgs == null || widgetArgs.AppExtensionId != "LegionWidget") return;
            TrayLifecycleTrace.Write(widgetArgs.IsLaunchActivation ? "widget_launch" : "widget_repeat");
            var window = Window.Current;
            WidgetView host;
            if (!widgets.TryGetValue(window, out host))
            {
                // Repeat activations carry no new hosted CoreWindow. Constructing
                // an SDK connection here could replace the desktop with a grey host.
                if (!widgetArgs.IsLaunchActivation) { TrayLifecycleTrace.Write("widget_repeat_missing_host"); return; }
                var frame = new Frame();
                window.Content = frame;
                var widget = new XboxGameBarWidget(widgetArgs, window.CoreWindow, frame);
                host = new WidgetView { Frame = frame, Widget = widget };
                widgets[window] = host; // Keep the original hosted frame and widget together.
                var created = host;
                window.Closed += (sender, e) => {
                    WidgetView removed; widgets.TryRemove(window, out removed);
                    TrayLifecycleTrace.Write("widget_closed");
                    created.Closed.TrySetResult(true);
                };
            }
            else if (window.Content != host.Frame) window.Content = host.Frame;
            if (!(host.Frame.Content is MainPage)) {
                try {
                    if (!host.Frame.Navigate(typeof(MainPage), host.Widget)) throw new InvalidOperationException("No se pudo cargar el panel.");
                }
                catch (Exception ex) {
                    host.Frame.Content = new TextBlock { Text = "No se pudo cargar LegionTweaks. Cierra y vuelve a abrir el widget.\n" + ex.Message,
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
                }
            }
            window.Activate();
        }
        private Task<bool> CloseWidgetsForExit()
        {
            var close = widgets.ToArray().Select(pair => (Func<Task<bool>>)(async () => {
                var owner = pair.Key; var host = pair.Value;
                bool accepted = await owner.Dispatcher.TryRunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                    try { host.Widget.Close(); }
                    catch (Exception) { host.Closed.TrySetResult(false); }
                });
                if (!accepted) return false;
                return await host.Closed.Task;
            })).ToArray();
            return HostedWidgetExit.CloseAllAsync(close, Task.Delay(4000));
        }
    }
}
