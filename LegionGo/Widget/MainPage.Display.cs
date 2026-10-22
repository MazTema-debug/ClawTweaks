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
        private bool displaySupported, displayBusy;
        private string displayPolledToken;
        private int displayPolls;
        private readonly DispatcherTimer displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private void InitializeDisplay()
        {
            Loaded += (s, e) => { if (displayStatus?.Pending == true) displayTimer.Start(); };
            displayTimer.Tick += async (s, e) => {
                if (!pageLoaded || displayStatus?.Pending != true) { displayTimer.Stop(); return; }
                if (!working && !displayBusy)
                {
                    if (++displayPolls > 30) { displayTimer.Stop(); DisplayTrialText.Text = "El resultado no se confirmó a tiempo. Consulta de nuevo; el respaldo pendiente se conserva."; return; }
                    await DisplaySend(new Request { Operation = "display_status" });
                }
            };
        }
        private void RenderDisplayCapability(bool supported)
        {
            displaySupported = supported;
            if (!supported) { displayTimer.Stop(); displayStatus = null; DisplayText.Text = "Consulta de pantalla sin confirmar. Comprueba la conexión y actualiza el helper."; }
            UpdateDisplayButtons();
        }
        private void UpdateDisplayButtons()
        {
            bool ready = displaySupported && !displayBusy;
            bool pending = displayStatus?.Pending == true;
            DisplayQuery.IsEnabled = ready;
            DisplayCopy.IsEnabled = displayStatus?.Info != null;
            DisplayModesBox.IsEnabled = ready && !pending && displayStatus?.Info?.CanChange == true;
            DisplayPreview.IsEnabled = DisplayModesBox.IsEnabled && displayStatus.SnapshotId != null;
            DisplayConfirm.IsEnabled = ready && pending && displayStatus.Phase == "AwaitingConfirmation" && displayStatus.RemainingSeconds > 0;
            DisplayRevert.IsEnabled = ready && pending && !displayStatus.RecoveryPending;
            DisplayRecover.IsEnabled = ready && pending && displayStatus.RecoveryPending;
        }
        private async Task DisplaySend(Request request)
        {
            if (working || displayBusy) return;
            displayBusy = true; UpdateDisplayButtons();
            try
            {
                var response = await PipeClient.Send(request);
                displaySupported = response.SupportsDisplay;
                if (response.Display != null) RenderDisplay(response.Display);
                else DisplayTrialText.Text = response.Message ?? "No se confirmó el estado de pantalla. Consulta de nuevo.";
                if (!response.Success) displayTimer.Stop();
            }
            catch (Exception ex)
            {
                displayTimer.Stop();
                // A lost connection never confirms or extends a trial. Its worker owns the deadline.
                DisplayTrialText.Text = ex.Message + " · No se confirmó la prueba. El supervisor conserva su plazo de recuperación.";
                displayStatus = null;
            }
            finally { displayBusy = false; UpdateDisplayButtons(); }
        }
        private void RenderDisplay(DisplayStatus value)
        {
            var selected = DisplayModesBox.SelectedItem as DisplayMode;
            if (value.TrialToken != displayPolledToken) { displayPolledToken = value.TrialToken; displayPolls = 0; }
            displayStatus = value;
            if (value.Info != null)
            {
                var info = value.Info;
                DisplayText.Text = info.DeviceName + " · " + info.Monitor + "\n" + info.Adapter
                    + "\nModo consultado: " + info.Current + " · orientación " + info.Orientation + "°"
                    + "\nPantallas activas: " + info.ActiveDisplays + " · consulta UTC: " + info.CapturedAtUtc
                    + (info.Restriction == null ? "" : "\n" + info.Restriction);
                DisplayModesBox.ItemsSource = info.Modes;
                DisplayModesBox.SelectedItem = info.Modes?.FirstOrDefault(x => x.Same(selected))
                    ?? info.Modes?.FirstOrDefault(x => x.Same(info.Current));
            }
            DisplayTrialText.Text = (value.Message ?? "Sin prueba activa.")
                + (value.Phase == "AwaitingConfirmation" ? "\nQuedan como máximo " + value.RemainingSeconds + " s." : "")
                + (value.Error == null ? "" : "\nDetalle de la última prueba: " + value.Error);
            if (value.Pending && !value.RecoveryPending && pageLoaded) displayTimer.Start(); else displayTimer.Stop();
            UpdateDisplayButtons();
        }
        private async void DisplayQuery_Click(object sender, RoutedEventArgs e) => await DisplaySend(new Request { Operation = "display_status" });
        private async void DisplayRecover_Click(object sender, RoutedEventArgs e)
        {
            displayPolledToken = null;
            await DisplaySend(new Request { Operation = "display_recover" });
        }
        private async void DisplayPreview_Click(object sender, RoutedEventArgs e)
        {
            if (working || displayBusy || displayStatus?.SnapshotId == null || !(DisplayModesBox.SelectedItem is DisplayMode mode)) return;
            var request = new DisplayRequest { SnapshotId = displayStatus.SnapshotId, Mode = mode.Copy() };
            try
            {
                var dialog = new ContentDialog { Title = "Probar " + mode,
                    Content = "Cierra los juegos antes de continuar. La pantalla puede apagarse brevemente. Tendrás 15 segundos para confirmar el modo en este panel; sin confirmación se intentará recuperar el anterior. El cambio no se guarda para el reinicio.",
                    PrimaryButtonText = "Iniciar prueba", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await DisplaySend(new Request { Operation = "display_preview", Display = request });
            }
            catch (Exception ex) { DisplayTrialText.Text = ex.Message; }
        }
        private async void DisplayConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (displayStatus?.TrialToken != null) await DisplaySend(new Request { Operation = "display_confirm", Display = new DisplayRequest { TrialToken = displayStatus.TrialToken } });
        }
        private async void DisplayRevert_Click(object sender, RoutedEventArgs e)
        {
            if (displayStatus?.TrialToken != null) await DisplaySend(new Request { Operation = "display_revert", Display = new DisplayRequest { TrialToken = displayStatus.TrialToken } });
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
