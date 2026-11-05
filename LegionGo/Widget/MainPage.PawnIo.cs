// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool supportsPawnIo, pawnIoDialogBusy;
        private PawnIoStatus pawnIoStatus;

        private void RenderPawnIo(bool supported, PawnIoStatus value)
        {
            supportsPawnIo = supported;
            pawnIoStatus = supported ? value : null;
            PawnIoInspect.IsEnabled = supported && value?.Busy != true && !pawnIoDialogBusy;
            PawnIoInstall.IsEnabled = supported && value?.CanInstall == true && !value.Busy && !pawnIoDialogBusy;
            PawnIoInstall.Content = value?.Installed == true ? "Actualizar PawnIO" : "Instalar PawnIO";
            if (!supported) {
                PawnIoText.Text = "Comprueba la conexión y actualiza el helper para consultar e instalar PawnIO.";
                return;
            }
            string captured = "";
            if (DateTime.TryParse(value?.CapturedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                captured = "\nComprobación: " + time.ToLocalTime().ToString("HH:mm:ss") + ". Pulsa Comprobar para renovarla.";
            PawnIoText.Text = (value?.Message ?? "PawnIO sin comprobar.") + captured;
        }
        private async void PawnIoInspect_Click(object sender, RoutedEventArgs e)
        {
            if (working || pawnIoDialogBusy || !supportsPawnIo || pawnIoStatus?.Busy == true) return;
            await Send(new Request { Operation = "pawnio_status" });
        }
        private async void PawnIoInstall_Click(object sender, RoutedEventArgs e)
        {
            if (working || pawnIoDialogBusy || !supportsPawnIo || pawnIoStatus?.CanInstall != true || pawnIoStatus.Busy) return;
            int view = viewGeneration;
            string notice = null;
            pawnIoDialogBusy = true;
            RenderPawnIo(supportsPawnIo, pawnIoStatus);
            try {
                var dialog = new ContentDialog {
                    Title = "Instalar PawnIO",
                    Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = "Se descargará PawnIO 2.2.0 desde su sitio oficial y se abrirá su asistente. Instala un controlador para todo el equipo y puede pedir permisos de administrador.\n\nLas frecuencias CPU aún no están disponibles en esta versión." },
                    PrimaryButtonText = "Descargar e instalar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && pageLoaded && view == viewGeneration) {
                    var response = await Send(new Request { Operation = "pawnio_install" });
                    if (response == null) notice = "No se solicitó la instalación: hay otra consulta en curso o se cerró la vista. Vuelve a pulsar Instalar cuando termine.";
                }
            }
            catch (Exception ex) { notice = "No se pudo abrir el asistente: " + ex.Message; }
            finally {
                pawnIoDialogBusy = false;
                if (pageLoaded && view == viewGeneration) {
                    RenderPawnIo(supportsPawnIo, pawnIoStatus);
                    if (notice != null) PawnIoText.Text = notice;
                }
            }
        }
        private async void PawnIoWebsite_Click(object sender, RoutedEventArgs e)
        {
            try {
                var uri = new Uri(PawnIoPackage.Website);
                bool accepted = widget != null ? await widget.LaunchUriAsync(uri) : await Launcher.LaunchUriAsync(uri);
                if (!accepted) PawnIoText.Text = "Windows no pudo abrir el sitio oficial de PawnIO.";
            }
            catch (Exception ex) { PawnIoText.Text = "No se pudo abrir el sitio oficial: " + ex.Message; }
        }
    }
}
