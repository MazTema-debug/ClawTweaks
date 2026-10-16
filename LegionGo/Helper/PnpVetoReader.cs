// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    // Reads existing local channels only. Does not enable logging, register ETW,
    // query device handles or invoke any controller/HID/SetupAPI/XInput API.
    internal static class PnpVetoReader
    {
        private const int BudgetMilliseconds = 1900;
        private static int active;
        internal static PnpVetoDiagnostic Read(PhysicalRecoveryOutcome outcome)
        {
            var clock = Stopwatch.StartNew();
            var window = PnpVetoRules.Window(outcome);
            if (window == null) return PnpVetoRules.Unknown("legacy");
            if (window.EndUtc > DateTime.UtcNow.AddSeconds(2).Ticks) return PnpVetoRules.Unknown();
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return PnpVetoRules.Unknown("busy");
            var analysis = new PnpVetoAnalysis(window);
            Task<PnpVetoDiagnostic> task = null;
            try
            {
                task = Task.Run(() => {
                    try { return Collect(window, analysis, clock); }
                    catch { return analysis.Snapshot(true); }
                    finally { Interlocked.Exchange(ref active, 0); }
                });
                int remaining = Math.Max(0, BudgetMilliseconds - (int)clock.ElapsedMilliseconds);
                return task.Wait(remaining) ? task.Result : analysis.Snapshot(true);
            }
            catch { if (task == null) Interlocked.Exchange(ref active, 0); return analysis.Snapshot(true); }
            // A stalled local Event Log API cannot hold the IPC request indefinitely.
            // Only one background reader is permitted until it actually terminates.
        }
        private static PnpVetoDiagnostic Collect(PnpVetoWindow window, PnpVetoAnalysis analysis, Stopwatch clock)
        {
            bool limit = false;
            foreach (string channel in new[] { PnpVetoRules.SystemChannel, PnpVetoRules.DeviceChannel })
            {
                if (clock.ElapsedMilliseconds >= BudgetMilliseconds || limit) break;
                try
                {
                    var query = new EventLogQuery(channel, PathType.LogName, PnpVetoRules.XPath(window, channel)) { ReverseDirection = false };
                    using (var reader = new EventLogReader(query))
                    {
                        while (true)
                        {
                            int remaining = BudgetMilliseconds - (int)clock.ElapsedMilliseconds;
                            if (remaining <= 0) { limit = true; break; }
                            using (var record = reader.ReadEvent(TimeSpan.FromMilliseconds(Math.Min(remaining, 200))))
                            {
                                if (record == null) { analysis.Complete(channel); break; }
                                if (!analysis.Observe(record.ToXml(), channel)) { limit = true; break; }
                            }
                        }
                    }
                }
                catch { /* Missing/disabled/inaccessible channel remains unconfirmed. */ }
            }
            return analysis.Snapshot(limit || clock.ElapsedMilliseconds >= BudgetMilliseconds);
        }
    }
}
