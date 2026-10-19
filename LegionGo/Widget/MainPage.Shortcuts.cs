// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool shortcutLoaded;
        private async void ShortcutApply_Click(object sender, RoutedEventArgs e)
        {
            if (ShortcutMode.SelectedIndex >= 0)
                await Send(new Request { Operation = "shortcut_set", Values = new[] { ShortcutMode.SelectedIndex } });
        }
        private void RenderShortcuts(LegionShortcutStatus value, bool refreshSelection = false)
        {
            RenderLegionButtons(value);
            ShortcutApply.IsEnabled = value != null;
            if (value == null)
            {
                ShortcutText.Text = "Estado del atajo sin confirmar. Comprueba la conexión y que el helper sea 0.1.21.";
                return;
            }
            if (!shortcutLoaded || refreshSelection) { ShortcutMode.SelectedIndex = (int)value.Mode; shortcutLoaded = true; }
            string[] names = { "Desactivado", "Legion L", "Legion R", "Legion L + Legion R" };
            int mode = (int)value.Mode;
            ShortcutText.Text = "Guardado: " + (mode >= 0 && mode < names.Length ? names[mode] : "Desconocido")
                + "\n" + value.Message;
            if (value.Mode != LegionShortcutMode.Disabled)
                ShortcutText.Text += "\nInformes HID: " + value.Reports + " · Win+G enviados: " + value.Activations
                    + "\nLegion L: " + (value.Connected ? value.Left ? "pulsado" : "libre" : "—")
                    + " · Legion R: " + (value.Connected ? value.Right ? "pulsado" : "libre" : "—");
            if (DateTime.TryParse(value.LastActivationUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last))
                ShortcutText.Text += "\nÚltimo Win+G: " + last.ToLocalTime().ToString("HH:mm:ss");
            if (!string.IsNullOrEmpty(value.Error)) ShortcutText.Text += "\n" + value.Error;
            if (value.Mode != LegionShortcutMode.Disabled && !string.IsNullOrEmpty(value.Diagnostic))
                ShortcutText.Text += "\n" + value.Diagnostic;
        }
    }
}
