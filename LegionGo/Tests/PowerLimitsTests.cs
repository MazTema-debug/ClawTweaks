// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using LegionTweaks;

internal static partial class Program
{
    private static void PowerLimitsTests()
    {
        Test("Power features map sustained to kind 2 and slow burst to kind 1 in each mode", () =>
        {
            foreach (int mode in new[] { 1, 2, 3, 255 })
            {
                Assert(PowerLimits.FeatureId(Setting.Spl, mode) == (0x01020000u | ((uint)mode << 8)), "SPL reversed");
                Assert(PowerLimits.FeatureId(Setting.Sppt, mode) == (0x01010000u | ((uint)mode << 8)), "SPPT reversed");
                Assert(PowerLimits.FeatureId(Setting.Fppt, mode) == (0x01030000u | ((uint)mode << 8)), "FPPT selector");
            }
        });
        Test("Active reads select applied mode and preserve asymmetric Custom cells without writes", () =>
        {
            foreach (int mode in new[] { 1, 2, 3, 255 }) foreach (bool ac in new[] { false, true })
            {
                var h = new PowerFake { Mode = mode, Ac = ac };
                var response = new ControlEngine(h, new Journal()).Execute(new Request { Operation = "telemetry" });
                Assert(response.State.PowerMappingVersion == 1 && response.State.OnAc == ac, "context missing");
                Assert(TelemetryValue.Value(response.State, "Spl") == 11 && TelemetryValue.Value(response.State, "Sppt") == 19, "stored tuple mixed");
                int expected = mode == 255 ? 11 : new[] { 8, 15, 20 }[mode - 1];
                Assert(TelemetryValue.Value(response.State, "ActiveSpl") == expected && h.Selectors.All(x => x == mode), "Custom substituted for active mode");
                int burst = mode == 255 ? 19 : ac ? new[] { 15, 25, 30 }[mode - 1] : expected;
                Assert(TelemetryValue.Value(response.State, "ActiveSppt") == burst, "AC/DC burst mixed");
                Assert(TelemetryValue.DescribeLimits(response.State).Contains("Límites del modo activo"), "active label missing");
                Assert(h.Writes == 0, "read wrote hardware");
            }
        });
        Test("Active context rejects mid-capture Lenovo and AC/DC changes", () =>
        {
            foreach (bool acChange in new[] { false, true })
            {
                var h = new PowerFake();
                var readings = PowerLimits.ReadActive(h, (s, m) => { if (acChange) h.Ac = false; else h.Mode = 255; return 20; });
                Assert(readings.All(x => !x.Value.HasValue && x.Error != null), "mixed context accepted");
            }
        });
        Test("Unknown active mode never queries Custom or invents limits", () =>
        {
            var h = new PowerFake { Mode = 7 };
            var readings = h.ReadActivePowerLimits();
            Assert(h.Selectors.Count == 0 && readings.All(x => x.Error != null && x.Value == null), "unknown mode fell back");
        });
        Test("Active getter failure is isolated and stored cells remain independent", () =>
        {
            var h = new PowerFake();
            var values = PowerLimits.ReadActive(h, (s, m) => s == Setting.Sppt ? throw new Exception("failed") : 20);
            Assert(values.Single(x => x.Key == "ActiveSppt").Value == null && values.Single(x => x.Key == "ActiveSpl").Value == 20, "failure fabricated tuple");
        });
        Test("Legacy unequal originals restore the same physical registers and retain AC/DC plan", () =>
        {
            var old = new List<OriginalValue> {
                new OriginalValue { Address = new Address { Setting = Setting.Spl }, Value = 23 },
                new OriginalValue { Address = new Address { Setting = Setting.Sppt }, Value = 12 },
                new OriginalValue { Address = new Address { Setting = Setting.Fppt }, Value = 29 },
                new OriginalValue { Address = new Address { Setting = Setting.CpuBoost, Ac = true, Scheme = "plan-B" }, Value = 2 }
            };
            old = Protocol.Decode<List<OriginalValue>>(Protocol.Encode(old));
            var j = new Journal { Values = old }; var h = new Fake();
            var engine = new ControlEngine(h, j);
            Assert(j.Saves == 0 && old[0].Address.Setting == Setting.Spl, "read rewrote legacy journal");
            Assert(engine.Execute(new Request { Operation = "restore" }).Success, "legacy restore failed");
            Assert(h.Data["Spl"] == 12 && h.Data["Sppt"] == 23 && h.Data["Fppt"] == 29, "legacy register identity lost");
            Assert(h.Cpu(Setting.CpuBoost, true, "plan-B") == 2, "plan or AC changed");
        });
        Test("New originals survive serialization and another startup without a second swap", () =>
        {
            var j = new Journal(); var h = new Fake();
            Assert(new ControlEngine(h, j).Execute(Tdp(10, 18, 28)).Success, "apply failed");
            j.Values = Protocol.Decode<List<OriginalValue>>(Protocol.Encode(j.Values.ToList()));
            Assert(j.Values.Where(x => PowerLimits.IsLimit(x.Address.Setting)).All(x => x.PowerMappingVersion == 1), "new journal unversioned");
            Assert(new ControlEngine(h, j).Execute(new Request { Operation = "restore" }).Success, "new restore failed");
            Assert(h.Data["Spl"] == 15 && h.Data["Sppt"] == 20 && h.Data["Fppt"] == 25, "new original swapped");
        });
        Test("Asymmetric TDP lowers sustained first and raises peak first", () =>
        {
            var h = new Fake(); var e = new ControlEngine(h, new Journal());
            Assert(e.Execute(Tdp(10, 18, 22)).Success, "lower failed");
            Assert(h.Writes.Where(x => !x.StartsWith("Mode")).SequenceEqual(new[] { "Spl=10", "Sppt=18", "Fppt=22" }), "lower order");
            h.Writes.Clear(); Assert(e.Execute(Tdp(16, 24, 30)).Success, "raise failed");
            Assert(h.Writes.SequenceEqual(new[] { "Fppt=30", "Sppt=24", "Spl=16" }), "raise order");
        });
        Test("Journal migration preserves exact legacy bytes and restores corrected semantics after restart", () =>
        {
            WithPowerJournal(path => {
                var legacy = new List<OriginalValue> {
                    new OriginalValue { Address = new Address { Setting = Setting.Spl }, Value = 23 },
                    new OriginalValue { Address = new Address { Setting = Setting.Sppt }, Value = 12 },
                    new OriginalValue { Address = new Address { Setting = Setting.Fppt }, Value = 29 }
                };
                string bytes = " \n" + Protocol.Encode(legacy);
                File.WriteAllText(path, bytes);
                var h = new Fake(); var engine = new ControlEngine(h, new FileJournal(path));
                Assert(!File.Exists(path + ".before-power-mapping-v1"), "read created backup");
                Assert(engine.Execute(Tdp(10, 18, 28)).Success, "migration apply failed");
                Assert(File.ReadAllText(path + ".before-power-mapping-v1") == bytes, "legacy bytes changed");
                var disk = new FileJournal(path).Load();
                Assert(disk.Single(x => x.Address.Setting == Setting.Spl).Value == 23
                    && disk.Single(x => x.Address.Setting == Setting.Sppt).Value == 12,
                    "older helper would restore a different physical cell");
                Assert(disk.Where(x => PowerLimits.IsLimit(x.Address.Setting)).All(x => x.PowerMappingVersion == 2), "physical disk format not identified");
                Assert(new ControlEngine(h, new FileJournal(path)).Execute(new Request { Operation = "restore" }).Success, "disk restart restore failed");
                Assert(h.Data["Spl"] == 12 && h.Data["Sppt"] == 23 && h.Data["Fppt"] == 29, "disk migration swapped again");
                Assert(new FileJournal(path).Load().Count == 0 && File.ReadAllText(path + ".before-power-mapping-v1") == bytes, "restore lost backup");
            });
        });
        Test("Journal save failure prevents hardware writes and preserves the original document", () =>
        {
            WithPowerJournal(path => {
                File.WriteAllText(path, "[]"); var h = new Fake();
                var engine = new ControlEngine(h, new FileJournal(path));
                using (var blocked = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                    Assert(!engine.Execute(Tdp(10, 18, 28)).Success && h.Writes.Count == 0, "write preceded persistence");
                Assert(File.ReadAllText(path) == "[]", "failed save replaced original");
            });
        });
    }
    private static void WithPowerJournal(Action<string> body)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LegionTweaks-PowerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { body(Path.Combine(directory, "originals.json")); }
        finally { foreach (string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
    }
    private sealed class PowerFake : IHardware, IActivePowerHardware
    {
        public int Mode = 3, Writes; public bool Ac = true;
        public List<int> Selectors = new List<int>();
        public DeviceIdentity Identity => Supported();
        public bool OnAc => Ac;
        public string ActiveScheme => "test";
        public int Read(Address a) => a.Setting == Setting.Mode ? Mode : a.Setting == Setting.Spl ? 11 : a.Setting == Setting.Sppt ? 19 : 27;
        public Reading[] ReadActivePowerLimits() => PowerLimits.ReadActive(this, (s, m) => {
            Selectors.Add(m);
            if (m == 255) return Read(new Address { Setting = s });
            return s == Setting.Spl ? new[] { 8, 15, 20 }[m - 1]
                : s == Setting.Sppt ? (Ac ? new[] { 15, 25, 30 }[m - 1] : new[] { 8, 15, 20 }[m - 1])
                : new[] { 20, 30, 35 }[m - 1];
        });
        public void Write(Address a, int value) { Writes++; }
        public Reading[] ReadTelemetry() => new Reading[0];
        public void Wait(int ms) { }
    }
}
