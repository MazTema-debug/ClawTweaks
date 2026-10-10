// SPDX-License-Identifier: AGPL-3.0-only
// Game Bar activation lifecycle adapted from ClawTweaks XboxGamingBar/App.xaml.cs.
using System.Collections.Generic;
using Microsoft.Gaming.XboxGameBar;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    sealed partial class App : Application
    {
        private readonly Dictionary<Window, XboxGameBarWidget> widgets = new Dictionary<Window, XboxGameBarWidget>();
        public App() { InitializeComponent(); }
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var frame = Window.Current.Content as Frame ?? new Frame();
            Window.Current.Content = frame;
            if (frame.Content == null) frame.Navigate(typeof(MainPage));
            ApplicationView.GetForCurrentView().TryResizeView(new Size(500, 860));
            Window.Current.Activate();
        }
        protected override void OnActivated(IActivatedEventArgs args)
        {
            var widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
            if (widgetArgs == null || widgetArgs.AppExtensionId != "LegionWidget") return;
            var window = Window.Current;
            if (widgetArgs.IsLaunchActivation || !widgets.ContainsKey(window))
            {
                var frame = new Frame();
                window.Content = frame;
                var widget = new XboxGameBarWidget(widgetArgs, window.CoreWindow, frame);
                widgets[window] = widget; // Retain for the full lifetime of this CoreWindow.
                frame.Navigate(typeof(MainPage));
                window.Closed += (sender, e) => widgets.Remove(window);
            }
            window.Activate();
        }
    }
}
