// SPDX-License-Identifier: AGPL-3.0-only
namespace LegionTweaks.QamProbe
{
    internal enum QamAction { None, Toggle, Up, Down, Accept, Close, YieldToGameBar, PreviousTab, NextTab, Left, Right }

    // Runs on the owning passive reader. Activation and close are emitted on release.
    internal sealed class QamInput
    {
        private const int Left = 1, Right = 2;
        private const int Up = 1, Down = 2, DpadLeft = 4, DpadRight = 8, A = 4096, B = 8192;
        private const int LB = 256, RB = 512;
        private readonly LegionShortcutGate shortcut = new LegionShortcutGate();
        private long lastReport = -1;
        private bool leftPending, rightHeld, navigationArmed;
        private int previousButtons;
        private QamAction pendingNavigation;

        internal void Reset()
        {
            shortcut.Reset();
            lastReport = -1;
            leftPending = rightHeld = false;
            ResetNavigation();
        }

        private void ResetNavigation()
        {
            navigationArmed = false;
            previousButtons = 0;
            pendingNavigation = QamAction.None;
        }

        internal QamAction Observe(byte[] report, long now, bool navigationAllowed, bool frontHandledByHelper = false)
        {
            if (lastReport >= 0 && (now < lastReport || now - lastReport > LegionGamepadCache.FreshMilliseconds)) Reset();
            if (!LegionButtonReport.TryDecode(report, out int front)) return QamAction.None;
            lastReport = now;
            if (frontHandledByHelper && front != 0) { shortcut.Reset(); leftPending = rightHeld = false; ResetNavigation(); return QamAction.None; }
            if (!frontHandledByHelper && (front & Right) != 0) {
                bool first = !rightHeld;
                rightHeld = true;
                shortcut.Reset();
                leftPending = false;
                ResetNavigation();
                return first && navigationAllowed ? QamAction.YieldToGameBar : QamAction.None;
            }
            rightHeld = false;
            if (!frontHandledByHelper && shortcut.Observe(LegionShortcutMode.Left, front, now)) leftPending = true;
            if (front == 0 && leftPending) {
                leftPending = false;
                ResetNavigation();
                return QamAction.Toggle;
            }
            if (!navigationAllowed || (front & Left) != 0 || !LegionGamepadReport.TryDecode(report, out GamepadFrame frame)) {
                ResetNavigation();
                return QamAction.None;
            }
            if (!navigationArmed) {
                navigationArmed = frame.Buttons == 0;
                return QamAction.None;
            }
            int pressed = frame.Buttons & ~previousButtons;
            previousButtons = frame.Buttons;
            if ((frame.Buttons & (LB | RB)) == (LB | RB)) {
                pendingNavigation = QamAction.None;
                return QamAction.None;
            }
            if ((pressed & B) != 0) pendingNavigation = QamAction.Close;
            else if (pendingNavigation != QamAction.Close) {
                if ((pressed & LB) != 0) pendingNavigation = QamAction.PreviousTab;
                else if ((pressed & RB) != 0) pendingNavigation = QamAction.NextTab;
                else if ((pressed & A) != 0 && pendingNavigation == QamAction.None) pendingNavigation = QamAction.Accept;
            }
            if (pendingNavigation != QamAction.None) {
                if (frame.Buttons != 0) return QamAction.None;
                QamAction action = pendingNavigation;
                pendingNavigation = QamAction.None;
                return action;
            }
            if ((pressed & Up) != 0) return QamAction.Up;
            if ((pressed & Down) != 0) return QamAction.Down;
            if ((pressed & DpadLeft) != 0) return QamAction.Left;
            return (pressed & DpadRight) != 0 ? QamAction.Right : QamAction.None;
        }
    }
}
