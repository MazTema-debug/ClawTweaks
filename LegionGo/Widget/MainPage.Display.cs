// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
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
        private bool displaySupported, displayResolutionSupported, displayRefreshSupported, displayBusy, renderingDisplay;
        private bool displayReadRequested = true;
        private long displayReadAt = -1;
        private readonly Stopwatch displayReadClock = Stopwatch.StartNew();
        private string displayPolledToken;
        private int displayPolls;
        private readonly DispatcherTimer displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private void InitializeDisplay()
        {
            SetDisplayChoices(null);
            Controls.SelectionChanged += async (s, e) => {
                if (e.OriginalSource != Controls) return;
                displayReadRequested = true;
                await RefreshDisplayIfNeeded();
            };
            Loaded += (s, e) => { displayReadRequested = true; };
            displayTimer.Tick += async (s, e) => await RefreshDisplayIfNeeded();
        }
        private void RequestDisplayRefresh()
        {
            displayReadRequested = true;
            _ = RefreshDisplayIfNeeded();
        }
        private async Task RefreshDisplayIfNeeded()
        {
            if (!pageLoaded || !StatusViewVisible || Controls.SelectedItem != DisplayTab || !displaySupported) {
                displayTimer.Stop(); return;
            }
            displayTimer.Start();
            bool oldTrial = displayStatus?.Pending == true && !displayStatus.RecoveryPending && displayPolls < 30;
            if (working || displayBusy || DisplayModesBox.IsDropDownOpen || DisplayRefreshBox.IsDropDownOpen
                || (!displayReadRequested && displayReadAt >= 0
                && displayReadClock.ElapsedMilliseconds - displayReadAt < (oldTrial ? 1000 : 5000))) return;
            displayReadRequested = false;
            if (oldTrial) displayPolls++;
            await DisplaySend(new Request { Operation = "display_status" });
        }
        private void RenderDisplayCapability(bool supported, bool resolutionSupported, bool refreshSupported)
        {
            if (supported && !displaySupported) displayReadRequested = true;
            displaySupported = supported;
            displayResolutionSupported = resolutionSupported;
            displayRefreshSupported = refreshSupported;
            if (!supported) {
                displayTimer.Stop(); displayStatus = null; ClearDisplayReading(); SetDisplayChoices(null);
                DisplayText.Text = "Consulta de pantalla sin confirmar. Comprueba la conexión y actualiza el helper.";
            }
            UpdateDisplayButtons();
            _ = RefreshDisplayIfNeeded();
        }
        private void UpdateDisplayButtons()
        {
            bool ready = displaySupported && !displayBusy;
            bool pending = displayStatus?.Pending == true;
            DisplayQuery.IsEnabled = ready;
            DisplayCopy.IsEnabled = displayStatus?.Info != null;
            DisplayModesBox.IsEnabled = ready && displayResolutionSupported && !pending
                && displayStatus?.Info?.CanChange == true && displayStatus.SnapshotId != null;
            DisplayRefreshBox.IsEnabled = ready && displayRefreshSupported && !pending
                && displayStatus?.Info?.CanChange == true && displayStatus.SnapshotId != null;
            DisplayRecover.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            DisplayRecover.IsEnabled = ready && pending && displayStatus.RecoveryPending;
        }
        private async Task DisplaySend(Request request)
        {
            if (working || displayBusy) {
                if (request.Operation == "display_status") displayReadRequested = true;
                return;
            }
            int view = viewGeneration;
            displayReadAt = displayReadClock.ElapsedMilliseconds;
            displayBusy = true; UpdateDisplayButtons();
            try
            {
                Response response;
                bool changing = request.Operation == "display_resolution" || request.Operation == "display_refresh_rate";
                if (changing) {
                    var previous = displayStatus?.Info;
                    // Refresh the IPC token and capabilities automatically; a native write is sent only once.
                    response = await PipeClient.Send(new Request { Operation = "display_status" });
                    if (!pageLoaded || view != viewGeneration) return;
                    var fresh = response.Display?.Info;
                    bool compatible = request.Operation == "display_resolution" ? response.SupportsDisplayResolution : response.SupportsDisplayRefreshRate;
                    if (response.Success && compatible && response.Display?.SnapshotId != null && !response.Display.Pending) {
                        if (previous == null || fresh == null || previous.DeviceName != fresh.DeviceName
                            || previous.Monitor != fresh.Monitor || previous.Orientation != fresh.Orientation) {
                            RenderDisplay(response.Display);
                            DisplayTrialText.Text = "La pantalla cambió; se actualizó la lectura. Selecciona de nuevo.";
                            return;
                        }
                        request.Display.SnapshotId = response.Display.SnapshotId;
                        response = await PipeClient.Send(request);
                    }
                }
                else response = await PipeClient.Send(request);
                if (!pageLoaded || view != viewGeneration) return;
                displaySupported = response.SupportsDisplay;
                displayResolutionSupported = response.SupportsDisplayResolution;
                displayRefreshSupported = response.SupportsDisplayRefreshRate;
                if (response.Display != null) RenderDisplay(response.Display);
                else {
                    displayStatus = null;
                    ClearDisplayReading(); SetDisplayChoices(null);
                    DisplayTrialText.Text = response.Message ?? "No se pudo leer la pantalla. Se reintentará automáticamente.";
                }
            }
            catch (Exception ex)
            {
                if (!pageLoaded || view != viewGeneration) return;
                DisplayTrialText.Text = ex.Message + " · Se reintentará la lectura automáticamente.";
                displayStatus = null;
                ClearDisplayReading(); SetDisplayChoices(null);
            }
            finally {
                displayBusy = false; UpdateDisplayButtons();
                if (displayReadRequested) _ = RefreshDisplayIfNeeded();
            }
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
                    : info.Current.Frequency + " Hz · se conservan al cambiar resolución";
                DisplayText.Text = info.DeviceName + " · " + info.Monitor + "\n" + info.Adapter
                    + "\nModo consultado: " + info.Current + " · orientación " + info.Orientation + "°"
                    + "\nPantallas activas: " + info.ActiveDisplays + " · consulta UTC: " + info.CapturedAtUtc
                    + (info.Restriction == null ? "" : "\n" + info.Restriction);
            }
            else { ClearDisplayReading(); DisplayText.Text = "Lectura de pantalla no disponible. Se reintentará automáticamente."; }
            SetDisplayChoices(value.Info);
            DisplayTrialText.Text = (value.Pending ? "Hay un cambio anterior pendiente. Espera su recuperación o pulsa Recuperar cuando esté disponible." : value.Message ?? "Sin cambios pendientes.")
                + (!displayResolutionSupported ? "\nActualiza el helper para usar el selector de resolución." : "")
                + (!displayRefreshSupported ? "\nActualiza el helper para cambiar entre 60 y 144 Hz." : "")
                + (value.Error == null ? "" : "\nDetalle: " + value.Error);
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
                var refreshItems = DisplayRefreshRate.Options(info).Select(x => new ComboBoxItem {
                    Content = x.Label, Tag = x, IsEnabled = x.IsAvailable }).ToArray();
                DisplayRefreshBox.ItemsSource = refreshItems;
                DisplayRefreshBox.SelectedItem = refreshItems.FirstOrDefault(item => ((DisplayRefreshRate)item.Tag).Frequency == info?.Current?.Frequency);
            }
            finally { renderingDisplay = false; }
        }
        private void ClearDisplayReading()
        {
            DisplayCurrentText.Text = "Resolución actual sin confirmar";
            DisplayRefreshText.Text = "Leyendo automáticamente resolución y frecuencia…";
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
        private async void DisplayRefreshBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (renderingDisplay || !pageLoaded || working || displayBusy || !displayRefreshSupported
                || displayStatus?.SnapshotId == null || displayStatus.Pending
                || !(DisplayRefreshBox.SelectedItem is ComboBoxItem item)
                || !(item.Tag is DisplayRefreshRate refreshRate) || !refreshRate.IsAvailable) return;
            if (displayStatus.Info?.Current?.Frequency == refreshRate.Frequency) return;
            await DisplaySend(new Request { Operation = "display_refresh_rate", Display = new DisplayRequest {
                SnapshotId = displayStatus.SnapshotId, RefreshRate = refreshRate.Frequency } });
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
