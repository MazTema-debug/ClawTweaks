// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private static void AmdLifecycleTests()
    {
        Test("AMD context disposal waits for an in-flight query without destroying its session", () => {
            var session = new HeldAmdSession();
            try
            {
                var telemetry = new AmdTelemetry(() => session);
                var read = Task.Run(() => telemetry.Read());
                Assert(session.Entered.WaitOne(3000), "read did not enter native-session seam");
                var closing = new ManualResetEventSlim(false);
                Task dispose = null;
                try
                {
                    dispose = Task.Run(() => { closing.Set(); telemetry.Dispose(); });
                    Assert(closing.Wait(3000), "dispose did not start");
                    Assert(!dispose.Wait(100) && session.Destroyed == 0, "session destroyed during query");
                    session.Release.Set();
                    Assert(read.Wait(3000) && dispose.Wait(3000) && session.Destroyed == 1, "session did not close after query");
                    bool rejected = false;
                    try { telemetry.Read(); } catch (ObjectDisposedException) { rejected = true; }
                    Assert(rejected, "closed context accepted another read");
                    telemetry.Dispose();
                    Assert(session.Destroyed == 1, "context destroyed twice");
                }
                finally { session.Release.Set(); read.Wait(3000); dispose?.Wait(3000); closing.Dispose(); telemetry.Dispose(); }
            }
            finally { session.Entered.Dispose(); session.Release.Dispose(); }
        });
    }

    private sealed class HeldAmdSession : IAmdSession
    {
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
        internal readonly ManualResetEvent Release = new ManualResetEvent(false);
        internal int Destroyed;
        public Reading[] Read(out int? queryStatus, out bool hasPmLogValues)
        {
            Entered.Set();
            if (!Release.WaitOne(5000)) throw new TimeoutException("test read timed out");
            queryStatus = 0; hasPmLogValues = true;
            return new[] { new Reading { Key = "GpuPower", Value = 0, Unit = "W" } };
        }
        public void Dispose() { Interlocked.Increment(ref Destroyed); }
    }
}
