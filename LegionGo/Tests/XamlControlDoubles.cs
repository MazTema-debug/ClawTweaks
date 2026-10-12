// SPDX-License-Identifier: AGPL-3.0-only
// Test-only stand-ins for the two UWP controls used by the production presenter.
// Model the documented effective IsEnabled value, including a disabled ancestor:
// https://learn.microsoft.com/en-us/uwp/api/windows.ui.xaml.controls.control.isenabled
// These classes are not compiled into the widget and do not test Game Bar itself.
namespace Windows.UI.Xaml.Controls
{
    internal class Control
    {
        private bool locallyEnabled = true;
        public Control Parent { get; set; }
        public bool IsEnabled
        {
            get { return locallyEnabled && (Parent == null || Parent.IsEnabled); }
            set { locallyEnabled = value; }
        }
    }
    internal sealed class Button : Control { }
    internal sealed class ToggleSwitch : Control
    {
        public bool IsOn { get; set; }
    }
}
