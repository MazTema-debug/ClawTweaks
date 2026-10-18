// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LegionTweaks
{
    // ControlEngine serializes reads. This lifecycle lock also coordinates native
    // context destruction with an optional background read during helper shutdown.
    internal sealed class AmdTelemetry : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<IAmdSession> open;
        private readonly Func<DateTime> utcNow;
        private IAmdSession session;
        private DateTime? lastRead;
        private DateTime retryAfter, recoverAfter;
        private string lastError, nextReason = "initial", sessionReason;
        private int generation;
        private bool disposed;

        internal AmdTelemetry(Func<IAmdSession> open = null, Func<DateTime> utcNow = null)
        {
            this.open = open ?? (() => new AmdSession());
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public Reading[] Read()
        {
            lock (gate) return ReadCore();
        }

        private Reading[] ReadCore()
        {
            if (disposed) throw new ObjectDisposedException(nameof(AmdTelemetry));
            DateTime now = utcNow();
            // An observed gap is not a confirmed Windows suspend event. Re-enumeration also
            // handles a hidden widget with no recorder running. Never reapply any setting.
            if (session != null && lastRead.HasValue &&
                ((now - lastRead.Value).TotalSeconds > 10 || (now - lastRead.Value).TotalSeconds < -2))
            {
                CloseSession();
                nextReason = now < lastRead.Value ? "clock_changed" : "after_gap";
            }
            lastRead = now;
            try
            {
                if (session == null)
                {
                    if (now < retryAfter) return Failed(lastError);
                    session = open();
                    generation++; sessionReason = nextReason;
                    recoverAfter = now.AddSeconds(30);
                    retryAfter = DateTime.MinValue;
                }
                int? queryStatus; bool hasPmLogValues;
                var readings = session.Read(out queryStatus, out hasPmLogValues);
                string diagnostic = SessionDiagnostic() + ";" + (queryStatus.HasValue
                    ? "pmlog_status=" + queryStatus.Value.ToString(CultureInfo.InvariantCulture) : "pmlog_unavailable");
                if (queryStatus == 0 && !hasPmLogValues) diagnostic += ";pmlog_empty";
                foreach (var reading in readings) reading.Diagnostic = diagnostic;
                // A working OD5 fallback must not mask a failed PMLog context forever.
                // A missing entry point with working fallbacks is a capability, not a reset loop.
                bool failed = (queryStatus.HasValue && (queryStatus != 0 || !hasPmLogValues))
                    || readings.All(x => !x.Value.HasValue);
                if (failed && now >= recoverAfter)
                {
                    CloseSession();
                    nextReason = "after_pmlog_error";
                }
                return readings;
            }
            catch (Exception ex)
            {
                CloseSession(); lastError = ex.Message;
                retryAfter = now.AddSeconds(30);
                nextReason = "after_error";
                return Failed(lastError);
            }
        }

        private string SessionDiagnostic() => "amd_session=" + generation.ToString(CultureInfo.InvariantCulture)
            + ";amd_open=" + (sessionReason ?? nextReason);

        private Reading[] Failed(string error)
        {
            var readings = Decode(null, "AMD ADL");
            foreach (var reading in readings)
            {
                reading.Error = error ?? "Telemetría AMD no disponible.";
                reading.Diagnostic = SessionDiagnostic() + ";amd_retry_pending";
            }
            return readings;
        }

        internal static AmdNative.Adapter SelectAdapter(IEnumerable<AmdNative.Adapter> adapters)
        {
            // Windows ADL can report VendorID as decimal 1002. The PCI string is authoritative.
            // Multiple display records can refer to the same physical adapter.
            var amd = adapters.Where(a => a.Present != 0 &&
                ((a.Udid ?? "").IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 (a.Pnp ?? "").IndexOf("VEN_1002", StringComparison.OrdinalIgnoreCase) >= 0))
                .GroupBy(a => a.Bus + ":" + a.Device + ":" + a.Function).Select(g => g.First()).ToArray();
            if (amd.Length != 1)
                throw new InvalidOperationException(amd.Length == 0 ? "No se detectó la GPU AMD." : "Varias GPU AMD: no se puede identificar de forma inequívoca la GPU interna.");
            return amd[0];
        }

        internal static Reading[] Decode(AmdNative.Sensor[] sensors, string source)
        {
            // AMD ADLSensorType IDs and integer units; see SOURCES.md. No domain summing,
            // guessed scaling or clamping to ASIC power: retain the driver's original values.
            var t = ReadSensor(sensors, AmdNative.SensorId.TemperatureEdge, "GpuTemperature", "°C", source + " · EDGE", 1, 150);
            if (!t.Value.HasValue) t = ReadSensor(sensors, AmdNative.SensorId.TemperatureGfx, "GpuTemperature", "°C", source + " · GFX", 1, 150);
            return new[] { t,
                ReadSensor(sensors, AmdNative.SensorId.GfxVoltage, "GpuVoltageMv", "mV", source, 1, 2500),
                ReadSensor(sensors, AmdNative.SensorId.SocVoltage, "SocVoltageMv", "mV", source, 1, 2500),
                ReadSensor(sensors, AmdNative.SensorId.GfxClock, "GpuClockMHz", "MHz", source, 0, 6000),
                ReadSensor(sensors, AmdNative.SensorId.GfxActivity, "GpuUsage", "%", source, 0, 100),
                ReadSensor(sensors, AmdNative.SensorId.GfxPower, "GpuPower", "W", source + " · GFX", 0, 200),
                ReadSensor(sensors, AmdNative.SensorId.AsicPower, "AsicPower", "W", source + " · ASIC", 0, 200),
                ReadSensor(sensors, AmdNative.SensorId.CpuPower, "CpuPower", "W", source + " · CPU", 0, 200) };
        }

        private static Reading ReadSensor(AmdNative.Sensor[] sensors, AmdNative.SensorId sensorId,
            string key, string unit, string source, int minimum, int maximum)
        {
            int id = (int)sensorId;
            bool present = sensors != null && id < sensors.Length;
            int? raw = present ? (int?)sensors[id].Value : null;
            int? supported = present ? (int?)sensors[id].Supported : null;
            var reading = TelemetryValue.Checked(key, supported.HasValue && supported != 0 ? raw : null,
                unit, source, minimum, maximum);
            reading.SensorId = id; reading.RawValue = raw; reading.SensorSupported = supported;
            return reading;
        }

        private void CloseSession()
        {
            var previous = session; session = null;
            try { previous?.Dispose(); } catch (Exception) { }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                CloseSession();
            }
        }
    }
}
