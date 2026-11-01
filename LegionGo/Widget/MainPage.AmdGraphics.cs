// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool supportsAmdGraphics;
        private AmdGraphicsStatus amdGraphicsStatus;
        private void RenderAmdGraphicsCapability(bool supported)
        {
            supportsAmdGraphics = supported;
            if (!supported) {
                amdGraphicsStatus = null;
                AmdGraphicsText.Text = "Actualiza el helper y consulta el equipo para acceder a los ajustes AMD.";
            }
            AmdGraphicsInspect.IsEnabled = supported;
            var state = amdGraphicsStatus;
            bool complete = supported && state?.Available == true && state.GpuCount == 1 && state.DisplayCount == 1
                && state.Revision != null && state.Values != null
                && state.Values.All(v => v.Supported.HasValue && (v.Supported == false || v.Value.HasValue && v.Error == null));
            bool editable = complete && !state.RecoveryPending;
            var rsr = state?.Find("rsr"); var afmf = state?.Find("afmf"); var sharpness = state?.Find("rsr_sharpness");
            AmdRsrEnable.IsEnabled = editable && rsr?.Supported == true && rsr.Value == 0;
            AmdRsrDisable.IsEnabled = editable && rsr?.Supported == true && rsr.Value == 1;
            AmdAfmfEnable.IsEnabled = editable && afmf?.Supported == true && afmf.Value == 0;
            AmdAfmfDisable.IsEnabled = editable && afmf?.Supported == true && afmf.Value == 1;
            bool range = sharpness?.Supported == true && sharpness.Minimum.HasValue && sharpness.Maximum.HasValue
                && sharpness.Step > 0 && sharpness.Minimum < sharpness.Maximum;
            AmdSharpness.IsEnabled = editable && range; AmdSharpnessApply.IsEnabled = editable && range;
            AmdGraphicsRestore.IsEnabled = complete && state.HasOriginals;
        }
        private void RenderAmdGraphics(AmdGraphicsStatus state)
        {
            amdGraphicsStatus = state;
            if (state == null) { AmdGraphicsText.Text = "Lectura AMD sin confirmar. Vuelve a consultar antes de aplicar."; }
            else {
                AmdGraphicsText.Text = "RSR: " + AmdValueText(state.Find("rsr")) + " · AFMF: " + AmdValueText(state.Find("afmf"))
                    + "\nADLX: " + (state.RuntimeVersion ?? "N/D") + ". Actividad dentro del juego: sin comprobar.";
                if (DateTime.TryParse(state.CapturedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var captured))
                    AmdGraphicsText.Text += "\nLectura: " + captured.ToLocalTime().ToString("HH:mm:ss") + ". Consultar renueva el estado.";
                var sharpness = state.Find("rsr_sharpness");
                if (sharpness?.Value != null && sharpness.Minimum.HasValue && sharpness.Maximum.HasValue && sharpness.Step > 0
                    && sharpness.Minimum < sharpness.Maximum && sharpness.Value >= sharpness.Minimum && sharpness.Value <= sharpness.Maximum) {
                    // Expand first to avoid inconsistent Slider limits when a driver changes its range.
                    AmdSharpness.Minimum = Math.Min(AmdSharpness.Minimum, sharpness.Minimum.Value);
                    AmdSharpness.Maximum = Math.Max(AmdSharpness.Maximum, sharpness.Maximum.Value);
                    AmdSharpness.Value = sharpness.Value.Value;
                    AmdSharpness.Minimum = sharpness.Minimum.Value; AmdSharpness.Maximum = sharpness.Maximum.Value;
                    AmdSharpness.StepFrequency = sharpness.Step.Value;
                }
                AmdGraphicsNotice.Text = state.Message;
            }
            RenderAmdGraphicsCapability(supportsAmdGraphics);
        }
        private static string AmdValueText(AmdGraphicsValue value) => value?.Supported == false ? "no compatible"
            : value?.Value == 1 ? "habilitado" : value?.Value == 0 ? "deshabilitado" : "sin confirmar";
        private async void AmdGraphicsInspect_Click(object sender, RoutedEventArgs e)
        {
            if (working || !supportsAmdGraphics) return;
            var response = await Send(new Request { Operation = "amd_graphics_status" });
            AmdGraphicsNotice.Text = response?.Message ?? "No se obtuvo respuesta AMD.";
        }
        private async Task ApplyAmdGraphics(string key, int value)
        {
            if (working || !supportsAmdGraphics || amdGraphicsStatus?.Revision == null) return;
            var response = await Send(new Request { Operation = "amd_graphics_apply",
                AmdGraphics = new AmdGraphicsRequest { Revision = amdGraphicsStatus.Revision, Key = key, Value = value } });
            AmdGraphicsNotice.Text = response?.Message ?? "Resultado AMD sin confirmar; vuelve a consultar.";
        }
        private async void AmdRsrEnable_Click(object sender, RoutedEventArgs e) => await ApplyAmdGraphics("rsr", 1);
        private async void AmdRsrDisable_Click(object sender, RoutedEventArgs e) => await ApplyAmdGraphics("rsr", 0);
        private async void AmdAfmfEnable_Click(object sender, RoutedEventArgs e) => await ApplyAmdGraphics("afmf", 1);
        private async void AmdAfmfDisable_Click(object sender, RoutedEventArgs e) => await ApplyAmdGraphics("afmf", 0);
        private async void AmdSharpnessApply_Click(object sender, RoutedEventArgs e) => await ApplyAmdGraphics("rsr_sharpness", (int)Math.Round(AmdSharpness.Value));
        private async void AmdGraphicsRestore_Click(object sender, RoutedEventArgs e)
        {
            if (working || amdGraphicsStatus?.Revision == null) return;
            var response = await Send(new Request { Operation = "amd_graphics_restore",
                AmdGraphics = new AmdGraphicsRequest { Revision = amdGraphicsStatus.Revision } });
            AmdGraphicsNotice.Text = response?.Message ?? "Restauración AMD sin confirmar; vuelve a consultar.";
        }
    }
}
