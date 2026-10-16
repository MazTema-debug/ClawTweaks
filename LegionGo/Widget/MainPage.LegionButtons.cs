// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private readonly List<ComboBox> legionMappingEditors = new List<ComboBox>();
        private bool legionButtonsSupported;

        private void InitializeLegionMappings()
        {
            if (legionMappingEditors.Count != 0) return;
            for (int i = 0; i < ControllerLayout.LegionLabels.Length; i++)
            {
                var box = new ComboBox { Header = ControllerLayout.LegionLabels[i] + " →" + (i < 2 ? "" : " (pendiente de identificar)"),
                    Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = i < 2 && legionButtonsSupported };
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
            legionButtonsSupported = value?.SupportsButtonMapping == true;
            InitializeLegionMappings();
            for (int i = 0; i < legionMappingEditors.Count; i++)
            {
                legionMappingEditors[i].IsEnabled = i < 2 && legionButtonsSupported;
                legionMappingEditors[i].Header = ControllerLayout.LegionLabels[i] + " →" + (i >= 2 ? " (pendiente de identificar)"
                    : ((value?.ReservedButtons ?? 0) & (1 << i)) != 0 ? " (Game Bar tiene prioridad)" : "");
            }
            var capture = value?.Capture;
            bool active = capture?.Active == true;
            bool preparing = controllerPreparing || value?.PreparationPaused == true;
            LegionCaptureStart.IsEnabled = legionButtonsSupported && !active && !preparing;
            LegionCaptureCancel.IsEnabled = legionButtonsSupported && active;
            LegionCaptureChoice.IsEnabled = legionButtonsSupported && !active && !preparing;
            if (!legionButtonsSupported)
            { LegionCaptureText.Text = "Identificación y asignación de botones sin confirmar. Comprueba la conexión y actualiza el helper."; return; }
            string note = "Legion L/R se guardan por perfil y se usan con el mando virtual. El atajo global de Game Bar reserva sus botones y tiene prioridad."
                + "\nY1, Y2, Y3, M2 y M3 siguen sin bit confirmado; sus asignaciones están desactivadas."
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
