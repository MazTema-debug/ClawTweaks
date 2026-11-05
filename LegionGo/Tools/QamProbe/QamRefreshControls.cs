// SPDX-License-Identifier: AGPL-3.0-only
namespace LegionTweaks.QamProbe
{
    // Binding and navigation only observe capabilities; explicit activation creates a write request.
    internal sealed class QamRefreshControls
    {
        private readonly TelemetryFreshness freshness;
        private QamDisplayResponse state;
        internal QamRefreshControls(TelemetryFreshness freshness = null) { this.freshness = freshness ?? new TelemetryFreshness(); }
        internal bool Busy { get; private set; }
        internal int? Current => freshness.IsCurrent ? state?.Info?.Current?.Frequency : null;
        internal bool CanApply(int frequency) => !Busy && freshness.IsCurrent && state?.Success == true
            && state.Supported && state.CanSetRefreshRate && state.Info?.CanChange == true && Current != frequency
            && (frequency == 60 ? state.Can60 : frequency == 144 && state.Can144);
        internal void Observe(QamDisplayResponse response)
        {
            if (response?.Info == null) { state = null; freshness.Invalidate(); return; }
            state = freshness.TryAccept(response.Info.CapturedAtUtc, freshness.Generation, out System.DateTime _) ? response : null;
        }
        internal QamDisplayRequest BeginApply(int frequency)
        {
            if (!CanApply(frequency)) return null;
            Busy = true;
            return new QamDisplayRequest { Operation = "set_refresh", Frequency = frequency, RequestId = System.Guid.NewGuid().ToString("N") };
        }
        internal void Complete(QamDisplayResponse response) { Busy = false; Observe(response); }
        internal void Invalidate() { state = null; freshness.Invalidate(); }
    }
}
