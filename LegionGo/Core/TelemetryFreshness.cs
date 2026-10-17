// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Globalization;

namespace LegionTweaks
{
    // Presentation policy only: this class never requests or modifies hardware state.
    public sealed class TelemetryFreshness
    {
        public const int MaximumAgeSeconds = 6;
        public const int FutureToleranceSeconds = 2;
        private readonly Func<DateTime> utcNow;
        private readonly Func<long> milliseconds;
        private DateTime? captured;
        private bool expired;
        private long receivedAt;
        private double initialAgeMilliseconds;

        public TelemetryFreshness(Func<DateTime> utcNow = null, Func<long> milliseconds = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (milliseconds != null) this.milliseconds = milliseconds;
            else
            {
                var clock = Stopwatch.StartNew();
                this.milliseconds = () => clock.ElapsedMilliseconds;
            }
        }

        public int Generation { get; private set; }

        public void Invalidate()
        {
            unchecked { Generation++; }
            captured = null; expired = false;
        }

        public static bool TryGetCapture(string timestamp, DateTime utcNow, out DateTime captured)
        {
            captured = DateTime.MinValue;
            if (utcNow.Kind != DateTimeKind.Utc || string.IsNullOrEmpty(timestamp) || timestamp.Length > 33
                || !(timestamp.EndsWith("Z", StringComparison.Ordinal)
                    || timestamp.EndsWith("+00:00", StringComparison.Ordinal)
                    || timestamp.EndsWith("-00:00", StringComparison.Ordinal))) return false;
            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParseExact(timestamp, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)
                || parsed.Offset != TimeSpan.Zero || !InWindow(parsed.UtcDateTime, utcNow)) return false;
            captured = parsed.UtcDateTime;
            return true;
        }

        private static bool InWindow(DateTime captured, DateTime now)
        {
            if (now.Kind != DateTimeKind.Utc) return false;
            double age = (now - captured).TotalSeconds;
            return age >= -FutureToleranceSeconds && age <= MaximumAgeSeconds;
        }

        public bool IsCurrent
        {
            get
            {
                if (!captured.HasValue || expired) return false;
                long now = milliseconds();
                bool current = now >= receivedAt && initialAgeMilliseconds + ((double)now - receivedAt) <= MaximumAgeSeconds * 1000
                    && InWindow(captured.Value, utcNow());
                // Once a receipt expires, a later clock correction cannot revive it.
                if (!current) expired = true;
                return current;
            }
        }

        public bool TryAccept(string timestamp, int generation, out DateTime capture)
        {
            capture = DateTime.MinValue;
            if (generation != Generation) return false;
            bool previousCurrent = IsCurrent;
            DateTime now = utcNow();
            if (!TryGetCapture(timestamp, now, out capture)) return false;
            if (captured.HasValue)
            {
                // Repeated cached responses must not restart the receipt-age budget.
                if (capture == captured.Value) return previousCurrent;
                if (capture < captured.Value) return false;
            }
            captured = capture; expired = false;
            receivedAt = milliseconds();
            initialAgeMilliseconds = Math.Max(0, (now - capture).TotalMilliseconds);
            return true;
        }
    }
}
