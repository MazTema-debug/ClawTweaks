// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool desktopSettingsLoaded;
        private ControllerStartupStatus desktopStatus;
        private async void DesktopStartupSave_Click(object sender, RoutedEventArgs e)
        {
            if (working || controllerPreparing || desktopStatus?.Supported != true) return;
            await Send(new Request { Operation = "controller_startup_set", Values = new[] { DesktopStartup.IsOn ? 1 : 0 } });
        }
        private async void DesktopStart_Click(object sender, RoutedEventArgs e)
        {
            if (working || controllerPreparing || playerOneDialogOpen || desktopStatus?.Supported != true) return;
            playerOneDialogOpen = true;
            try {
                var dialog = new ContentDialog { Title = "Activar Desktop como jugador 1", PrimaryButtonText = "Iniciar", CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Desktop usa la distribución Xbox estándar integrada, sin ajustes de energía.\n\nCierra los juegos; Steam puede permanecer abierto con el helper 0.1.25. Mantén FPS en OFF y los mandos acoplados. Si hace falta, el mando cambiará de modo brevemente y recuperará su modo original.\n\nEl inicio automático guardado no cambia con este botón." } };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Send(new Request { Operation = "controller_desktop" });
            }
            catch (Exception ex) { StatusText.Text = "No se inició Desktop: " + ex.Message; }
            finally { playerOneDialogOpen = false; }
        }
        private void RenderDesktop(ControllerStartupStatus value, bool saved = false)
        {
            desktopStatus = value;
            bool supported = value?.Supported == true;
            DesktopStart.IsEnabled = DesktopStartup.IsEnabled = DesktopStartupSave.IsEnabled = supported && !controllerPreparing;
            if (!supported) { DesktopText.Text = "Desktop sin confirmar. Comprueba la conexión y actualiza el helper a 0.1.25."; return; }
            if (!desktopSettingsLoaded || saved) { DesktopStartup.IsOn = value.Enabled; desktopSettingsLoaded = true; }
            DesktopText.Text = value.Message + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
        }
    }
}
