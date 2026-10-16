// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private LegionSpaceStatus legionSpaceStatus;
        private bool spaceRestoreDialog;
        private static string DescribeSpace(LegionSpaceState value)
        {
            if (value == null) return "Sin confirmar";
            string startup = value.StartMode == 4 ? "deshabilitado" : value.StartMode == 3 ? "manual"
                : value.DelayedAutoStart ? "automático con retraso" : "automático";
            return (value.Running ? "En ejecución" : "Detenido") + " · Inicio " + startup;
        }
        private void RenderLegionSpace(LegionSpaceStatus value)
        {
            legionSpaceStatus = value;
            SpaceDisable.IsEnabled = SpaceStart.IsEnabled = value?.CanChange == true;
            SpaceRestore.IsEnabled = value?.CanChange == true && value.Original != null;
            SpaceStatusText.Text = value == null ? "Estado sin confirmar. Actualiza el helper para usar este control."
                : DescribeSpace(value.State) + (value.Original == null ? "\nSin estado original guardado." : "\nGuardado: " + DescribeSpace(value.Original))
                    + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
            if (DateTime.TryParse(value?.CapturedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time))
                SpaceStatusText.Text += "\nConsultado: " + time.ToLocalTime().ToString("HH:mm:ss");
        }
        private async void SpaceRefresh_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "space_status" });
        private async void SpaceDisable_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "space_disable" });
        private async void SpaceStart_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "space_start" });
        private async void SpaceRestore_Click(object sender, RoutedEventArgs e)
        {
            if (legionSpaceStatus?.Original == null || spaceRestoreDialog) return;
            spaceRestoreDialog = true;
            try
            {
                var dialog = new ContentDialog { Title = "Restaurar Legion Space", Content = "Se recuperará: " + DescribeSpace(legionSpaceStatus.Original),
                    PrimaryButtonText = "Restaurar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Send(new Request { Operation = "space_restore" });
            }
            catch (Exception ex) { StatusText.Text = "No se pudo restaurar Legion Space: " + ex.Message; }
            finally { spaceRestoreDialog = false; }
        }
    }
}
