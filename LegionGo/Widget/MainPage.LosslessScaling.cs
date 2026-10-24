// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.System;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool supportsLosslessScaling, losslessLaunching;
        private LosslessScalingStatus losslessStatus;
        private void RenderLosslessCapability(bool supported)
        {
            supportsLosslessScaling = supported;
            LosslessInspect.IsEnabled = supported && !losslessLaunching;
            LosslessOpen.IsEnabled = supported && losslessStatus?.Installed == true && !losslessLaunching;
            if (!supported) {
                losslessStatus = null;
                LosslessText.Text = "Comprueba la conexión y actualiza el helper para consultar Lossless Scaling.";
            }
        }
        private void RenderLossless(LosslessScalingStatus value)
        {
            if (value == null) return;
            losslessStatus = value;
            string captured = "";
            if (DateTime.TryParse(value.CapturedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                captured = "\nLectura: " + time.ToLocalTime().ToString("HH:mm:ss") + ". Pulsa Consultar para renovarla.";
            LosslessText.Text = value.Message + (value.Installed ? "\nVersión del archivo: " + (value.FileVersion ?? "N/D") : "") + captured;
            RenderLosslessCapability(supportsLosslessScaling);
        }
        private async void LosslessInspect_Click(object sender, RoutedEventArgs e)
        {
            if (working || losslessLaunching || !supportsLosslessScaling) return;
            LosslessActionText.Text = "";
            var response = await Send(new Request { Operation = "lossless_status" });
            if (response?.Success != true) {
                losslessStatus = null; LosslessOpen.IsEnabled = false;
                LosslessText.Text = "Detección sin confirmar. Vuelve a consultar.";
            }
        }
        private async void LosslessOpen_Click(object sender, RoutedEventArgs e)
        {
            if (working || losslessLaunching || !supportsLosslessScaling) return;
            losslessLaunching = true;
            RenderLosslessCapability(supportsLosslessScaling);
            LosslessActionText.Text = "Comprobando la instalación…";
            try {
                // Recheck on this explicit click; an old status cannot authorize
                // opening a stale installation. Launch from UWP, never elevated.
                var response = await Send(new Request { Operation = "lossless_status" });
                if (response?.Success != true || response.LosslessScaling?.Installed != true) {
                    losslessStatus = null;
                    LosslessActionText.Text = "No se solicitó la apertura: instalación sin confirmar."; return;
                }
                bool accepted = await Launcher.LaunchUriAsync(new Uri(LosslessScalingAccess.LaunchUri));
                LosslessActionText.Text = accepted
                    ? "Apertura solicitada a Steam. Pulsa Consultar para confirmar si Lossless Scaling está en ejecución."
                    : "Windows no pudo abrir el enlace de Steam. Abre Lossless Scaling desde tu biblioteca.";
            }
            catch (Exception ex) { LosslessActionText.Text = "No se pudo solicitar la apertura: " + ex.Message; }
            finally { losslessLaunching = false; RenderLosslessCapability(supportsLosslessScaling); }
        }
    }
}
