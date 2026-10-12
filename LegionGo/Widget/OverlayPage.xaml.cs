// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace LegionTweaks.Widget
{
    public sealed partial class OverlayPage : Page
    {
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private XboxGameBarWidget widget;
        private bool polling, closed;
        private DateTime lastSample;

        public OverlayPage()
        {
            InitializeComponent();
            RequestedTheme = ElementTheme.Dark;
            timer.Tick += async (s, e) => await Poll();
            Loaded += async (s, e) => { if (!closed) { timer.Start(); await Poll(); } };
            Unloaded += (s, e) => Stop();
            Window.Current.Closed += (s, e) => Stop();
        }
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            widget = e.Parameter as XboxGameBarWidget;
            if (widget != null)
            {
                widget.VisibleChanged += WidgetChanged;
                widget.RequestedOpacityChanged += WidgetChanged;
                widget.GameBarDisplayModeChanged += WidgetChanged;
                ApplyAppearance();
            }
        }
        private async void WidgetChanged(XboxGameBarWidget sender, object args)
        {
            if (closed) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { if (!closed) ApplyAppearance(); });
        }
        private void ApplyAppearance()
        {
            // Game Bar supplies XAML opacity (0..1), as in Microsoft's WidgetAdvSampleCS.
            // Dividing again by 100 makes an opaque widget nearly invisible (1 -> 0.01).
            Panel.Opacity = Math.Max(0, Math.Min(1, widget.RequestedOpacity));
            Hint.Visibility = widget.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly ? Visibility.Collapsed : Visibility.Visible;
        }
        private void Stop()
        {
            if (closed) return;
            closed = true; timer.Stop();
            if (widget == null) return;
            widget.VisibleChanged -= WidgetChanged;
            widget.RequestedOpacityChanged -= WidgetChanged;
            widget.GameBarDisplayModeChanged -= WidgetChanged;
        }
        private async Task Poll()
        {
            if (closed || (widget != null && !widget.Visible)) return;
            if ((DateTime.UtcNow - lastSample).TotalSeconds > 6) Clear("Esperando una lectura nueva…");
            if (polling) return;
            polling = true;
            try
            {
                var response = await PipeClient.Send(new Request { Operation = "telemetry" });
                if (closed) return;
                if (!response.Success || response.State?.Supported != true)
                    throw new InvalidOperationException(response.Message);
                var state = response.State;
                if (!DateTime.TryParse(state.CapturedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var captured)
                    || (DateTime.UtcNow - captured.ToUniversalTime()).TotalSeconds > 6)
                    throw new InvalidOperationException("Lectura vencida o servicio anterior. Actualiza también el helper.");
                lastSample = captured.ToUniversalTime();
                string F(string key, string unit, double divisor = 1, string format = "0") => TelemetryValue.Format(state, key, unit, divisor, format);
                Temperatures.Text = "CPU " + F("CpuTemperature", "°C") + " · GPU " + F("GpuTemperature", "°C");
                Voltages.Text = "Voltaje GPU " + F("GpuVoltageMv", "V", 1000, "0.000") + " · SoC " + F("SocVoltageMv", "V", 1000, "0.000");
                GpuLoad.Text = "GPU " + F("GpuUsage", "%") + " · " + F("GpuClockMHz", "MHz");
                Power.Text = "Consumo GPU " + F("GpuPower", "W") + " · CPU " + F("CpuPower", "W");
                Asic.Text = "ASIC (AMD) " + F("AsicPower", "W");
                Limits.Text = TelemetryValue.DescribeLimits(state);
                Cooling.Text = "Ventilador " + F("FanRpm", "RPM") + " · Batería " + F("Battery", "%");
                PowerSource.Text = state.OnAc ? "Cargador conectado" : "En batería";
                bool missing = state.Values.Any(x => x.Error != null || !x.Value.HasValue);
                SampleStatus.Text = "Lectura " + lastSample.ToLocalTime().ToString("HH:mm:ss") + " · cada 2 s" + (missing ? " · Hay sensores N/D" : "");
                var gpu = state.Values.FirstOrDefault(x => x.Key == "GpuTemperature");
                if (gpu?.Value != null) SampleStatus.Text += "\nGPU: " + gpu.Source;
                Recording.Text = response.Recording?.Active == true ? "● Grabando CSV · " + response.Recording.Samples + " muestras" : "";
                if (!string.IsNullOrEmpty(response.Recording?.Error)) Recording.Text += "\nRegistro: " + response.Recording.Error;
            }
            catch (Exception ex) { if (!closed) { lastSample = DateTime.MinValue; Clear(ex.Message); } }
            finally { polling = false; }
        }
        private void Clear(string message)
        {
            Temperatures.Text = "CPU N/D · GPU N/D";
            Voltages.Text = "Voltaje GPU N/D · SoC N/D";
            GpuLoad.Text = "GPU N/D · N/D";
            Power.Text = "Consumo GPU N/D · CPU N/D";
            Asic.Text = "ASIC (AMD) N/D";
            Limits.Text = TelemetryValue.DescribeLimits(null);
            Cooling.Text = "Ventilador N/D · Batería N/D";
            PowerSource.Text = "Fuente de energía N/D";
            SampleStatus.Text = message;
            Recording.Text = "Estado del registro sin confirmar";
        }
    }
}
