// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private readonly List<ComboBox> legionMappingEditors = new List<ComboBox>();
        private bool legionButtonsSupported;
        private int supportedLegionButtons;
        private FpsButtonKeysStatus fpsKeysStatus;

        private async void FpsKeysToggle_Click(object sender, RoutedEventArgs e)
        {
            if (working || fpsKeysStatus == null) return;
            await Send(new Request { Operation = "fps_keys_set", Values = new[] { fpsKeysStatus.Enabled ? 0 : 1 } });
        }

        private bool SupportsLegionSlot(int index) => legionButtonsSupported && (supportedLegionButtons & (1 << index)) != 0;
        private bool CanUseLegionMappings(ControllerLayout layout)
        {
            if (layout?.LegionMap == null) return true;
            for (int i = 0; i < layout.LegionMap.Length; i++)
                if (layout.LegionMap[i] != 0 && (i >= ControllerLayout.LegionLabels.Length || !SupportsLegionSlot(i))) return false;
            return true;
        }
        private string LegionMappingHeader(int index, int reserved = 0)
        {
            string suffix = (LegionButtonReport.SupportedControllerButtons & (1 << index)) == 0 ? " (pendiente de identificar)"
                : !SupportsLegionSlot(index) ? " (requiere helper compatible)"
                : (reserved & (1 << index)) != 0 ? " (Game Bar tiene prioridad)" : "";
            return ControllerLayout.LegionLabels[index] + " →" + suffix;
        }

        private void InitializeLegionMappings()
        {
            if (legionMappingEditors.Count != 0) return;
            for (int i = 0; i < ControllerLayout.LegionLabels.Length; i++)
            {
                var box = new ComboBox { Header = LegionMappingHeader(i),
                    Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = SupportsLegionSlot(i) };
                box.Items.Add("Sin asignación");
                foreach (string label in ControllerLayout.Labels) box.Items.Add(label);
                box.SelectedIndex = 0;
                box.SelectionChanged += (s, e) => UpdateProfileDraftNotice();
                legionMappingEditors.Add(box); LegionButtonMappings.Children.Add(box);
            }
            if (LegionCaptureChoice.Items.Count == 0)
                foreach (string label in ControllerLayout.LegionLabels) LegionCaptureChoice.Items.Add(label);
            if (LegionCaptureChoice.SelectedIndex < 0) LegionCaptureChoice.SelectedIndex = 2;
        }

        private void LoadLegionMappings(ControllerLayout layout)
        {
            InitializeLegionMappings();
            var values = layout?.LegionMap;
            for (int i = 0; i < legionMappingEditors.Count; i++)
                legionMappingEditors[i].SelectedIndex = values == null ? 0 : Array.IndexOf(ControllerLayout.Buttons, values[i]) + 1;
        }

        private int[] ReadLegionMappings()
        {
            if (legionMappingEditors.Count == 0) return null;
            var values = new int[ControllerLayout.LegionLabels.Length];
            bool any = false;
            for (int i = 0; i < values.Length; i++)
            {
                int selected = legionMappingEditors[i].SelectedIndex;
                values[i] = selected <= 0 ? 0 : ControllerLayout.Buttons[selected - 1];
                any |= values[i] != 0;
            }
            return any ? values : null;
        }

        private async void LegionCaptureStart_Click(object sender, RoutedEventArgs e)
        {
            if (working || controllerPreparing || !legionButtonsSupported || LegionCaptureChoice.SelectedIndex < 0) return;
            await Send(new Request { Operation = "legion_capture_start", Values = new[] { LegionCaptureChoice.SelectedIndex, 30 } });
        }
        private async void LegionCaptureCancel_Click(object sender, RoutedEventArgs e)
        {
            if (working || !legionButtonsSupported) return;
            await Send(new Request { Operation = "legion_capture_cancel" });
        }

        private void RenderLegionButtons(LegionShortcutStatus value)
        {
            fpsKeysStatus = value?.FpsKeys;
            FpsKeysToggle.IsEnabled = fpsKeysStatus != null && (fpsKeysStatus.Enabled
                || (lastControllerStatus?.Active != true && !controllerPreparing));
            FpsKeysToggle.Content = fpsKeysStatus?.Enabled == true ? "Desactivar botones FPS" : "Activar Y3 → Insert y M3 → Escape";
            FpsKeysText.Text = fpsKeysStatus == null ? "Botones FPS sin confirmar; requiere un helper compatible."
                : fpsKeysStatus.Message + "\nInsert enviados: " + fpsKeysStatus.InsertSent + " · Escape enviados: " + fpsKeysStatus.EscapeSent
                    + (string.IsNullOrEmpty(fpsKeysStatus.Error) ? "" : "\n" + fpsKeysStatus.Error);
            legionButtonsSupported = value?.SupportsButtonMapping == true;
            // Older helpers advertise only L/R. Unknown/new helper bits do not
            // enable controls that this widget has not identified either.
            supportedLegionButtons = legionButtonsSupported ? ((value.SupportedButtons == 0 ? 3 : value.SupportedButtons)
                & LegionButtonReport.SupportedControllerButtons) : 0;
            InitializeLegionMappings();
            for (int i = 0; i < legionMappingEditors.Count; i++)
            {
                legionMappingEditors[i].IsEnabled = SupportsLegionSlot(i);
                legionMappingEditors[i].Header = LegionMappingHeader(i, (value?.ReservedButtons ?? 0) & 3);
            }
            var capture = value?.Capture;
            bool active = capture?.Active == true;
            bool preparing = controllerPreparing || value?.PreparationPaused == true;
            LegionCaptureStart.IsEnabled = legionButtonsSupported && !active && !preparing;
            LegionCaptureCancel.IsEnabled = legionButtonsSupported && active;
            LegionCaptureChoice.IsEnabled = legionButtonsSupported && !active && !preparing;
            if (!legionButtonsSupported)
            { LegionCaptureText.Text = "Identificación y asignación de botones sin confirmar. Comprueba la conexión y actualiza el helper."; return; }
            string available = string.Join(", ", ControllerLayout.LegionLabels.Where((x, i) => SupportsLegionSlot(i)));
            string pending = string.Join(", ", ControllerLayout.LegionLabels.Where((x, i) => (LegionButtonReport.SupportedControllerButtons & (1 << i)) == 0));
            string note = "Los botones Legion se guardan por perfil y se usan con el mando virtual. El atajo global de Game Bar reserva solo sus L/R y tiene prioridad."
                + "\nAsignaciones disponibles con este helper: " + (available.Length == 0 ? "sin confirmar" : available) + "."
                + (pending.Length == 0 ? "" : "\nPendientes de identificar: " + pending + ". Sus asignaciones están desactivadas.")
                + "\nLa captura voluntaria pausa Game Bar y las asignaciones Legion durante 30 segundos. Primero deja todo libre un segundo; después pulsa y suelta solo el botón elegido."
                + "\nAl terminar, suelta todos los botones para rearmar. No se cambia el firmware ni se guardan informes brutos.";
            if (capture == null) { LegionCaptureText.Text = note; return; }
            string selected = capture.ButtonIndex >= 0 && capture.ButtonIndex < ControllerLayout.LegionLabels.Length
                ? ControllerLayout.LegionLabels[capture.ButtonIndex] : "sin identificar";
            string header = capture.Header == "04 00 A1" || capture.Header == "04 3C 74" ? capture.Header : "sin informe compatible";
            LegionCaptureText.Text = (active ? "Capturando " : "Resultado para ") + selected
                + (active ? " · quedan " + capture.SecondsRemaining + " s" : "")
                + "\n" + capture.Message + "\nCabecera: " + header
                + (capture.Samples == 0 ? "" : " · índice candidato: " + capture.CandidateOffset
                    + "\nReposo: " + capture.BaselineBits.ToString("X2") + " · actual: " + capture.CurrentBits.ToString("X2")
                    + " · cambiaron: " + capture.ChangedBits.ToString("X2")
                    + "\nSubidas: " + capture.PressedBits.ToString("X2") + " · bajadas: " + capture.ReleasedBits.ToString("X2")
                    + " · transiciones: " + capture.Transitions)
                + "\n\n" + note;
        }
    }
}
