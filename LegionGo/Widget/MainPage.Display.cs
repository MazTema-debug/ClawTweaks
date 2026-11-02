// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private DisplayStatus displayStatus;
        private bool displaySupported, displayResolutionSupported, displayBusy, renderingDisplay;
        private string displayPolledToken;
        private int displayPolls;
        private readonly DispatcherTimer displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private void InitializeDisplay()
        {
            SetDisplayChoices(null);
            Controls.SelectionChanged += async (s, e) => {
                if (e.OriginalSource == Controls && Controls.SelectedItem == DisplayTab && pageLoaded && displaySupported)
                    await DisplaySend(new Request { Operation = "display_status" });
            };
            Loaded += (s, e) => { if (displayStatus?.Pending == true) displayTimer.Start(); };
            displayTimer.Tick += async (s, e) => {
                if (!pageLoaded || displayStatus?.Pending != true) { displayTimer.Stop(); return; }
                if (!working && !displayBusy)
                {
                    if (++displayPolls > 30) { displayTimer.Stop(); DisplayTrialText.Text = "El cambio anterior sigue pendiente. Actualiza la consulta para comprobarlo."; return; }
                    await DisplaySend(new Request { Operation = "display_status" });
                }
            };
        }
        private void RenderDisplayCapability(bool supported, bool resolutionSupported)
        {
            displaySupported = supported;
            displayResolutionSupported = resolutionSupported;
            if (!supported) {
                displayTimer.Stop(); displayStatus = null; ClearDisplayReading(); SetDisplayChoices(null);
                DisplayText.Text = "Consulta de pantalla sin confirmar. Comprueba la conexión y actualiza el helper.";
            }
            UpdateDisplayButtons();
        }
        private void UpdateDisplayButtons()
        {
            bool ready = displaySupported && !displayBusy;
            bool pending = displayStatus?.Pending == true;
            DisplayQuery.IsEnabled = ready;
            DisplayCopy.IsEnabled = displayStatus?.Info != null;
            DisplayModesBox.IsEnabled = ready && displayResolutionSupported && !pending
                && displayStatus?.Info?.CanChange == true && displayStatus.SnapshotId != null;
            DisplayRecover.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            DisplayRecover.IsEnabled = ready && pending && displayStatus.RecoveryPending;
        }
        private async Task DisplaySend(Request request)
        {
            if (working || displayBusy) return;
            int view = viewGeneration;
            displayBusy = true; UpdateDisplayButtons();
            try
            {
                var response = await PipeClient.Send(request);
                if (!pageLoaded || view != viewGeneration) return;
                displaySupported = response.SupportsDisplay;
                displayResolutionSupported = response.SupportsDisplayResolution;
                if (response.Display != null) RenderDisplay(response.Display);
                else {
                    displayStatus = null;
                    ClearDisplayReading(); SetDisplayChoices(null);
                    DisplayTrialText.Text = response.Message ?? "No se pudo leer el estado de pantalla. Actualiza la consulta.";
                }
                if (!response.Success) displayTimer.Stop();
            }
            catch (Exception ex)
            {
                displayTimer.Stop();
                if (!pageLoaded || view != viewGeneration) return;
                DisplayTrialText.Text = ex.Message + " · Actualiza la consulta para comprobar la resolución activa.";
                displayStatus = null;
                ClearDisplayReading(); SetDisplayChoices(null);
            }
            finally { displayBusy = false; UpdateDisplayButtons(); }
        }
        private void RenderDisplay(DisplayStatus value)
        {
            if (value.TrialToken != displayPolledToken) { displayPolledToken = value.TrialToken; displayPolls = 0; }
            displayStatus = value;
            if (value.Info != null)
            {
                var info = value.Info;
                DisplayCurrentText.Text = info.Current == null ? "Resolución sin confirmar"
                    : info.Current.Height + "p · " + info.Current.Width + " × " + info.Current.Height;
                DisplayRefreshText.Text = info.Current == null ? "Frecuencia sin confirmar"
                    : info.Current.Frequency + " Hz consultados · se conservan al cambiar resolución";
                DisplayText.Text = info.DeviceName + " · " + info.Monitor + "\n" + info.Adapter
                    + "\nModo consultado: " + info.Current + " · orientación " + info.Orientation + "°"
                    + "\nPantallas activas: " + info.ActiveDisplays + " · consulta UTC: " + info.CapturedAtUtc
                    + (info.Restriction == null ? "" : "\n" + info.Restriction);
            }
            else { ClearDisplayReading(); DisplayText.Text = "Lectura de pantalla no disponible. Actualiza la consulta."; }
            SetDisplayChoices(value.Info);
            DisplayTrialText.Text = (value.Pending ? "Hay un cambio anterior pendiente. Espera su recuperación o pulsa Recuperar cuando esté disponible." : value.Message ?? "Sin cambios pendientes.")
                + (!displayResolutionSupported ? "\nActualiza el helper para usar el selector de resolución." : "")
                + (value.Error == null ? "" : "\nDetalle: " + value.Error);
            if (value.Pending && !value.RecoveryPending && pageLoaded) displayTimer.Start(); else displayTimer.Stop();
            UpdateDisplayButtons();
        }
        private void SetDisplayChoices(DisplayInfo info)
        {
            // Programmatic refreshes never apply a mode. Explicit containers also disable unavailable items in UWP.
            renderingDisplay = true;
            try {
                var items = DisplayResolution.Options(info).Select(x => new ComboBoxItem {
                    Content = x.Label, Tag = x, IsEnabled = x.IsAvailable }).ToArray();
                DisplayModesBox.ItemsSource = items;
                DisplayModesBox.SelectedItem = items.FirstOrDefault(item => {
                    var resolution = (DisplayResolution)item.Tag;
                    return resolution.Width == info?.Current?.Width && resolution.Height == info?.Current?.Height;
                });
            }
            finally { renderingDisplay = false; }
        }
        private void ClearDisplayReading()
        {
            DisplayCurrentText.Text = "Resolución actual sin confirmar";
            DisplayRefreshText.Text = "Actualiza la consulta para comprobar los Hz activos";
        }
        private async void DisplayQuery_Click(object sender, RoutedEventArgs e) => await DisplaySend(new Request { Operation = "display_status" });
        private async void DisplayRecover_Click(object sender, RoutedEventArgs e)
        {
            displayPolledToken = null;
            await DisplaySend(new Request { Operation = "display_recover" });
        }
        private async void DisplayModesBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (renderingDisplay || !pageLoaded || working || displayBusy || !displayResolutionSupported
                || displayStatus?.SnapshotId == null || displayStatus.Pending
                || !(DisplayModesBox.SelectedItem is ComboBoxItem item)
                || !(item.Tag is DisplayResolution resolution) || !resolution.IsAvailable) return;
            var current = displayStatus.Info?.Current;
            if (current?.Width == resolution.Width && current?.Height == resolution.Height) return;
            await DisplaySend(new Request { Operation = "display_resolution", Display = new DisplayRequest {
                SnapshotId = displayStatus.SnapshotId, ResolutionHeight = resolution.Height } });
        }
        private void DisplayCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (displayStatus?.Info == null) return;
                var data = new DataPackage(); data.SetText(Protocol.Encode(displayStatus)); Clipboard.SetContent(data);
                DisplayTrialText.Text = "Diagnóstico de la última consulta copiado; copiar no cambia la pantalla.";
            }
            catch (Exception ex) { DisplayTrialText.Text = "No se pudo copiar: " + ex.Message; }
        }
    }
}
