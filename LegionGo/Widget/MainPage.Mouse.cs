// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private StickMouseStatus mouseStatus;
        private bool mouseSettingsLoaded;

        private async void MouseToggle_Click(object sender, RoutedEventArgs e)
        {
            if (working || mouseStatus?.Supports != true) return;
            await Send(new Request { Operation = "mouse_set", Values = new[] { mouseStatus.Enabled ? 0 : 1,
                (int)Math.Round(MouseSensitivity.Value), (int)Math.Round(MouseDeadzone.Value) } });
        }

        private async void MouseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (working || mouseStatus?.Supports != true) return;
            await Send(new Request { Operation = "mouse_set", Values = new[] { mouseStatus.Enabled ? 1 : 0,
                (int)Math.Round(MouseSensitivity.Value), (int)Math.Round(MouseDeadzone.Value) } });
        }

        private void RenderMouse(StickMouseStatus value)
        {
            mouseStatus = value;
            bool supported = value?.Supports == true;
            MouseToggle.IsEnabled = supported && (value.Available || value.Enabled);
            MouseSettings.IsEnabled = supported;
            MouseSensitivity.IsEnabled = MouseDeadzone.IsEnabled = supported;
            MouseToggle.Content = value?.Enabled == true ? "Desactivar mouse con stick derecho" : "Activar mouse con stick derecho";
            if (!supported)
            { MouseText.Text = "Mouse sin confirmar. Comprueba la conexión y actualiza el helper."; return; }
            if (!mouseSettingsLoaded)
            {
                MouseSensitivity.Value = value.Sensitivity; MouseDeadzone.Value = value.Deadzone;
                mouseSettingsLoaded = true;
            }
            MouseText.Text = value.Message + "\nSensibilidad aplicada: " + value.Sensitivity + " · Zona muerta: " + value.Deadzone + " %"
                + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
        }
    }
}
