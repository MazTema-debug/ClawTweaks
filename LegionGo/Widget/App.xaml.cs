// SPDX-License-Identifier: AGPL-3.0-only
// Game Bar activation lifecycle adapted from ClawTweaks XboxGamingBar/App.xaml.cs.
using System;
using System.Collections.Concurrent;
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
        }
        private sealed class DesktopView
        {
            internal int Id;
            internal Windows.UI.Core.CoreDispatcher Dispatcher;
        }
        private readonly ConcurrentDictionary<Window, WidgetView> widgets = new ConcurrentDictionary<Window, WidgetView>();
        private readonly ConcurrentDictionary<Window, DesktopView> desktops = new ConcurrentDictionary<Window, DesktopView>();
        public App() { InitializeComponent(); }
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (args.PrelaunchActivated) return;
            try { if (await TryShowDesktopView()) return; }
            catch (Exception ex) {
                ((Window.Current.Content as Frame)?.Content as MainPage)?.ReportDesktopLaunchError(ex.Message);
                return; // Never create a duplicate view over an existing desktop/draft.
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
            var window = Window.Current;
            if (desktops.TryAdd(window, new DesktopView { Id = ApplicationView.GetForCurrentView().Id, Dispatcher = window.Dispatcher }))
                window.Closed += (sender, e) => { DesktopView removed; desktops.TryRemove(window, out removed); };
            Window.Current.Activate();
        }
        private async Task<bool> TryShowDesktopView()
        {
            foreach (var desktop in desktops.ToArray()) {
                try {
                    await desktop.Value.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => desktop.Key.Activate());
                    if (!await ApplicationViewSwitcher.TryShowAsStandaloneAsync(desktop.Value.Id))
                        throw new InvalidOperationException("Windows no pudo mostrar la ventana de escritorio existente.");
                    return true;
                }
                catch (Exception) { if (desktops.ContainsKey(desktop.Key)) throw; }
            }
            return false;
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
            var window = Window.Current;
            WidgetView host;
            if (!widgets.TryGetValue(window, out host))
            {
                var frame = new Frame();
                window.Content = frame;
                var widget = new XboxGameBarWidget(widgetArgs, window.CoreWindow, frame);
                host = new WidgetView { Frame = frame, Widget = widget };
                widgets[window] = host; // Keep the original hosted frame and widget together.
                window.Closed += (sender, e) => { WidgetView removed; widgets.TryRemove(window, out removed); };
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
    }
}
