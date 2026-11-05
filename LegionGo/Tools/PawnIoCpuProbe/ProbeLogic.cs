// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LegionTweaks.CpuProbe
{
    internal static class CpuProbeModule
    {
        internal const int Size = 10652;
        internal const string Sha256 = "dae74615761b78bdf064dfb3e136252ddcc6fc727d88f14738d0e5800d427a91";
        internal const string Release = "0.2.11";
        internal const string Commit = "52a7e536dff3e53c96917a28caac5e0fa6510696";
        internal static bool Accepts(byte[] bytes)
        {
            if (bytes == null || bytes.Length != Size) return false;
            using (var hash = SHA256.Create())
                return string.Equals(BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), Sha256, StringComparison.OrdinalIgnoreCase);
        }
        internal static byte[] Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                if (file.Length != Size) throw new InvalidDataException("Tamaño de módulo no reconocido.");
                var bytes = new byte[Size]; int offset = 0, count;
                while (offset < bytes.Length && (count = file.Read(bytes, offset, bytes.Length - offset)) != 0) offset += count;
                if (offset != Size || !Accepts(bytes)) throw new InvalidDataException("Hash de módulo AMD no reconocido.");
                return bytes;
            }
        }
    }
    internal sealed class CpuProbeDevice
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string Version { get; set; }
        public string CpuVendor { get; set; }
        public string CpuName { get; set; }
        public uint Signature { get; set; }
        public int PhysicalCores { get; set; }
        public int LogicalProcessors { get; set; }
        public int Family => (int)(((Signature >> 8) & 15) == 15 ? 15 + ((Signature >> 20) & 255) : ((Signature >> 8) & 15));
        public int CpuModel => (int)(((Signature >> 4) & 15) | (((Signature >> 16) & 15) << 4));
        public bool Supported => string.Equals(Manufacturer?.Trim(), "LENOVO", StringComparison.OrdinalIgnoreCase)
            && ((Model ?? "").StartsWith("83E1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Version?.Trim(), "Legion Go 8APU1", StringComparison.OrdinalIgnoreCase))
            && string.Equals(CpuVendor?.Trim(), "AuthenticAMD", StringComparison.OrdinalIgnoreCase)
            && (CpuName ?? "").IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0
            && (CpuName ?? "").IndexOf("Z1 Extreme", StringComparison.OrdinalIgnoreCase) >= 0
            && Family == 0x19 && CpuModel == 0x74 && PhysicalCores == 8 && LogicalProcessors == 16;
    }
    internal sealed class CpuTarget
    {
        public int Core { get; set; }
        public int Sibling { get; set; }
        public ushort Group { get; set; }
        public int Logical { get; set; }
        internal string Key => Group + ":" + Logical;
    }
    internal static class CpuProbeTopology
    {
        // x64 SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX / PROCESSOR_RELATIONSHIP.
        // Core relation: 8-byte header, 24-byte fixed body, 16-byte GROUP_AFFINITY.
        internal static CpuTarget[] Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 65536) throw new InvalidDataException("Topología ausente o fuera de límite.");
            var masks = new List<ulong>(); ulong used = 0;
            for (int offset = 0; offset < bytes.Length;) {
                if (bytes.Length - offset < 8) throw new InvalidDataException("Cabecera de topología incompleta.");
                uint kind = BitConverter.ToUInt32(bytes, offset), size = BitConverter.ToUInt32(bytes, offset + 4);
                if (kind != 0 || size < 48 || size > bytes.Length - offset) throw new InvalidDataException("Relación de núcleo no válida.");
                ushort groups = BitConverter.ToUInt16(bytes, offset + 30), group = BitConverter.ToUInt16(bytes, offset + 40);
                ulong mask = BitConverter.ToUInt64(bytes, offset + 32);
                // This probe deliberately targets the original 8C/16T, one group.
                if (groups != 1 || group != 0 || PopCount(mask) != 2 || (mask & used) != 0)
                    throw new InvalidDataException("Mapa de núcleos/SMT no compatible con esta sonda.");
                masks.Add(mask); used |= mask; offset += (int)size;
            }
            if (masks.Count != 8 || PopCount(used) != 16) throw new InvalidDataException("Se requieren 8 núcleos y 16 hilos identificados por Windows.");
            var targets = new List<CpuTarget>(); int core = 0;
            foreach (ulong mask in masks.OrderBy(LowestBit)) {
                int sibling = 0;
                for (int bit = 0; bit < 64; bit++) if ((mask & (1UL << bit)) != 0)
                    targets.Add(new CpuTarget { Core = core, Sibling = sibling++, Group = 0, Logical = bit });
                core++;
            }
            return targets.ToArray();
        }
        private static int PopCount(ulong value) { int count = 0; for (; value != 0; value &= value - 1) count++; return count; }
        private static int LowestBit(ulong value) { for (int i = 0; i < 64; i++) if ((value & (1UL << i)) != 0) return i; return 64; }
    }
    internal static class CpuProbeMsr
    {
        internal const uint HardwarePstate = 0xC0010293, Aperf = 0xC00000E8, Mperf = 0xC00000E7;
        internal static bool Allowed(uint index) => index == HardwarePstate || index == Aperf || index == Mperf;
    }
    internal static class CpuProbeAbi
    {
        // PawnIO 2.2.0 ships PawnIOLib ABI 2.0.0. Release/file version is
        // separate from the API version exported by pawnio_version.
        internal static bool Supported(uint version) => (version >> 16) == 2;
    }
    internal interface ICpuPawnApi
    {
        int Open(out IntPtr handle);
        int Load(IntPtr handle, byte[] module);
        int Read(IntPtr handle, uint index, out ulong value, out ulong count);
        int Close(IntPtr handle);
    }
    internal sealed class CpuProbeReader : IDisposable
    {
        private readonly object gate = new object();
        private readonly ICpuPawnApi api;
        private IntPtr handle;
        private bool disposed;
        internal int Reads { get; private set; }
        internal CpuProbeReader(ICpuPawnApi api, byte[] module)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            if (module == null || module.Length == 0) throw new ArgumentException("Módulo ausente.");
            try {
                Check(api.Open(out handle), "pawnio_open");
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new IOException("Handle PawnIO no válido.");
                Check(api.Load(handle, module), "pawnio_load");
            }
            catch { if (handle != IntPtr.Zero && handle != new IntPtr(-1)) api.Close(handle); handle = IntPtr.Zero; throw; }
        }
        internal ulong Read(uint index)
        {
            lock (gate) {
                if (disposed) throw new ObjectDisposedException(nameof(CpuProbeReader));
                if (!CpuProbeMsr.Allowed(index)) throw new ArgumentException("MSR fuera de la lista de lectura de la sonda.");
                ulong value, count;
                int result = api.Read(handle, index, out value, out count); Reads++;
                Check(result, "ioctl_read_msr 0x" + index.ToString("X8"));
                if (count != 1) throw new IOException("PawnIO devolvió un tamaño de lectura inesperado: " + count + ".");
                return value;
            }
        }
        public void Dispose()
        {
            lock (gate) {
                if (disposed) return;
                disposed = true; var old = handle; handle = IntPtr.Zero;
                if (old != IntPtr.Zero) Check(api.Close(old), "pawnio_close");
            }
        }
        private static void Check(int result, string operation)
        { if (result < 0) throw new IOException(operation + " HRESULT 0x" + unchecked((uint)result).ToString("X8")); }
    }
    internal sealed class CpuCounterReading
    {
        internal ulong Aperf, Mperf;
        internal long Timestamp;
    }
    internal sealed class CpuSessionPeak
    {
        public int Core { get; set; }
        public double MHz { get; set; }
        public string ObservedAtUtc { get; set; }
    }
    internal sealed class CpuProbePeaks
    {
        private readonly Dictionary<int, CpuSessionPeak> peaks = new Dictionary<int, CpuSessionPeak>();
        internal CpuSessionPeak Observe(int core, ulong? hardwarePstate, string utc)
        {
            double? mhz = hardwarePstate.HasValue ? CpuProbeCounters.PstateMHz(hardwarePstate.Value) : null;
            CpuSessionPeak peak; peaks.TryGetValue(core, out peak);
            if (mhz.HasValue && (peak == null || mhz.Value > peak.MHz))
                peaks[core] = peak = new CpuSessionPeak { Core = core, MHz = mhz.Value, ObservedAtUtc = utc };
            return peak == null ? null : new CpuSessionPeak { Core = peak.Core, MHz = peak.MHz, ObservedAtUtc = peak.ObservedAtUtc };
        }
        internal CpuSessionPeak[] GetAll() => peaks.Values.OrderBy(x => x.Core)
            .Select(x => new CpuSessionPeak { Core = x.Core, MHz = x.MHz, ObservedAtUtc = x.ObservedAtUtc }).ToArray();
    }
    internal sealed class CpuCounterResult
    {
        public double? EffectiveLogicalMHz { get; set; }
        public double? AperfMperfRatio { get; set; }
        public string State { get; set; }
    }
    internal sealed class CpuCoreEffectiveSample
    {
        public int Core { get; set; }
        public double? EffectiveCoreMHz { get; set; }
        public string State { get; set; }
    }
    internal sealed class CpuProbeCoreAverages
    {
        private readonly KeyValuePair<int, string[]>[] cores;
        internal CpuProbeCoreAverages(CpuTarget[] targets)
        {
            if (targets == null || targets.Length != 16 || targets.Any(x => x == null)
                || targets.Select(x => x.Key).Distinct().Count() != 16)
                throw new ArgumentException("Se requiere un mapa único de 8 núcleos / 16 hilos.");
            var groups = targets.GroupBy(x => x.Core).OrderBy(x => x.Key).ToArray();
            if (groups.Length != 8 || groups.Any(g => g.Key < 0 || g.Key > 7 || g.Count() != 2
                || !g.Select(x => x.Sibling).OrderBy(x => x).SequenceEqual(new[] { 0, 1 })))
                throw new ArgumentException("Cada núcleo debe tener sus dos hilos SMT identificados.");
            // Freeze Windows' actual map; callers cannot retarget an existing average.
            cores = groups.Select(g => new KeyValuePair<int, string[]>(g.Key,
                g.OrderBy(x => x.Sibling).Select(x => x.Key).ToArray())).ToArray();
        }
        // Input contains only the current sweep. No values or baselines are cached
        // here, so a missing sibling can never reuse an older measurement.
        internal CpuCoreEffectiveSample[] Average(IDictionary<string, CpuCounterResult> currentSweep)
        {
            if (currentSweep == null) throw new ArgumentNullException(nameof(currentSweep));
            return cores.Select(core => {
                CpuCounterResult first, second;
                currentSweep.TryGetValue(core.Value[0], out first);
                currentSweep.TryGetValue(core.Value[1], out second);
                if (first?.EffectiveLogicalMHz == null || second?.EffectiveLogicalMHz == null)
                    return new CpuCoreEffectiveSample { Core = core.Key,
                        State = first?.State == "warmup" && second?.State == "warmup" ? "warmup" : "incomplete-smt" };
                double a = first.EffectiveLogicalMHz.Value, b = second.EffectiveLogicalMHz.Value;
                if (double.IsNaN(a) || double.IsInfinity(a) || double.IsNaN(b) || double.IsInfinity(b)
                    || a < 0 || b < 0 || a > 6000 || b > 6000)
                    return new CpuCoreEffectiveSample { Core = core.Key, State = "invalid-smt" };
                // Zero is an observed idle value and participates in the mean.
                return new CpuCoreEffectiveSample { Core = core.Key, EffectiveCoreMHz = (a + b) / 2.0, State = "averaged-smt" };
            }).ToArray();
        }
    }
    internal sealed class CpuProbeCounters
    {
        private readonly Dictionary<string, CpuCounterReading> previous = new Dictionary<string, CpuCounterReading>();
        private readonly double ticksPerSecond;
        internal CpuProbeCounters(long ticksPerSecond)
        { if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond)); this.ticksPerSecond = ticksPerSecond; }
        internal void Invalidate(string key) { previous.Remove(key); }
        internal CpuCounterResult Observe(string key, CpuCounterReading now)
        {
            CpuCounterReading old; bool hasOld = previous.TryGetValue(key, out old);
            previous[key] = new CpuCounterReading { Aperf = now.Aperf, Mperf = now.Mperf, Timestamp = now.Timestamp };
            if (!hasOld) return new CpuCounterResult { State = "warmup" };
            double seconds = (now.Timestamp - (double)old.Timestamp) / ticksPerSecond;
            if (seconds < 0.05 || seconds > 3 || now.Aperf < old.Aperf || now.Mperf < old.Mperf)
                return new CpuCounterResult { State = "discontinuity" };
            ulong actual = now.Aperf - old.Aperf, reference = now.Mperf - old.Mperf;
            double mhz = actual / seconds / 1000000.0;
            // All fields remain empty on an inconsistent tuple; never reuse data.
            if (mhz > 6000 || (reference == 0 && actual != 0)) return new CpuCounterResult { State = "invalid" };
            return new CpuCounterResult { EffectiveLogicalMHz = mhz,
                AperfMperfRatio = reference == 0 ? (double?)null : actual / (double)reference,
                State = actual == 0 ? "no-cycles-observed" : "sampled" };
        }
        internal static double? PstateMHz(ulong raw)
        {
            uint fid = (uint)(raw & 255), divisor = (uint)((raw >> 8) & 63);
            if (fid == 0 || divisor == 0) return null;
            double mhz = 200.0 * fid / divisor;
            return mhz > 6000 ? (double?)null : mhz;
        }
    }
}
