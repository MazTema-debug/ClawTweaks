// SPDX-License-Identifier: AGPL-3.0-only
using Windows.UI.Xaml.Controls.Primitives;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private readonly FanManualDraft fanManualDraft = new FanManualDraft();
        private void FanManualSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
        {
            // Slider changes only the local draft; never infer a confirmed setting from RPM or the firmware maximum bit.
            fanManualDraft.Edit(args.NewValue);
            if (FanManualDraftText != null) FanManualDraftText.Text = fanManualDraft.DraftText;
        }
        private void RenderFanManualReading()
        {
            if (FanManualDraftText == null || FanManualConfirmedText == null || FanManualRpmText == null || FanManualApply == null) return;
            FanManualDraftText.Text = fanManualDraft.DraftText;
            FanManualConfirmedText.Text = "Porcentaje manual confirmado: sin lectura disponible.";
            FanManualRpmText.Text = fanManualDraft.RpmText(state, metricsFreshness.IsCurrent);
            FanManualApply.IsEnabled = fanManualDraft.CanApply;
        }
    }
}
