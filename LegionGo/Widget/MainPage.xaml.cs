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
    [DataContract]
    public sealed class PowerProfile
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public int[] Limits { get; set; }
    }
    public sealed partial class MainPage : Page
    {
        private Snapshot state;
        private bool working;
        private List<PowerProfile> profiles;
        private XboxGameBarWidget widget;
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            widget = e.Parameter as XboxGameBarWidget;
        }
        public MainPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => { LoadProfiles(); await Send(new Request { Operation = "status" }); };
        }
        private int? Value(string key) => state?.Values?.FirstOrDefault(x => x.Key == key)?.Value;
        private string Show(string key, string suffix = "") => Value(key)?.ToString() + (Value(key).HasValue ? suffix : "—");
        private async Task Send(Request request)
        {
            if (working) return;
            working = true;
            Controls.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            Busy.Visibility = Visibility.Visible;
            StatusText.Text = request.Operation == "status" ? "Leyendo el equipo…" : "Aplicando y verificando…";
            try
            {
                var response = await PipeClient.Send(request);
                state = response.State;
                Render();
                StatusText.Text = response.Message + " · " + DateTime.Now.ToString("HH:mm:ss");
                if (request.Operation == "status" && state?.Supported == false)
                    StatusText.Text = "Este equipo no es una Legion Go 8APU1 / Z1 Extreme. Los controles están deshabilitados.";
            }
            catch (Exception ex)
            {
                // Stale readings must not remain actionable after a failed connection.
                state = null;
                StatusText.Text = ex.Message;
                DeviceText.Text = "Sin conexión al servicio";
                MetricsText.Text = "Lecturas no disponibles";
            }
            finally
            {
                working = false;
                Busy.Visibility = Visibility.Collapsed;
                RefreshButton.IsEnabled = true;
                Controls.IsEnabled = state?.Supported == true;
            }
        }
        private void Render()
        {
            if (state == null) return;
            DeviceText.Text = state.Device.Model + " · " + state.Device.Cpu;
            MetricsText.Text = "CPU " + Show("CpuTemperature", " °C") + " · GPU " + TelemetryValue.Format(state, "GpuTemperature", "°C")
                + "\nGPU " + TelemetryValue.Format(state, "GpuVoltageMv", "V", 1000, "0.000") + " · Ventilador " + Show("FanRpm", " RPM")
                + "\nBatería " + Show("Battery", " %") + (state.OnAc ? " · Cargador conectado" : " · En batería");
            foreach (ComboBoxItem item in ModeBox.Items)
                if (Convert.ToInt32(item.Tag) == Value("Mode")) ModeBox.SelectedItem = item;
            ModeApply.IsEnabled = Value("Mode").HasValue;
            bool tdpAvailable = Value("Spl").HasValue && Value("Sppt").HasValue && Value("Fppt").HasValue && Value("Mode").HasValue;
            TdpApply.IsEnabled = tdpAvailable;
            TdpLive.Text = "Leído: SPL " + Show("Spl", " W") + " · SPPT " + Show("Sppt", " W") + " · FPPT " + Show("Fppt", " W");
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
            var dialog = new ContentDialog { Title = "Restaurar valores originales", Content = "Se restaurarán los ajustes modificados por este port desde el último respaldo. ¿Continuar?",
                PrimaryButtonText = "Restaurar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Send(new Request { Operation = "restore" });
        }
        private void LoadProfiles()
        {
            try
            {
                var saved = ApplicationData.Current.LocalSettings.Values["LegionPowerProfiles"] as string;
                profiles = string.IsNullOrEmpty(saved) ? new List<PowerProfile>() : Protocol.Decode<List<PowerProfile>>(saved);
            }
            catch { profiles = new List<PowerProfile>(); StatusText.Text = "No se pudieron leer los perfiles guardados."; }
            Profiles.ItemsSource = profiles;
            if (profiles.Count > 0) Profiles.SelectedIndex = 0;
        }
        private void ProfileSave_Click(object sender, RoutedEventArgs e)
        {
            string name = ProfileName.Text.Trim();
            int[] limits = DraftLimits();
            if (name.Length == 0 || limits[0] > limits[1] || limits[1] > limits[2])
            { StatusText.Text = "Escribe un nombre y ordena los límites: SPL ≤ SPPT ≤ FPPT."; return; }
            var next = profiles.Where(x => !string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (next.Count >= 20) { StatusText.Text = "Máximo 20 perfiles. Usa un nombre existente para reemplazarlo."; return; }
            next.Add(new PowerProfile { Name = name, Limits = limits });
            try
            {
                ApplicationData.Current.LocalSettings.Values["LegionPowerProfiles"] = Protocol.Encode(next);
                profiles = next; Profiles.ItemsSource = profiles; Profiles.SelectedIndex = profiles.Count - 1;
                StatusText.Text = "Perfil guardado. Pulsa Aplicar perfil para usarlo.";
            }
            catch (Exception ex) { StatusText.Text = "No se guardó el perfil: " + ex.Message; }
        }
        private async void ProfileApply_Click(object sender, RoutedEventArgs e)
        {
            var profile = Profiles.SelectedItem as PowerProfile;
            if (profile != null) await Send(new Request { Operation = "tdp", Values = profile.Limits });
        }
    }
}
