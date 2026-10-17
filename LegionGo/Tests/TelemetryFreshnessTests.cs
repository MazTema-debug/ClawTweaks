// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void TelemetryFreshnessTests()
    {
        Test("Telemetry freshness accepts explicit UTC and zero offsets with inclusive time bounds", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), parsed;
            foreach (DateTime time in new[] { now, now.AddSeconds(-6), now.AddSeconds(2) })
            {
                Assert(TelemetryFreshness.TryGetCapture(time.ToString("O"), now, out parsed) && parsed == time && parsed.Kind == DateTimeKind.Utc, "UTC boundary rejected");
                Assert(TelemetryFreshness.TryGetCapture(new DateTimeOffset(time).ToString("O"), now, out parsed) && parsed == time, "zero offset rejected");
            }
            Assert(TelemetryFreshness.TryGetCapture("2026-10-16T12:00:00.0000000-00:00", now, out parsed), "negative zero offset rejected");
        });
        Test("Telemetry freshness rejects local ambiguous malformed and nonzero-offset dates", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), parsed;
            foreach (string value in new[] { null, "", "2026-10-16T12:00:00.0000000", "16/10/2026 12:00:00", "2026-10-16T12:00:00Z", "2026-10-16T13:00:00.0000000+01:00", "2026-10-16T12:00:00.0000000Z garbage" })
                Assert(!TelemetryFreshness.TryGetCapture(value, now, out parsed) && parsed == DateTime.MinValue, "ambiguous timestamp accepted: " + value);
            Assert(!TelemetryFreshness.TryGetCapture(now.ToString("O"), DateTime.SpecifyKind(now, DateTimeKind.Unspecified), out parsed), "ambiguous comparison clock accepted");
        });
        Test("Telemetry freshness rejects expired and excessively future samples", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), parsed;
            Assert(!TelemetryFreshness.TryGetCapture(now.AddSeconds(-6).AddTicks(-1).ToString("O"), now, out parsed), "expired sample accepted");
            Assert(!TelemetryFreshness.TryGetCapture(now.AddSeconds(2).AddTicks(1).ToString("O"), now, out parsed), "future sample accepted");
        });
        Test("Telemetry receipt age expires despite a UTC clock rollback", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            long elapsed = 1000; var gate = new TelemetryFreshness(() => now, () => elapsed);
            Assert(gate.TryAccept(now.AddSeconds(-3).ToString("O"), gate.Generation, out capture) && gate.IsCurrent, "fresh receipt rejected");
            now = now.AddSeconds(-1); elapsed += 3000;
            Assert(gate.IsCurrent, "inclusive receipt boundary rejected");
            elapsed++; Assert(!gate.IsCurrent, "UTC rollback kept old receipt alive");
        });
        Test("Telemetry repeated samples cannot renew the receipt-age budget", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            long elapsed = 0; var gate = new TelemetryFreshness(() => now, () => elapsed);
            string stamp = now.ToString("O");
            Assert(gate.TryAccept(stamp, gate.Generation, out capture), "initial sample rejected");
            elapsed = 5000; Assert(gate.TryAccept(stamp, gate.Generation, out capture), "valid repeat rejected");
            elapsed = 6001; Assert(!gate.TryAccept(stamp, gate.Generation, out capture) && !gate.IsCurrent, "repeat renewed stale sample");
        });
        Test("Telemetry invalidation rejects responses from a previous visibility generation", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            var gate = new TelemetryFreshness(() => now, () => 0);
            int old = gate.Generation; Assert(gate.TryAccept(now.ToString("O"), old, out capture), "initial sample rejected");
            gate.Invalidate(); Assert(!gate.IsCurrent && !gate.TryAccept(now.ToString("O"), old, out capture), "hidden response restored stale sample");
            Assert(gate.TryAccept(now.ToString("O"), gate.Generation, out capture) && gate.IsCurrent, "new generation could not recover");
        });
        Test("Telemetry backward monotonic time and regressing samples are not current", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            long elapsed = 1000; var gate = new TelemetryFreshness(() => now, () => elapsed);
            Assert(gate.TryAccept(now.ToString("O"), gate.Generation, out capture), "initial sample rejected");
            Assert(!gate.TryAccept(now.AddSeconds(-1).ToString("O"), gate.Generation, out capture), "older response replaced sample");
            elapsed = 999; Assert(!gate.IsCurrent, "backward elapsed clock accepted");
        });
        Test("Telemetry a new valid sample recovers after receipt expiration", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            long elapsed = 0; var gate = new TelemetryFreshness(() => now, () => elapsed);
            Assert(gate.TryAccept(now.ToString("O"), gate.Generation, out capture), "initial sample rejected");
            now = now.AddSeconds(7); elapsed = 7000; Assert(!gate.IsCurrent, "old sample not expired");
            Assert(gate.TryAccept(now.ToString("O"), gate.Generation, out capture) && gate.IsCurrent, "fresh sample did not recover");
        });
        Test("Telemetry an expired receipt cannot revive after a wall-clock correction", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            long elapsed = 0; var gate = new TelemetryFreshness(() => now, () => elapsed);
            string stamp = now.ToString("O");
            Assert(gate.TryAccept(stamp, gate.Generation, out capture), "initial sample rejected");
            now = now.AddSeconds(7); elapsed = 100;
            Assert(!gate.IsCurrent, "wall-clock jump did not expire receipt");
            now = now.AddSeconds(-7); elapsed = 200;
            Assert(!gate.IsCurrent && !gate.TryAccept(stamp, gate.Generation, out capture), "wall-clock correction revived expired sample");
            now = now.AddSeconds(1);
            Assert(gate.TryAccept(now.ToString("O"), gate.Generation, out capture) && gate.IsCurrent, "new sample did not recover after clock correction");
        });
        Test("Telemetry rejected repeated timestamps latch expiration before the clock returns", () =>
        {
            DateTime now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), capture;
            var gate = new TelemetryFreshness(() => now, () => 0);
            string stamp = now.ToString("O");
            Assert(gate.TryAccept(stamp, gate.Generation, out capture), "initial sample rejected");
            now = now.AddSeconds(7);
            Assert(!gate.TryAccept(stamp, gate.Generation, out capture), "expired repeat accepted");
            now = now.AddSeconds(-7);
            Assert(!gate.TryAccept(stamp, gate.Generation, out capture) && !gate.IsCurrent, "rejected repeat revived after rollback");
        });
    }
}
