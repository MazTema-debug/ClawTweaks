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
        private async void DesktopHistory_Click(object sender, RoutedEventArgs e)
        {
            if (working || desktopStatus?.SupportsHistory != true) return;
            var response = await Send(new Request { Operation = "controller_startup_history" });
            if (response?.Success != true || response.ControllerStartupHistory == null) {
                DesktopHistoryText.Text = "No se pudo consultar el historial. Los registros se conservan."; return;
            }
            try {
                var history = response.ControllerStartupHistory; history.Validate();
                var rows = new System.Collections.Generic.List<string>();
                for (int i = history.Decisions.Length - 1; i >= 0; i--) rows.Add(DescribeStartupDecision(history.Decisions[i]));
                DesktopHistoryText.Text = rows.Count == 0 ? "Todavía no hay decisiones de inicio registradas."
                    : "Últimos inicios del helper, más reciente primero:\n" + string.Join("\n\n", rows);
            }
            catch (Exception) { DesktopHistoryText.Text = "Historial recibido sin confirmar. Vuelve a consultar."; }
        }
        private static string DescribeStartupDecision(ControllerStartupDecision value)
        {
            value.Validate();
            return new DateTime(value.StartedUtc, DateTimeKind.Utc).ToLocalTime().ToString("g") + " · " + value.Message
                + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
        }
        private bool GeneralSplitSelected => SplitLayout?.SelectedIndex == 0;
        private bool SelectedSplitSupported => GeneralSplitSelected ? lastControllerStatus?.SupportsGeneralSplitController == true
            : SplitLayout?.SelectedIndex == 1 && lastControllerStatus?.SupportsSplitController == true;
        private void SplitLayout_SelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSplitAvailability();
        private void RenderSplitAvailability()
        {
            if (SplitStart == null || SplitLayout == null || SplitAvailabilityText == null) return;
            bool resuming = lastControllerStatus?.Suspended == true || lastControllerStatus?.RecoveringInput == true;
            SplitLayout.IsEnabled = !controllerPreparing && !resuming;
            SplitStart.IsEnabled = SelectedSplitSupported && !controllerPreparing && !resuming;
            SplitAvailabilityText.Text = SelectedSplitSupported ? "Seleccionar no cambia la sesión activa. Pulsa Activar dos jugadores para usar esta distribución."
                : GeneralSplitSelected ? "General requiere el helper 0.1.34. Actualiza o selecciona Isaac si tu helper admite dos jugadores."
                : "Consulta el estado del mando o actualiza el helper para usar dos jugadores.";
        }
        private async void SplitStart_Click(object sender, RoutedEventArgs e)
        {
            if (working || controllerPreparing || playerOneDialogOpen || !SelectedSplitSupported) return;
            bool general = GeneralSplitSelected;
            playerOneDialogOpen = true;
            try {
                string distribution = general ? "General: cada stick mueve a su jugador. La cruceta izquierda y los botones derechos envían A/B/X/Y según su posición horizontal; no envían el stick derecho."
                    : "Isaac: cada stick mueve a su jugador. La cruceta izquierda y los botones derechos envían su stick de disparo. Clic del stick: A para los menús.";
                var dialog = new ContentDialog { Title = general ? "Dos jugadores · General" : "Dos jugadores · Isaac", PrimaryButtonText = "Iniciar", CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Cierra los juegos, mantén ambos mandos acoplados y FPS en OFF. Steam puede permanecer abierto.\n\nSe sustituirá la sesión actual por dos Xbox virtuales: izquierda jugador 1 y derecha jugador 2. Para reservar ambos puestos, el receptor puede cambiar de modo brevemente y volver al original.\n\n" + distribution + "\n\nSuelta todos los controles antes de jugar. Puedes desacoplar ambos al terminar la preparación. Sostén cada mitad horizontal con los botones hacia ti: LB/LT a tu izquierda y RB/RT a tu derecha. No hay vibración ni mouse propio en este modo. No se aplican ajustes de energía ni se cambia el inicio Desktop guardado. Usa Devolver mando físico para detener ambos." } };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Send(new Request { Operation = general ? "controller_split_general" : "controller_split" });
            }
            catch (Exception ex) { StatusText.Text = "No se inició dos jugadores: " + ex.Message; }
            finally { playerOneDialogOpen = false; }
        }
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
            bool resuming = lastControllerStatus?.Suspended == true || lastControllerStatus?.RecoveringInput == true;
            DesktopStart.IsEnabled = DesktopStartup.IsEnabled = DesktopStartupSave.IsEnabled = supported && !controllerPreparing && !resuming;
            DesktopHistory.IsEnabled = supported && value.SupportsHistory;
            if (!supported) { DesktopText.Text = "Desktop sin confirmar. Comprueba la conexión y actualiza el helper a 0.1.25.";
                DesktopLastDecision.Text = "Último inicio sin confirmar."; return; }
            if (!desktopSettingsLoaded || saved) { DesktopStartup.IsOn = value.Enabled; desktopSettingsLoaded = true; }
            DesktopText.Text = value.Message + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
            try { DesktopLastDecision.Text = value.LastDecision == null ? ""
                : "Último inicio registrado: " + DescribeStartupDecision(value.LastDecision); }
            catch (Exception) { DesktopLastDecision.Text = "Último registro de inicio sin confirmar."; }
            if (!string.IsNullOrEmpty(value.HistoryError)) DesktopLastDecision.Text += "\n" + value.HistoryError;
        }
    }
}
