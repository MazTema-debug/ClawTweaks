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
        private readonly TelemetryFreshness freshness = new TelemetryFreshness();
        private XboxGameBarWidget widget;
        private bool polling, closed, refreshWhenIdle;

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
                widget.VisibleChanged += WidgetVisibilityChanged;
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
        private async void WidgetVisibilityChanged(XboxGameBarWidget sender, object args)
        {
            if (closed) return;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (closed) return;
                // A response begun before hiding must not repaint the reopened widget.
                freshness.Invalidate();
                Clear("Esperando una lectura nueva…");
                ApplyAppearance();
                refreshWhenIdle = widget.Visible;
                if (refreshWhenIdle) _ = Poll();
            });
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
            closed = true; timer.Stop(); freshness.Invalidate(); refreshWhenIdle = false;
            if (widget == null) return;
            widget.VisibleChanged -= WidgetVisibilityChanged;
            widget.RequestedOpacityChanged -= WidgetChanged;
            widget.GameBarDisplayModeChanged -= WidgetChanged;
        }
        private async Task Poll()
        {
            if (closed || (widget != null && !widget.Visible)) return;
            // Keep expiring the displayed values even while IPC is waiting for a reply.
            if (!freshness.IsCurrent) Clear("Esperando una lectura nueva…");
            if (polling) return;
            polling = true; refreshWhenIdle = false;
            int generation = freshness.Generation;
            try
            {
                var response = await PipeClient.Send(new Request { Operation = "telemetry" });
                if (!CanRender(generation)) return;
                if (!response.Success || response.State?.Supported != true)
                    throw new InvalidOperationException(response.Message);
                var state = response.State;
                if (!freshness.TryAccept(state.CapturedAtUtc, generation, out var captured))
                    throw new InvalidOperationException("Lectura vencida o fecha de captura inválida. Esperando una lectura nueva…");
                string F(string key, string unit, double divisor = 1, string format = "0") => TelemetryValue.Format(state, key, unit, divisor, format);
                Temperatures.Text = "CPU " + F("CpuTemperature", "°C") + " · GPU " + F("GpuTemperature", "°C");
                Voltages.Text = "Voltaje GPU " + F("GpuVoltageMv", "V", 1000, "0.000") + " · SoC " + F("SocVoltageMv", "V", 1000, "0.000");
                GpuLoad.Text = "GPU " + F("GpuUsage", "%") + " · " + F("GpuClockMHz", "MHz");
                Power.Text = "Consumo GPU " + F("GpuPower", "W") + " · CPU " + F("CpuPower", "W");
                Asic.Text = "ASIC (AMD) " + F("AsicPower", "W");
                PowerNote.Text = TelemetryValue.PowerNote(state) ?? "";
                PowerNote.Visibility = PowerNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                Limits.Text = TelemetryValue.DescribeLimits(state);
                Cooling.Text = "Ventilador " + F("FanRpm", "RPM") + " · Batería " + F("Battery", "%");
                PowerSource.Text = state.OnAc ? "Cargador conectado" : "En batería";
                bool missing = state.Values.Any(x => x.Error != null || !x.Value.HasValue);
                SampleStatus.Text = "Lectura " + captured.ToLocalTime().ToString("HH:mm:ss") + " · cada 2 s" + (missing ? " · Hay sensores N/D" : "");
                var gpu = state.Values.FirstOrDefault(x => x.Key == "GpuTemperature");
                if (gpu?.Value != null) SampleStatus.Text += "\nGPU: " + gpu.Source;
                Recording.Text = response.Recording?.Active == true ? "● Grabando CSV · " + response.Recording.Samples + " muestras" : "";
                if (!string.IsNullOrEmpty(response.Recording?.Error)) Recording.Text += "\nRegistro: " + response.Recording.Error;
            }
            catch (Exception ex) { if (CanRender(generation)) { freshness.Invalidate(); Clear(ex.Message); } }
            finally
            {
                polling = false;
                // Reopening can request one refresh while the previous generation is in flight.
                // Do not enqueue or overlap another request for every timer tick.
                if (refreshWhenIdle && !closed && (widget == null || widget.Visible)) _ = Poll();
            }
        }
        private bool CanRender(int generation) => !closed && generation == freshness.Generation && (widget == null || widget.Visible);
        private void Clear(string message)
        {
            Temperatures.Text = "CPU N/D · GPU N/D";
            Voltages.Text = "Voltaje GPU N/D · SoC N/D";
            GpuLoad.Text = "GPU N/D · N/D";
            Power.Text = "Consumo GPU N/D · CPU N/D";
            Asic.Text = "ASIC (AMD) N/D";
            PowerNote.Text = ""; PowerNote.Visibility = Visibility.Collapsed;
            Limits.Text = TelemetryValue.DescribeLimits(null);
            Cooling.Text = "Ventilador N/D · Batería N/D";
            PowerSource.Text = "Fuente de energía N/D";
            SampleStatus.Text = message;
            Recording.Text = "Estado del registro sin confirmar";
        }
    }
}
