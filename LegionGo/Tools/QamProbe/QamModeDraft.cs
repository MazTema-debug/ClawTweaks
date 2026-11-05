// SPDX-License-Identifier: AGPL-3.0-only
namespace LegionTweaks.QamProbe
{
    // Selection is presentation state; only the explicit Apply action creates a command.
    internal sealed class QamModeDraft
    {
        internal int? Selected { get; private set; }
        internal int? Confirmed { get; private set; }
        internal bool Available { get; private set; }
        internal bool Busy { get; private set; }
        internal bool CanApply => Available && !Busy && Selected.HasValue && Selected != Confirmed;
        internal void Observe(QamModeResponse response)
        {
            int? actual = QamModeService.CurrentMode(response?.State);
            Available = response?.Success == true && response.CanApply && response.State?.Supported == true && actual.HasValue;
            Confirmed = Available ? actual : null;
            if (!Selected.HasValue && actual.HasValue && QamModeRequest.IsPreset(actual.Value)) Selected = actual;
        }
        internal void Move(int direction)
        {
            if (Busy || (direction != -1 && direction != 1)) return;
            Selected = !Selected.HasValue ? (direction == 1 ? 1 : 3) : ((Selected.Value - 1 + direction + 3) % 3) + 1;
        }
        internal QamModeRequest BeginApply()
        {
            if (!CanApply) return null;
            Busy = true;
            return new QamModeRequest { Operation = "apply_mode", Mode = Selected.Value, RequestId = System.Guid.NewGuid().ToString("N") };
        }
        internal void Complete(QamModeResponse response) { Busy = false; Observe(response); }
        internal void Invalidate() { Available = false; Confirmed = null; }
    }
}
