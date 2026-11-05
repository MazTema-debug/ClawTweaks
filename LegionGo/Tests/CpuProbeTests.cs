// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegionTweaks.CpuProbe;

internal static partial class Program
{
    private static CpuProbeDevice CpuProbeSupported() => new CpuProbeDevice { Manufacturer = "LENOVO", Model = "83E1", Version = "Legion Go 8APU1",
        CpuVendor = "AuthenticAMD", CpuName = "AMD Ryzen Z1 Extreme", Signature = 0x00A70F41, PhysicalCores = 8, LogicalProcessors = 16 };
    private static byte[] CpuProbeMap()
    {
        var data = new byte[8 * 48];
        for (int i = 0; i < 8; i++) {
            Array.Copy(BitConverter.GetBytes(48U), 0, data, i * 48 + 4, 4);
            Array.Copy(BitConverter.GetBytes((ushort)1), 0, data, i * 48 + 30, 2);
            Array.Copy(BitConverter.GetBytes((1UL << i) | (1UL << (i + 8))), 0, data, i * 48 + 32, 8);
        }
        return data;
    }
    private static void CpuProbeMustFail(Action action)
    { bool failed = false; try { action(); } catch { failed = true; } Assert(failed, "invalid probe operation was accepted"); }
    private static CpuCounterReading CpuCounter(ulong a, ulong m, long tick) => new CpuCounterReading { Aperf = a, Mperf = m, Timestamp = tick };
    private static void CpuProbeTests()
    {
        Test("CPU core effective frequency averages Windows SMT partners and emits exactly eight cores", () => {
            var map = CpuProbeTopology.Parse(CpuProbeMap()); // siblings are 0+8, 1+9, ...
            var readings = map.ToDictionary(x => x.Key, x => new CpuCounterResult { EffectiveLogicalMHz = x.Sibling == 0 ? 4000 : 100, State = "sampled" });
            var cores = new CpuProbeCoreAverages(map).Average(readings);
            Assert(cores.Length == 8 && cores.Select(x => x.Core).SequenceEqual(Enumerable.Range(0,8))
                && cores.All(x => x.EffectiveCoreMHz == 2050 && x.State == "averaged-smt"), "SMT map or arithmetic mean incorrect");
        });
        Test("CPU core averages include observed zero without treating idle as missing", () => {
            var map = CpuProbeTopology.Parse(CpuProbeMap());
            var readings = map.ToDictionary(x => x.Key, x => new CpuCounterResult { EffectiveLogicalMHz = x.Sibling == 0 ? 4000 : 0 });
            var averages = new CpuProbeCoreAverages(map);
            Assert(averages.Average(readings).All(x => x.EffectiveCoreMHz == 2000), "zero sibling was discarded");
            foreach (var value in readings.Values) value.EffectiveLogicalMHz = 0;
            Assert(averages.Average(readings).All(x => x.EffectiveCoreMHz == 0), "idle core became unknown or nonzero");
        });
        Test("CPU core averages require both valid current siblings and never reuse older sweep data", () => {
            var map = CpuProbeTopology.Parse(CpuProbeMap()); var averages = new CpuProbeCoreAverages(map);
            var readings = map.ToDictionary(x => x.Key, x => new CpuCounterResult { EffectiveLogicalMHz = 4000, State = "sampled" });
            Assert(averages.Average(readings).All(x => x.EffectiveCoreMHz == 4000), "valid initial data lost");
            readings.Remove(map[1].Key);
            var partial = averages.Average(readings);
            Assert(!partial[0].EffectiveCoreMHz.HasValue && partial[0].State == "incomplete-smt"
                && partial.Skip(1).All(x => x.EffectiveCoreMHz == 4000), "partial core guessed a value or damaged other cores");
            Assert(averages.Average(new Dictionary<string,CpuCounterResult>()).All(x => !x.EffectiveCoreMHz.HasValue), "previous sweep was reused");
        });
        Test("CPU core averages preserve initial warmup and invalidate failed or nonfinite siblings", () => {
            var map = CpuProbeTopology.Parse(CpuProbeMap()); var averages = new CpuProbeCoreAverages(map);
            var readings = map.ToDictionary(x => x.Key, x => new CpuCounterResult { State = "warmup" });
            Assert(averages.Average(readings).All(x => !x.EffectiveCoreMHz.HasValue && x.State == "warmup"), "warmup invented core frequency");
            foreach (double? bad in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 6001 }) {
                readings[map[0].Key] = new CpuCounterResult { EffectiveLogicalMHz = 4000 };
                readings[map[1].Key] = new CpuCounterResult { EffectiveLogicalMHz = bad, State = "read-error-or-delay" };
                Assert(!averages.Average(readings)[0].EffectiveCoreMHz.HasValue, "invalid sibling entered mean");
            }
        });
        Test("CPU core average map is frozen and rejects duplicate or incomplete topology", () => {
            var map = CpuProbeTopology.Parse(CpuProbeMap()); var averages = new CpuProbeCoreAverages(map);
            var readings = map.ToDictionary(x => x.Key, x => new CpuCounterResult { EffectiveLogicalMHz = 1000 });
            map[0].Logical = 63;
            Assert(averages.Average(readings)[0].EffectiveCoreMHz == 1000, "caller retargeted a frozen SMT map");
            CpuProbeMustFail(() => new CpuProbeCoreAverages(null));
            CpuProbeMustFail(() => new CpuProbeCoreAverages(map.Take(15).ToArray()));
            map = CpuProbeTopology.Parse(CpuProbeMap()); map[1].Logical = map[0].Logical;
            CpuProbeMustFail(() => new CpuProbeCoreAverages(map));
            map = CpuProbeTopology.Parse(CpuProbeMap()); map[1].Sibling = 0;
            CpuProbeMustFail(() => new CpuProbeCoreAverages(map));
        });
        Test("CPU probe accepts PawnIO ABI 2.0 independently of the installed package version", () => {
            Assert(CpuProbeAbi.Supported(0x00020000) && CpuProbeAbi.Supported(0x00020200)
                && !CpuProbeAbi.Supported(0x00010000) && !CpuProbeAbi.Supported(0x00030000) && !CpuProbeAbi.Supported(0), "release version was confused with API compatibility");
        });
        Test("CPU probe validates Lenovo identity AMD signature and 8C16T independently", () => {
            Assert(CpuProbeSupported().Supported, "target rejected");
            foreach (Action<CpuProbeDevice> mutate in new Action<CpuProbeDevice>[] {
                d => { d.Model = "83XX"; d.Version = "Other Legion Go"; }, d => d.Manufacturer = "Other",
                d => d.CpuVendor = "GenuineIntel", d => d.CpuName = "AMD Ryzen Z1", d => d.Signature = 0x00B00F10,
                d => d.PhysicalCores = 16, d => d.LogicalProcessors = 8
            }) { var device = CpuProbeSupported(); mutate(device); Assert(!device.Supported, "wrong device bypassed gate"); }
        });
        Test("CPU probe topology uses Windows core masks rather than adjacent SMT assumptions", () => {
            var targets = CpuProbeTopology.Parse(CpuProbeMap());
            Assert(targets.Length == 16 && targets.Select(x => x.Core).Distinct().Count() == 8
                && targets[0].Logical == 0 && targets[1].Logical == 8 && targets[1].Sibling == 1
                && targets.Select(x => x.Key).Distinct().Count() == 16, "SMT became extra physical cores");
        });
        Test("CPU probe rejects malformed truncated overlapping and foreign-group topologies", () => {
            foreach (Action<byte[]> mutate in new Action<byte[]>[] {
                b => b[4] = 0, b => b[4] = 255, b => b[0] = 1, b => b[30] = 2, b => b[40] = 1,
                b => Array.Copy(b, 32, b, 48 + 32, 8), b => Array.Clear(b, 32, 8)
            }) { var data = CpuProbeMap(); mutate(data); CpuProbeMustFail(() => CpuProbeTopology.Parse(data)); }
            CpuProbeMustFail(() => CpuProbeTopology.Parse(CpuProbeMap().Take(383).ToArray()));
            CpuProbeMustFail(() => CpuProbeTopology.Parse(new byte[65537]));
            CpuProbeMustFail(() => CpuProbeTopology.Parse(null));
        });
        Test("CPU probe refuses unpinned module data without relying on a filename", () => {
            Assert(!CpuProbeModule.Accepts(null) && !CpuProbeModule.Accepts(new byte[CpuProbeModule.Size - 1])
                && !CpuProbeModule.Accepts(new byte[CpuProbeModule.Size]), "unrecognized module accepted");
            WithRecordingDirectory(folder => {
                string file = Path.Combine(folder, "AMDFamily17.bin"); File.WriteAllBytes(file, new byte[CpuProbeModule.Size]);
                CpuProbeMustFail(() => CpuProbeModule.Read(file));
            });
        });
        Test("CPU probe read boundary permits exactly three MSRs and no tuning registers", () => {
            var api = new CpuPawnFake();
            using (var reader = new CpuProbeReader(api, new byte[] { 1 })) {
                foreach (uint index in new[] { CpuProbeMsr.HardwarePstate, CpuProbeMsr.Aperf, CpuProbeMsr.Mperf }) Assert(reader.Read(index) == 123, "read lost");
                int reads = api.Reads.Count;
                foreach (uint index in new uint[] { 0, 0xC0010064, 0xC00102B3, 0xC0011020, 0xFFFFFFFF }) CpuProbeMustFail(() => reader.Read(index));
                Assert(api.Reads.Count == reads && reads == 3 && reader.Reads == 3, "arbitrary register reached transport");
            }
            Assert(api.Closes == 1, "executor leaked");
        });
        Test("CPU probe partial open or load failure closes handles exactly once", () => {
            foreach (bool openFail in new[] { false, true }) {
                var api = new CpuPawnFake { OpenError = openFail, LoadError = !openFail };
                CpuProbeMustFail(() => new CpuProbeReader(api, new byte[] { 1 }));
                Assert(api.Closes == 1 && api.Reads.Count == 0, "partial executor leaked or read after load failure");
            }
        });
        Test("CPU probe rejects failed HRESULTs and non-unit return counts without fake values", () => {
            foreach (ulong count in new ulong[] { 0, 2, ulong.MaxValue }) {
                var api = new CpuPawnFake { Count = count };
                using (var reader = new CpuProbeReader(api, new byte[] { 1 })) CpuProbeMustFail(() => reader.Read(CpuProbeMsr.Aperf));
            }
            var bad = new CpuPawnFake { ReadError = true };
            using (var reader = new CpuProbeReader(bad, new byte[] { 1 })) {
                CpuProbeMustFail(() => reader.Read(CpuProbeMsr.Aperf)); bad.ReadError = false;
                Assert(reader.Read(CpuProbeMsr.Aperf) == 123, "read failure poisoned subsequent independent read");
            }
        });
        Test("CPU probe dispose is idempotent and blocks reads even after a close failure", () => {
            var api = new CpuPawnFake { CloseError = true }; var reader = new CpuProbeReader(api, new byte[] { 1 });
            CpuProbeMustFail(reader.Dispose); reader.Dispose(); CpuProbeMustFail(() => reader.Read(CpuProbeMsr.Aperf));
            Assert(api.Closes == 1 && api.Reads.Count == 0, "closed executor reused");
        });
        Test("CPU effective frequency uses APERF deltas and monotonic elapsed time", () => {
            var counters = new CpuProbeCounters(1000);
            Assert(!counters.Observe("0:0", CpuCounter(100, 200, 1000)).EffectiveLogicalMHz.HasValue, "initial sample invented MHz");
            var value = counters.Observe("0:0", CpuCounter(1200000100, 600000200, 1500));
            Assert(value.EffectiveLogicalMHz == 2400 && value.AperfMperfRatio == 2 && value.State == "sampled", "counter units or ratio incorrect");
        });
        Test("CPU effective frequency preserves idle zero while leaving zero-denominator ratio unknown", () => {
            var counters = new CpuProbeCounters(1000); counters.Observe("a", CpuCounter(100, 100, 1000));
            var value = counters.Observe("a", CpuCounter(100, 100, 1500));
            Assert(value.EffectiveLogicalMHz == 0 && !value.AperfMperfRatio.HasValue && value.State == "no-cycles-observed", "idle sample became fake active clock");
        });
        Test("CPU counters reject reset wrap suspension gaps and repeated or backward timestamps", () => {
            foreach (var next in new[] { CpuCounter(99, 200, 1500), CpuCounter(200, 99, 1500), CpuCounter(200, 200, 1000),
                CpuCounter(200, 200, 900), CpuCounter(200, 200, 5000) }) {
                var counters = new CpuProbeCounters(1000); counters.Observe("a", CpuCounter(100, 100, 1000));
                Assert(counters.Observe("a", next).State == "discontinuity", "invalid interval became current MHz");
            }
        });
        Test("CPU counters reject implausible rates and inconsistent tuples then recover", () => {
            var counters = new CpuProbeCounters(1000); counters.Observe("a", CpuCounter(0, 0, 1000));
            Assert(!counters.Observe("a", CpuCounter(4000000000, 1, 1500)).EffectiveLogicalMHz.HasValue, "implausible rate accepted");
            Assert(counters.Observe("a", CpuCounter(4500000000, 500000001, 2000)).EffectiveLogicalMHz == 1000, "counter did not recover");
            counters.Observe("b", CpuCounter(0, 0, 1000));
            Assert(!counters.Observe("b", CpuCounter(1, 0, 1500)).EffectiveLogicalMHz.HasValue, "inconsistent denominator accepted");
        });
        Test("CPU read errors invalidate only the affected logical processor baseline", () => {
            var counters = new CpuProbeCounters(1000);
            counters.Observe("a", CpuCounter(0, 0, 1000)); counters.Observe("b", CpuCounter(0, 0, 1000)); counters.Invalidate("a");
            Assert(counters.Observe("a", CpuCounter(100, 100, 1500)).State == "warmup"
                && counters.Observe("b", CpuCounter(1000000000, 1000000000, 1500)).EffectiveLogicalMHz == 2000, "SMT baselines were mixed or stale");
        });
        Test("CPU session peaks retain observed maxima across siblings without summing SMT", () => {
            var peaks = new CpuProbePeaks();
            peaks.Observe(0, 100 | (8UL << 8), "t1"); // 2500 MHz
            var high = peaks.Observe(0, 204 | (8UL << 8), "t2"); // 5100 MHz
            var low = peaks.Observe(0, 80 | (8UL << 8), "t3");
            Assert(high.MHz == 5100 && low.MHz == 5100 && low.ObservedAtUtc == "t2" && peaks.GetAll().Length == 1, "peak became last clock or SMT sum");
        });
        Test("CPU session peaks reject invalid fields preserve history on errors and reset with a new session", () => {
            var peaks = new CpuProbePeaks();
            Assert(peaks.Observe(0, 0, "t0") == null && peaks.Observe(0, 255 | (1UL << 8), "t0") == null, "invalid status invented peak");
            peaks.Observe(0, 100 | (8UL << 8), "t1");
            var copy = peaks.Observe(0, null, "t2"); copy.MHz = 999;
            peaks.Observe(1, 120 | (8UL << 8), "t3");
            Assert(peaks.GetAll()[0].MHz == 2500 && peaks.GetAll()[0].ObservedAtUtc == "t1" && peaks.GetAll()[1].MHz == 3000
                && new CpuProbePeaks().GetAll().Length == 0, "peak history mutated or persisted across sessions");
        });
    }
    private sealed class CpuPawnFake : ICpuPawnApi
    {
        internal bool OpenError, LoadError, ReadError, CloseError;
        internal int Closes;
        internal ulong Count = 1;
        internal List<uint> Reads = new List<uint>();
        public int Open(out IntPtr handle) { handle = new IntPtr(42); return OpenError ? -1 : 0; }
        public int Load(IntPtr handle, byte[] module) => LoadError ? -2 : 0;
        public int Read(IntPtr handle, uint index, out ulong value, out ulong count) { Reads.Add(index); value = 123; count = Count; return ReadError ? -3 : 0; }
        public int Close(IntPtr handle) { Closes++; return CloseError ? -4 : 0; }
    }
}
