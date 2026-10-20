// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage : Page
    {
        private Snapshot state;
        private bool working;
        private XboxGameBarWidget widget;
        private readonly DispatcherTimer recordingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private RecordingStatus recording;
        private bool pollingRecording, pageLoaded;
        private int requestGeneration;
        private readonly TelemetryFreshness metricsFreshness = new TelemetryFreshness();
        private DateTime metricCaptured;
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            widget = e.Parameter as XboxGameBarWidget;
        }
        public MainPage()
        {
            InitializeComponent();
            WidgetVersionRun.Text = DiagnosticWidgetVersion;
            Loaded += async (s, e) => { pageLoaded = true; shortcutLoaded = false; UpdateMetricAge(); await LoadProfiles(); if (!pageLoaded) return; await Send(new Request { Operation = "status" }); if (pageLoaded) recordingTimer.Start(); };
            recordingTimer.Tick += async (s, e) => { UpdateMetricAge(); await PollRecording(); };
            Unloaded += (s, e) => { pageLoaded = false; recordingTimer.Stop(); metricsFreshness.Invalidate(); CancelSessionSummary(); };
            Window.Current.Closed += (s, e) => { pageLoaded = false; recordingTimer.Stop(); metricsFreshness.Invalidate(); CancelSessionSummary(); };
        }
        private int? Value(string key) => TelemetryValue.Value(state, key);
        private string Show(string key, string suffix = "") => Value(key)?.ToString() + (Value(key).HasValue ? suffix : "—");
        private async Task Send(Request request)
        {
            if (working) return;
            requestGeneration++;
            working = true;
            Controls.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            Busy.Visibility = Visibility.Visible;
            StatusText.Text = request.Operation == "status" ? "Leyendo el equipo…" : "Aplicando y verificando…";
            int metricGeneration = metricsFreshness.Generation;
            try
            {
                var response = await PipeClient.Send(request);
                bool staleState = false;
                if (response.State != null)
                {
                    if (response.State.Supported && !metricsFreshness.TryAccept(response.State.CapturedAtUtc, metricGeneration, out metricCaptured))
                    {
                        state = null; staleState = true; metricsFreshness.Invalidate();
                        DeviceText.Text = "Lectura del equipo sin confirmar";
                        MetricsText.Text = "Lecturas recibidas vencidas o con fecha inválida. Actualiza las lecturas.";
                    }
                    else { state = response.State; Render(); }
                    UpdateMetricAge();
                }
                else if (request.Operation == "status" || request.Operation == "set" || request.Operation == "tdp" || request.Operation == "restore")
                {
                    state = null;
                    metricsFreshness.Invalidate(); UpdateMetricAge();
                    DeviceText.Text = "Estado del equipo sin confirmar";
                    MetricsText.Text = "Actualiza las lecturas antes de continuar.";
                }
                RenderRecording(response.Recording);
                RenderController(response.Controller);
                RenderDesktop(response.ControllerStartup, request.Operation == "controller_startup_set" && response.Success);
                RenderMouse(response.Mouse);
                RenderRtss(response.Rtss);
                if (request.Operation == "diagnostics") RenderDiagnostics(response.Diagnostics);
                RenderShortcuts(response.Shortcuts, request.Operation == "shortcut_set");
                if (request.Operation == "status" || request.Operation.StartsWith("space_", StringComparison.Ordinal)) RenderLegionSpace(response.LegionSpace);
                // Preserve the command outcome even when its snapshot expired in
                // transit; a successful write must not look like an IPC failure.
                StatusText.Text = response.Message + (staleState ? " · Actualiza las lecturas; la respuesta de la operación se conserva." : "")
                    + " · " + DateTime.Now.ToString("HH:mm:ss");
                if (request.Operation == "status" && state?.Supported == false)
                    StatusText.Text = "Este equipo no es una Legion Go 8APU1 / Z1 Extreme. Los controles están deshabilitados.";
            }
            catch (Exception ex)
            {
                // Stale readings must not remain actionable after a failed connection.
                state = null;
                metricsFreshness.Invalidate(); UpdateMetricAge();
                StatusText.Text = ex.Message;
                DeviceText.Text = "Sin conexión al servicio";
                MetricsText.Text = "Lecturas no disponibles";
                RenderRecording(null);
                RenderController(null);
                RenderDesktop(null);
                RenderMouse(null);
                RenderRtss(null);
                RenderDiagnostics(null);
                RenderShortcuts(null);
                RenderLegionSpace(null);
            }
            finally
            {
                working = false;
                Busy.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                Controls.IsEnabled = state?.Supported == true;
            }
        }
        private void UpdateMetricAge()
        {
            if (state?.Supported != true) { MetricAgeText.Text = "Sin una lectura del equipo confirmada."; return; }
            bool current = metricsFreshness.IsCurrent;
            MetricAgeText.Text = (current ? "Última lectura: " : "Lectura anterior (no actual): ")
                + metricCaptured.ToLocalTime().ToString("HH:mm:ss")
                + (current ? " · Actualización manual." : " · Pulsa Actualizar lecturas para renovarla.");
        }
        private void Render()
        {
            if (state == null) return;
            DeviceText.Text = state.Device.Model + " · " + state.Device.Cpu;
            MetricsText.Text = "CPU " + Show("CpuTemperature", " °C") + " · GPU " + TelemetryValue.Format(state, "GpuTemperature", "°C")
                + "\nGPU " + TelemetryValue.Format(state, "GpuVoltageMv", "V", 1000, "0.000") + " · Ventilador " + Show("FanRpm", " RPM")
                + "\nBatería " + Show("Battery", " %") + (state.OnAc ? " · Cargador conectado" : " · En batería");
            ModeBox.SelectedItem = null;
            foreach (ComboBoxItem item in ModeBox.Items)
                if (Convert.ToInt32(item.Tag) == Value("Mode")) ModeBox.SelectedItem = item;
            ModeApply.IsEnabled = Value("Mode").HasValue;
            bool tdpAvailable = Value("Spl").HasValue && Value("Sppt").HasValue && Value("Fppt").HasValue && Value("Mode").HasValue;
            TdpApply.IsEnabled = tdpAvailable;
            TdpLive.Text = TelemetryValue.DescribeLimits(state);
            // Values above this port's limits are shown truthfully above; draft sliders are bounded.
            if (tdpAvailable) { Spl.Value = Value("Spl").Value; Sppt.Value = Value("Sppt").Value; Fppt.Value = Value("Fppt").Value; }
            SetToggle(Fan, FanApply, "FanFullSpeed"); SetToggle(Charge, ChargeApply, "ChargeLimit"); SetToggle(Light, LightApply, "PowerLight");
            OsApply.IsEnabled = Value("OsPowerMode").HasValue;
            OsMode.SelectedIndex = Value("OsPowerMode") ?? -1;
            RenderCpu();
            var unavailable = state.Values.Where(x => x.Error != null).Select(x => x.Key).ToArray();
            if (unavailable.Length > 0) MetricsText.Text += "\nNo disponible: " + string.Join(", ", unavailable);
        }
        private void SetToggle(ToggleSwitch toggle, Button apply, string key)
        {
            TogglePresenter.Render(toggle, apply, Value(key));
        }
        private async Task PollRecording()
        {
            if (!pageLoaded || working || pollingRecording || (widget != null && !widget.Visible)) return;
            pollingRecording = true;
            int generation = requestGeneration;
            try
            {
                var response = await PipeClient.Send(new Request { Operation = "recording_status" });
                if (pageLoaded && !working && generation == requestGeneration) { RenderRecording(response.Success ? response.Recording : null); RenderController(response.Controller); RenderDesktop(response.ControllerStartup); RenderShortcuts(response.Shortcuts); RenderMouse(response.Mouse); RenderRtss(response.Rtss); }
            }
            catch { if (pageLoaded && !working && generation == requestGeneration) { RenderRecording(null); RenderController(null); RenderDesktop(null); RenderShortcuts(null); RenderMouse(null); RenderRtss(null); } }
            finally { pollingRecording = false; }
        }
        private void RenderRecording(RecordingStatus value)
        {
            recording = value;
            RecordStart.IsEnabled = value != null && !value.Active;
            RecordStop.IsEnabled = value?.Active == true;
            RecordOpen.IsEnabled = !string.IsNullOrEmpty(value?.Directory);
            if (value == null)
            {
                RecordingText.Text = "Estado del registro sin confirmar. Comprueba la conexión y que el helper sea 0.1.5 o posterior.";
                RecordingPath.Text = "";
                return;
            }
            RecordingText.Text = (value.Active ? "● Grabando" : "Detenido") + " · " + value.Samples + " muestras";
            if (DateTime.TryParse(value.StartedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var started))
            {
                DateTime end = DateTime.UtcNow;
                if (!value.Active && DateTime.TryParse(value.StoppedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var stopped)) end = stopped.ToUniversalTime();
                var duration = end - started.ToUniversalTime();
                RecordingText.Text += " · " + TimeSpan.FromSeconds(Math.Max(0, duration.TotalSeconds)).ToString(@"hh\:mm\:ss");
            }
            if (DateTime.TryParse(value.LastSampleAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last))
                RecordingText.Text += "\nÚltima muestra: " + last.ToLocalTime().ToString("HH:mm:ss")
                    + (value.Active && (DateTime.UtcNow - last.ToUniversalTime()).TotalSeconds > 6 ? " · Esperando lecturas nuevas" : "");
            if (!string.IsNullOrEmpty(value.Error)) RecordingText.Text += "\n" + value.Error;
            RecordingPath.Text = value.Directory + (string.IsNullOrEmpty(value.FileName) ? "" : "\n" + value.FileName);
        }
        private async void RecordStart_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "recording_start" });
        private async void RecordStop_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "recording_stop" });
        private async void RecordOpen_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(recording?.Directory)) return;
            try
            {
                if (!await Windows.System.Launcher.LaunchFolderPathAsync(recording.Directory))
                    StatusText.Text = "No se pudo abrir la carpeta. Copia la ruta que aparece en Registro al Explorador.";
            }
            catch (Exception ex) { StatusText.Text = "No se pudo abrir la carpeta: " + ex.Message; }
        }
        private void RenderCpu()
        {
            if (CpuLive == null || state == null) return;
            string suffix = Source.SelectedIndex == 0 ? "AC" : "DC";
            var boost = Value("CpuBoost" + suffix);
            var epp = Value("Epp" + suffix);
            BoostApply.IsEnabled = boost.HasValue; Boost.IsEnabled = boost.HasValue; Boost.IsOn = boost > 0;
            EppApply.IsEnabled = epp.HasValue; Epp.IsEnabled = epp.HasValue;
            if (epp.HasValue) Epp.Value = epp.Value;
            CpuLive.Text = "Leído: Boost " + (boost.HasValue ? (boost > 0 ? "activado" : "desactivado") : "—") + " · EPP " + Show("Epp" + suffix);
        }
        private Task Set(string key, int value) => Send(new Request { Operation = "set", Key = key, Values = new[] { value }, Ac = Source.SelectedIndex == 0 });
        private async void Refresh_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "status" });
        private async void Overlay_Click(object sender, RoutedEventArgs e)
        {
            if (widget == null) { StatusText.Text = "Abre Win+G → Widgets → LegionTweaks Overlay y pulsa la chincheta para fijarlo al juego."; return; }
            try { await new XboxGameBarWidgetControl(widget).ActivateAsync("LegionOverlay"); }
            catch (Exception ex) { StatusText.Text = "No se pudo abrir el overlay: " + ex.Message; }
        }
        private async void Mode_Click(object sender, RoutedEventArgs e)
        {
            var item = ModeBox.SelectedItem as ComboBoxItem;
            if (item != null) await Set("Mode", Convert.ToInt32(item.Tag));
        }
        private void Equalize_Click(object sender, RoutedEventArgs e) { Sppt.Value = Spl.Value; Fppt.Value = Spl.Value; }
        private int[] DraftLimits() => new[] { (int)Spl.Value, (int)Sppt.Value, (int)Fppt.Value };
        private async void Tdp_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "tdp", Values = DraftLimits() });
        private async void Boost_Click(object sender, RoutedEventArgs e) => await Set("CpuBoost", Boost.IsOn ? 2 : 0);
        private async void Epp_Click(object sender, RoutedEventArgs e) => await Set("Epp", (int)Epp.Value);
        private async void Os_Click(object sender, RoutedEventArgs e) => await Set("OsPowerMode", OsMode.SelectedIndex);
        private async void Fan_Click(object sender, RoutedEventArgs e) => await Set("FanFullSpeed", Fan.IsOn ? 1 : 0);
        private async void Charge_Click(object sender, RoutedEventArgs e) => await Set("ChargeLimit", Charge.IsOn ? 1 : 0);
        private async void Light_Click(object sender, RoutedEventArgs e) => await Set("PowerLight", Light.IsOn ? 1 : 0);
        private void Source_Changed(object sender, SelectionChangedEventArgs e) => RenderCpu();
        private async void Restore_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog { Title = "Restaurar valores originales", Content = "Se restaurarán los ajustes del equipo modificados por este port desde el último respaldo. Legion Space tiene su propio botón de restauración. ¿Continuar?",
                PrimaryButtonText = "Restaurar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Send(new Request { Operation = "restore" });
        }
    }
}
