// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool renderingQam, qamConfigurationDirty, registeringDesktop;
        private readonly string desktopViewId = Guid.NewGuid().ToString("N");
        private long nextDesktopRegistration;
        private bool qamSupported;
        private bool desktopRegistrationAccepted;
        private void QamConfiguration_Changed(object sender, object args)
        {
            if (!renderingQam && pageLoaded && widget == null) qamConfigurationDirty = true;
        }
        private async void DesktopQamOpen_Click(object sender, RoutedEventArgs args)
        {
            if (widget != null || !qamSupported) return;
            await Send(new Request { Operation = "qam_open" });
        }
        private async void QamConfigurationSave_Click(object sender, RoutedEventArgs args)
        {
            if (widget != null || !qamSupported || QamToggleMode.SelectedIndex < 0 || DesktopToggleMode.SelectedIndex < 0) return;
            await Send(new Request { Operation = "qam_configure", Values = new[] { QamStartup.IsOn ? 1 : 0,
                QamToggleMode.SelectedIndex, DesktopToggleMode.SelectedIndex } });
        }
        private void RenderQam(QamLifecycleStatus value, bool saved = false)
        {
            bool desktop = widget == null;
            DesktopQamCard.Visibility = QamStartupCard.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
            qamSupported = desktop && value?.Supported == true;
            DesktopQamOpen.IsEnabled = QamStartup.IsEnabled = QamToggleMode.IsEnabled = DesktopToggleMode.IsEnabled = QamConfigurationSave.IsEnabled = qamSupported;
            if (saved) qamConfigurationDirty = false;
            if (value == null || !value.Supported) {
                QamStartupText.Text = "Configuración no disponible; comprueba la conexión y la entrega del helper.";
                return;
            }
            if (!qamConfigurationDirty) {
                renderingQam = true;
                try { QamStartup.IsOn = value.StartWithLegionTweaks; QamToggleMode.SelectedIndex = (int)value.ToggleMode; DesktopToggleMode.SelectedIndex = (int)value.DesktopToggleMode; }
                finally { renderingQam = false; }
            }
            QamStartupText.Text = value.Message + (value.PreferenceKnown ? "" : "\nPreferencia sin confirmar; vuelve a guardarla.")
                + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
            if (desktop && (!desktopRegistrationAccepted || !value.DesktopRegistered) && pageLoaded && controllerReadClock.ElapsedMilliseconds >= nextDesktopRegistration)
                _ = RegisterDesktopView();
        }
        private async Task RegisterDesktopView()
        {
            if (widget != null || !pageLoaded || !qamSupported || registeringDesktop) return;
            registeringDesktop = true;
            nextDesktopRegistration = controllerReadClock.ElapsedMilliseconds + 5000;
            int view = viewGeneration;
            try {
                var interop = (ICoreWindowInterop)(object)ownerWindow.CoreWindow;
                Marshal.ThrowExceptionForHR(interop.GetWindowHandle(out IntPtr window));
                if (!pageLoaded || view != viewGeneration || widget != null) return;
                var response = await PipeClient.Send(new Request { Operation = "qam_desktop_register", DesktopView = new DesktopViewRegistration {
                    Window = window.ToInt64(), ViewId = desktopViewId } });
                desktopRegistrationAccepted = response.Success;
                if (!response.Success && pageLoaded && view == viewGeneration) QamStartupText.Text = "Registro de la vista pendiente: " + response.Message;
            }
            catch (Exception ex) { if (pageLoaded && view == viewGeneration) QamStartupText.Text = "Registro de la vista pendiente: " + ex.Message; }
            finally { registeringDesktop = false; }
        }
        [ComImport, Guid("45D64A29-A63E-4CB6-B498-5781D298CB4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICoreWindowInterop
        {
            [PreserveSig] int GetWindowHandle(out IntPtr window);
            [PreserveSig] int SetMessageHandled([MarshalAs(UnmanagedType.Bool)] bool value);
        }
    }
}
