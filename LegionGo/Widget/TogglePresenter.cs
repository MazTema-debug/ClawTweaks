// SPDX-License-Identifier: AGPL-3.0-only
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    internal static class TogglePresenter
    {
        public static void Render(ToggleSwitch toggle, Button apply, int? value)
        {
            // Send renders while the parent Pivot is disabled. Reading another
            // control's IsEnabled here returns its inherited busy state, not the
            // local value we just assigned, and would latch the switch off.
            bool available = value.HasValue && (value == 0 || value == 1);
            apply.IsEnabled = available;
            toggle.IsEnabled = available;
            toggle.IsOn = value == 1;
        }
    }
}
