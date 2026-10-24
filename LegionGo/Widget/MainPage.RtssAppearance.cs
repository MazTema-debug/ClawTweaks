// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private const string RtssAppearanceKey = "RtssAppearance.v1";
        private bool rtssAppearanceReady, optiScalerSupported;
        private RtssAppearance rtssSavedAppearance = new RtssAppearance();
        private readonly ObservableCollection<RtssMetricChoice> rtssMetricChoices = new ObservableCollection<RtssMetricChoice>();
        private string rtssAppearanceLoadError;
        private string rtssAppearanceApplyError;
        private bool rtssAppearanceSaving, rtssAppearanceDialogOpen;
        private sealed class RtssMetricChoice
        {
            public RtssMetric Metric { get; set; }
            public string Label => RtssAppearance.Label(Metric);
        }

        private void InitializeRtssAppearance()
        {
            try
            {
                object raw;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(RtssAppearanceKey, out raw))
                {
                    if (!(raw is string text)) throw new InvalidOperationException("Formato de preferencia incompatible.");
                    rtssSavedAppearance = RtssAppearance.Decode(text);
                }
            }
            catch { rtssAppearanceLoadError = "No se cargó la apariencia guardada. Se conserva la preferencia anterior hasta que guardes explícitamente."; }
            RtssMetricsList.ItemsSource = rtssMetricChoices;
            RtssMetricAddBox.ItemsSource = Enum.GetValues(typeof(RtssMetric)).Cast<RtssMetric>()
                .Select(x => new RtssMetricChoice { Metric = x }).ToArray();
            RtssMetricAddBox.SelectedIndex = 0;
            RtssLevelBox.SelectedIndex = (int)rtssSavedAppearance.Level;
            RtssOrientationBox.SelectedIndex = (int)rtssSavedAppearance.Orientation;
            RtssScaleBox.SelectedIndex = Array.IndexOf(new[] { 75, 100, 125, 150 }, rtssSavedAppearance.Scale);
            foreach (var metric in rtssSavedAppearance.Metrics) rtssMetricChoices.Add(new RtssMetricChoice { Metric = metric });
            rtssAppearanceReady = true;
            UpdateRtssDraft();
        }
        private RtssAppearance ReadRtssDraft() => new RtssAppearance {
            Level = (RtssLevel)RtssLevelBox.SelectedIndex, Orientation = (RtssOrientation)RtssOrientationBox.SelectedIndex,
            Scale = new[] { 75, 100, 125, 150 }[RtssScaleBox.SelectedIndex],
            Metrics = rtssMetricChoices.Select(x => x.Metric).ToArray() };
        private void RtssDraft_Changed(object sender, SelectionChangedEventArgs e) { if (rtssAppearanceReady) UpdateRtssDraft(); }
        private void UpdateRtssDraft()
        {
            RtssCustomEditor.Visibility = RtssLevelBox.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
            try
            {
                var draft = ReadRtssDraft(); draft.Validate();
                RtssPreview.Text = RtssOverlayText.Preview(draft);
                RtssPreview.FontSize = 12 * draft.Scale / 100.0;
                RtssDraftDescription.Text = "Borrador: " + draft.Describe();
                RtssSavedDescription.Text = rtssAppearanceLoadError == null ? "Guardada: " + rtssSavedAppearance.Describe()
                    : "Guardada: no se pudo leer. La preferencia anterior se conserva.";
                RtssAppliedDescription.Text = rtssStatus?.Appearance == null ? "Apariencia en el helper: sin confirmar."
                    : (rtssStatus.Enabled ? "Configurada para publicar: " : "Overlay apagado. Apariencia en el helper: ") + rtssStatus.Appearance.Describe();
                RtssAppearanceText.Text = rtssAppearanceLoadError ?? (draft.Encode() == rtssSavedAppearance.Encode()
                    ? "Apariencia guardada. La activación del overlay es manual."
                    : "Cambios sin guardar. Activar usará la última apariencia guardada.");
                if (rtssStatus?.Appearance?.Encode() == rtssSavedAppearance.Encode()) rtssAppearanceApplyError = null;
                if (rtssAppearanceApplyError != null) RtssAppearanceText.Text += "\n" + rtssAppearanceApplyError;
                UpdateRtssAppearanceButtons();
                RtssEnable.Content = draft.Encode() != rtssSavedAppearance.Encode() ? "Activar y elegir apariencia…" : "Activar apariencia guardada";
            }
            catch (Exception ex) { RtssPreview.Text = "Vista previa no disponible."; RtssAppearanceText.Text = ex.Message; RtssEnable.IsEnabled = false; }
        }
        private void UpdateRtssAppearanceButtons()
        {
            RtssSaveAppearance.IsEnabled = rtssStatus?.SupportsAppearance == true && !rtssAppearanceSaving;
            RtssEnable.IsEnabled = rtssStatus?.Supports == true && !rtssStatus.Enabled && !rtssStatus.CleanupPending
                && rtssAppearanceLoadError == null && !rtssAppearanceSaving;
        }
        private void MoveRtssMetric(int offset)
        {
            int index = RtssMetricsList.SelectedIndex, target = index + offset;
            if (index < 0 || target < 0 || target >= rtssMetricChoices.Count) return;
            rtssMetricChoices.Move(index, target); RtssMetricsList.SelectedIndex = target; UpdateRtssDraft();
        }
        private void RtssMetricUp_Click(object sender, RoutedEventArgs e) => MoveRtssMetric(-1);
        private void RtssMetricDown_Click(object sender, RoutedEventArgs e) => MoveRtssMetric(1);
        private void RtssMetricRemove_Click(object sender, RoutedEventArgs e)
        {
            int index = RtssMetricsList.SelectedIndex;
            if (index < 0) return;
            if (rtssMetricChoices.Count <= 1) { RtssAppearanceText.Text = "Conserva al menos una métrica."; return; }
            rtssMetricChoices.RemoveAt(index); RtssMetricsList.SelectedIndex = Math.Min(index, rtssMetricChoices.Count - 1); UpdateRtssDraft();
        }
        private void RtssMetricAdd_Click(object sender, RoutedEventArgs e)
        {
            var choice = RtssMetricAddBox.SelectedItem as RtssMetricChoice;
            if (choice == null) return;
            if (rtssMetricChoices.Any(x => x.Metric == choice.Metric)) { RtssAppearanceText.Text = "Esa métrica ya está en la lista."; return; }
            rtssMetricChoices.Add(new RtssMetricChoice { Metric = choice.Metric });
            RtssMetricsList.SelectedIndex = rtssMetricChoices.Count - 1; UpdateRtssDraft();
        }
        private async void RtssSaveAppearance_Click(object sender, RoutedEventArgs e)
            => await SaveRtssAppearance();
        private bool HasRtssDraftChanges() => ReadRtssDraft().Encode() != rtssSavedAppearance.Encode();
        private async Task<bool> SaveRtssAppearance()
        {
            if (working || rtssAppearanceSaving || rtssStatus?.SupportsAppearance != true) return false;
            rtssAppearanceSaving = true;
            try
            {
                var draft = ReadRtssDraft().Copy(); string encoded = draft.Encode();
                // LocalSettings belongs to this widget/user and survives updates.
                // Retain the previous value before an explicit replacement.
                var values = ApplicationData.Current.LocalSettings.Values;
                object old;
                if (values.TryGetValue(RtssAppearanceKey, out old) && old is string previous && previous != encoded)
                    values["RtssAppearance.previous"] = previous;
                values[RtssAppearanceKey] = encoded;
                if (RtssAppearance.Decode(values[RtssAppearanceKey] as string).Encode() != encoded)
                    throw new InvalidOperationException("No se confirmó el guardado de la apariencia.");
                rtssSavedAppearance = draft; rtssAppearanceLoadError = null;
                var response = await Send(new Request { Operation = "rtss_configure", RtssAppearance = draft.Copy() });
                if (response?.Success != true) {
                    rtssAppearanceApplyError = "Preferencia guardada; no se confirmó su aplicación al helper. Vuelve a guardar o activa el overlay cuando se recupere la conexión.";
                    UpdateRtssDraft();
                    return false;
                }
                rtssAppearanceApplyError = null; UpdateRtssDraft();
                return true;
            }
            catch (Exception ex) { RtssAppearanceText.Text = "No se completó el guardado: " + ex.Message; return false; }
            finally { rtssAppearanceSaving = false; UpdateRtssAppearanceButtons(); }
        }
        private void RenderOptiScaler(bool supported)
        {
            optiScalerSupported = supported;
            bool available = supported && widget != null && widget.GameBarDisplayMode == Microsoft.Gaming.XboxGameBar.XboxGameBarDisplayMode.Foreground;
            OptiScalerMenu.IsEnabled = available;
            OptiScalerText.Text = !supported ? "Acceso sin confirmar. Requiere el helper 0.1.30 o posterior."
                : !available ? "Para enviar Insert, abre este panel desde Game Bar sobre el juego."
                : "Acceso disponible. Enviar Insert no confirma que OptiScaler haya abierto su menú.";
        }
        private async void OptiScalerMenu_Click(object sender, RoutedEventArgs e)
        {
            if (working || !optiScalerSupported || widget == null
                || widget.GameBarDisplayMode != Microsoft.Gaming.XboxGameBar.XboxGameBarDisplayMode.Foreground) return;
            await Send(new Request { Operation = "optiscaler_menu" });
        }
    }
}
