// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    // The controller worker still owns all handles and the recovery deadline.
    // Keep a reopened source alive while its required companion reader catches
    // up; repeatedly reopening it would invalidate that companion's fresh cache.
    internal sealed class ControllerSourceReadiness
    {
        private readonly Func<bool> open, read, companionRequired, companionReady;
        private readonly Action enableCompanion, close;
        private bool opened;

        internal ControllerSourceReadiness(Func<bool> open, Func<bool> read, Action enableCompanion,
            Func<bool> companionRequired, Func<bool> companionReady, Action close)
        {
            this.open = open ?? throw new ArgumentNullException(nameof(open));
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.enableCompanion = enableCompanion ?? throw new ArgumentNullException(nameof(enableCompanion));
            this.companionRequired = companionRequired ?? throw new ArgumentNullException(nameof(companionRequired));
            this.companionReady = companionReady ?? throw new ArgumentNullException(nameof(companionReady));
            this.close = close ?? throw new ArgumentNullException(nameof(close));
        }

        // The owner closes the source and neutralizes output when resetting.
        internal void Reset() { opened = false; }

        internal bool TryOpen()
        {
            if (!opened) {
                if (!open()) return false;
                opened = true;
                enableCompanion();
            }
            if (!read()) { opened = false; close(); return false; }
            return !companionRequired() || companionReady();
        }
    }
}
