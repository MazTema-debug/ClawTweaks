// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LegionTweaks;
using LegionTweaks.Widget;
using Windows.UI.Xaml.Controls;

internal static class Program
{
    private static int passed;
    private static DeviceIdentity Supported() => new DeviceIdentity { Manufacturer = "LENOVO", Model = "83E10000MX", Version = "Legion Go 8APU1", Cpu = "AMD Ryzen Z1 Extreme" };
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static Request Set(string key, int value, bool ac = false) => new Request { Operation = "set", Key = key, Values = new[] { value }, Ac = ac };
    private static Request Tdp(params int[] watts) => new Request { Operation = "tdp", Values = watts };
    private static void Main()
    {
        Test("System switches re-enable after each refresh or apply while their parent is disabled", () =>
        {
            var parent = new Control();
            var apply = new Button { Parent = parent };
            var toggle = new ToggleSwitch { Parent = parent };
            foreach (int value in new[] { 0, 1, 0, 1, 0 })
            {
                // MainPage.Send disables the parent before rendering the new snapshot.
                parent.IsEnabled = false;
                TogglePresenter.Render(toggle, apply, value);
                Assert(!apply.IsEnabled && !toggle.IsEnabled, "busy panel allowed edits");
                parent.IsEnabled = true;
                Assert(apply.IsEnabled && toggle.IsEnabled, "valid switch stayed disabled after rendering " + value);
                Assert(toggle.IsOn == (value == 1), "switch does not show the returned value");
            }
        });
        Test("Unavailable or invalid switch readings stay disabled when the parent re-enables", () =>
        {
            var parent = new Control();
            var apply = new Button { Parent = parent };
            var toggle = new ToggleSwitch { Parent = parent };
            foreach (int? value in new int?[] { null, -1, 2, 255 })
            {
                parent.IsEnabled = false;
                TogglePresenter.Render(toggle, apply, value);
                parent.IsEnabled = true;
                Assert(!apply.IsEnabled && !toggle.IsEnabled, "unavailable switch enabled for " + value);
            }
        });
        Test("A valid switch reading recovers after an unavailable snapshot", () =>
        {
            var parent = new Control();
            var apply = new Button { Parent = parent };
            var toggle = new ToggleSwitch { Parent = parent };
            TogglePresenter.Render(toggle, apply, null);
            Assert(!apply.IsEnabled && !toggle.IsEnabled, "missing reading not disabled");
            parent.IsEnabled = false;
            TogglePresenter.Render(toggle, apply, 1);
            parent.IsEnabled = true;
            Assert(apply.IsEnabled && toggle.IsEnabled && toggle.IsOn, "fresh valid reading did not recover");
        });
        Test("Lenovo feature output accepts lower, upper and mixed-case property names", () =>
        {
            foreach (string key in new[] { "value", "Value", "VALUE", "vAlUe" })
            {
                var output = new WmiOutput(); output.Add(key, (uint)15); output.Add("ReturnValue", (uint)0);
                output.EnsureSuccess("GetFeatureValue");
                Assert(output.ReadInt32("Value") == 15, "feature value lost for " + key);
            }
        });
        Test("Mode and lighting outputs retain their numeric readings", () =>
        {
            var mode = new WmiOutput(); mode.Add("Data", (uint)1);
            var light = new WmiOutput(); light.Add("Current_Brightness_Level", (byte)2);
            Assert(mode.ReadInt32("Data") == 1 && light.ReadInt32("Current_Brightness_Level") == 2, "existing readings changed");
        });
        Test("WMI failure codes are rejected regardless of property casing", () =>
        {
            foreach (string key in new[] { "ReturnValue", "returnvalue", "RETURNVALUE" })
            {
                var output = new WmiOutput(); output.Add("value", 15); output.Add(key, (uint)5);
                bool rejected = false;
                try { output.EnsureSuccess("GetFeatureValue"); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("GetFeatureValue") && ex.Message.Contains("5"); }
                Assert(rejected, "firmware error hidden for " + key);
            }
        });
        Test("Missing and null WMI outputs remain errors instead of becoming zero", () =>
        {
            foreach (bool includeNull in new[] { false, true })
            {
                var output = new WmiOutput(); output.Add("ReturnValue", (uint)0);
                if (includeNull) output.Add("value", null);
                bool rejected = false;
                try { output.ReadInt32("Value"); }
                catch (InvalidOperationException ex) { rejected = ex.Message.Contains("Campos recibidos:") && ex.Message.Contains("ReturnValue"); }
                Assert(rejected, "missing output was accepted or lacked field diagnostics");
            }
            var zero = new WmiOutput(); zero.Add("value", 0);
            Assert(zero.ReadInt32("Value") == 0, "valid zero was rejected");
        });
        Test("Recognizes the original 83E1 Z1 Extreme", () => Assert(Supported().IsSupported, "identity"));
        Test("Rejects Z1, Z2E, Go S, other Lenovo and MSI", () =>
        {
            foreach (string cpu in new[] { "AMD Ryzen Z1", "AMD Ryzen Z2 Extreme", "Intel Core Ultra 5" })
            { var d = Supported(); d.Cpu = cpu; Assert(!d.IsSupported, cpu); }
            foreach (string model in new[] { "83N0", "83L3", "LNVNB161822", "MS-1T41" })
            { var d = Supported(); d.Model = model; d.Version = "unknown"; Assert(!d.IsSupported, model); }
            var other = Supported(); other.Manufacturer = "ASUS"; Assert(!other.IsSupported, "manufacturer");
        });
        Test("Startup and status make no hardware writes", () =>
        {
            var h = new Fake(); var j = new Journal(); var e = new ControlEngine(h, j);
            Assert(e.Execute(new Request { Operation = "status" }).Success, "status");
            Assert(h.Writes.Count == 0 && j.Saves == 0, "startup changed settings");
        });
        Test("Unsupported device rejects every mutation", () =>
        {
            var h = new Fake(); h.Identity.Cpu = "AMD Ryzen Z1"; var e = new ControlEngine(h, new Journal());
            Assert(!e.Execute(Tdp(15,15,15)).Success && !e.Execute(Set("Epp", 10)).Success, "write accepted");
            Assert(h.Writes.Count == 0, "hardware touched");
        });
        Test("Bad versions, operations and numeric setting names are rejected", () =>
        {
            var h = new Fake(); var e = new ControlEngine(h, new Journal());
            foreach (var r in new[] { new Request { Version = 99, Operation = "tdp", Values = new[] { 15,15,15 } }, new Request { Operation = "shell" }, Set("9", 1), Set("Spl", 15) })
                Assert(!e.Execute(r).Success, "accepted invalid message");
            Assert(h.Writes.Count == 0, "write occurred");
        });
        Test("Invalid and unordered TDP tuples never reach firmware", () =>
        {
            var h = new Fake(); var e = new ControlEngine(h, new Journal());
            foreach (var r in new[] { Tdp(4,15,15), Tdp(15,15,31), Tdp(20,15,25), Tdp(15,25,20), Tdp(15,15), Tdp() }) Assert(!e.Execute(r).Success, "accepted invalid TDP");
            Assert(h.Writes.Count == 0, "write occurred");
        });
        Test("Custom mode is acknowledged before limits, including slow firmware", () =>
        {
            var h = new Fake { ModeDelay = 4 }; var e = new ControlEngine(h, new Journal());
            Assert(e.Execute(Tdp(12,18,22)).Success, "apply failed");
            Assert(h.BadModeWrites == 0 && h.Data[Setting.Mode.ToString()] == 255, "mode order");
        });
        Test("Mode timeout does not write TDP registers", () =>
        {
            var h = new Fake { IgnoreCustom = true }; var e = new ControlEngine(h, new Journal());
            Assert(!e.Execute(Tdp(12,18,22)).Success, "mode timeout hidden");
            Assert(!h.Writes.Any(x => x.StartsWith("Spl") || x.StartsWith("Sppt") || x.StartsWith("Fppt")), "unconfirmed mode writes");
        });
        Test("Read failure prevents partial TDP application", () =>
        {
            var h = new Fake { FailRead = Setting.Sppt }; var e = new ControlEngine(h, new Journal());
            Assert(!e.Execute(Tdp(12,18,22)).Success && h.Writes.Count == 0, "partial application");
        });
        Test("Cannot write if the original settings cannot be persisted", () =>
        {
            var h = new Fake(); var j = new Journal { FailSave = true }; var e = new ControlEngine(h,j);
            Assert(!e.Execute(Tdp(12,18,22)).Success && h.Writes.Count == 0, "write without backup");
        });
        Test("Mid-batch failure rolls back mode and all TDP values", () =>
        {
            var h = new Fake { FailOnce = Setting.Sppt }; var e = new ControlEngine(h,new Journal());
            Assert(!e.Execute(Tdp(12,18,22)).Success, "error hidden");
            Assert(h.Data["Mode"] == 2 && h.Data["Spl"] == 15 && h.Data["Sppt"] == 20 && h.Data["Fppt"] == 25, "rollback incomplete");
        });
        Test("Firmware silently ignoring a write is reported as failure", () =>
        {
            var h = new Fake { IgnoreSetting = Setting.ChargeLimit }; var e = new ControlEngine(h,new Journal());
            Assert(!e.Execute(Set("ChargeLimit",1)).Success, "false success");
        });
        Test("Coupled-register drift cannot produce a false TDP success", () =>
        {
            var h = new Fake { DriftOnce = true }; var e = new ControlEngine(h,new Journal());
            Assert(!e.Execute(Tdp(12,18,22)).Success, "coupled drift hidden");
        });
        Test("AC/DC edits are isolated and preserve exact boost mode on restore", () =>
        {
            var h = new Fake(); var j = new Journal(); var e = new ControlEngine(h,j);
            Assert(e.Execute(Set("CpuBoost",0,true)).Success, "boost failed");
            Assert(h.Cpu(Setting.CpuBoost,true,"plan-A") == 0 && h.Cpu(Setting.CpuBoost,false,"plan-A") == 4, "DC overwritten");
            Assert(e.Execute(new Request { Operation = "restore" }).Success && h.Cpu(Setting.CpuBoost,true,"plan-A") == 4, "original mode lost");
        });
        Test("Restore survives helper restart and targets the original plan", () =>
        {
            var h = new Fake(); var j = new Journal(); var e = new ControlEngine(h,j);
            Assert(e.Execute(Set("Epp",20,true)).Success, "epp");
            h.ActiveScheme = "plan-B";
            e = new ControlEngine(h,j);
            Assert(e.Execute(new Request { Operation = "restore" }).Success, "restore");
            Assert(h.Cpu(Setting.Epp,true,"plan-A") == 50 && h.Cpu(Setting.Epp,true,"plan-B") == 50 && h.ActiveScheme == "plan-B", "wrong plan");
        });
        Test("Repeated edits never overwrite first originals", () =>
        {
            var h = new Fake(); var j = new Journal(); var e = new ControlEngine(h,j);
            e.Execute(Set("Epp",20)); e.Execute(Set("Epp",80));
            Assert(j.Values.Single().Value == 50, "original replaced");
        });
        Test("Leaving Custom mode retains the custom tuple for restore", () =>
        {
            var h = new Fake(); h.Data["Mode"] = 255; var j = new Journal(); var e = new ControlEngine(h,j);
            Assert(e.Execute(Set("Mode",1)).Success && j.Values.Count == 4, "tuple not saved");
            Assert(e.Execute(new Request { Operation = "restore" }).Success && h.Data["Mode"] == 255, "restore failed");
        });
        Test("Restoring Custom mode does not reset the restored power registers", () =>
        {
            var h = new Fake { ResetLimitsOnMode = true }; h.Data["Mode"] = 255;
            var e = new ControlEngine(h,new Journal());
            Assert(e.Execute(Set("Mode",1)).Success, "mode switch");
            Assert(e.Execute(new Request { Operation = "restore" }).Success, "restore");
            Assert(h.Data["Mode"] == 255 && h.Data["Spl"] == 15 && h.Data["Sppt"] == 20 && h.Data["Fppt"] == 25, "mode reset the tuple");
        });
        Test("A failed restore keeps its backup for retry", () =>
        {
            var h = new Fake(); var j = new Journal(); var e = new ControlEngine(h,j);
            e.Execute(Set("ChargeLimit",1)); h.IgnoreSetting = Setting.ChargeLimit;
            Assert(!e.Execute(new Request { Operation = "restore" }).Success && j.Values.Count == 1, "lost backup");
        });
        Test("Concurrent edits are serialized into complete tuples", () =>
        {
            var h = new Fake(); var e = new ControlEngine(h,new Journal());
            var results = new Response[2];
            Parallel.Invoke(() => results[0] = e.Execute(Tdp(10,12,15)), () => results[1] = e.Execute(Tdp(20,25,30)));
            Assert(results.All(x => x.Success), "concurrency");
            Assert(h.Data["Spl"] <= h.Data["Sppt"] && h.Data["Sppt"] <= h.Data["Fppt"], "torn tuple");
        });
        Test("JSON round trip preserves Unicode, arrays and protocol version", () =>
        {
            var r = new Response { Success = false, Message = "Carga \"80 %\" · conexión\nfalló", State = new Snapshot { Values = new[] { new Reading { Key = "CPU", Value = null, Error = "No disponible" } } } };
            var copy = Protocol.Decode<Response>(Protocol.Encode(r));
            Assert(copy.Message == r.Message && copy.State.Values[0].Value == null && copy.Version == Protocol.Version, "JSON");
        });
        Console.WriteLine(passed + " tests passed.");
    }
    private sealed class Journal : IJournal
    {
        public IList<OriginalValue> Values = new List<OriginalValue>(); public int Saves; public bool FailSave;
        public IList<OriginalValue> Load() => Values;
        public void Save(IList<OriginalValue> values) { if (FailSave) throw new Exception("disk full"); Saves++; Values = values.ToList(); }
    }
    private sealed class Fake : IHardware
    {
        public DeviceIdentity Identity { get; } = Supported();
        public bool OnAc => true;
        public string ActiveScheme { get; set; } = "plan-A";
        public Dictionary<string,int> Data = new Dictionary<string,int> { {"Mode",2},{"Spl",15},{"Sppt",20},{"Fppt",25},{"FanFullSpeed",0},{"ChargeLimit",0},{"PowerLight",1},{"OsPowerMode",1} };
        public List<string> Writes = new List<string>();
        public Setting? FailOnce, FailRead, IgnoreSetting; public bool IgnoreCustom, DriftOnce, ResetLimitsOnMode; public int ModeDelay, BadModeWrites;
        private int pending;
        private string Key(Address a) => a.Setting == Setting.CpuBoost || a.Setting == Setting.Epp ? a.Id : a.Setting.ToString();
        public int Cpu(Setting s, bool ac, string scheme) => Read(new Address { Setting = s, Ac = ac, Scheme = scheme });
        public int Read(Address a)
        {
            if (FailRead == a.Setting) throw new Exception("read failed");
            if (a.Setting == Setting.Mode && pending > 0 && --pending == 0) Data["Mode"] = 255;
            string key = Key(a);
            if (!Data.ContainsKey(key)) Data[key] = a.Setting == Setting.CpuBoost ? 4 : 50;
            return Data[key];
        }
        public void Write(Address a, int v)
        {
            Writes.Add(a.Setting + "=" + v);
            if (IgnoreSetting == a.Setting || (IgnoreCustom && a.Setting == Setting.Mode && v == 255)) return;
            if (a.Setting == Setting.Mode && v == 255 && ModeDelay > 0) { pending = ModeDelay; return; }
            if ((a.Setting == Setting.Spl || a.Setting == Setting.Sppt || a.Setting == Setting.Fppt) && Data["Mode"] != 255) BadModeWrites++;
            Data[Key(a)] = v;
            if (ResetLimitsOnMode && a.Setting == Setting.Mode) { Data["Spl"] = 18; Data["Sppt"] = 24; Data["Fppt"] = 30; }
            if (FailOnce == a.Setting) { FailOnce = null; throw new Exception("injected write failure"); }
            if (DriftOnce && a.Setting == Setting.Fppt) { DriftOnce = false; Data["Spl"] = 11; }
        }
        public Reading[] ReadTelemetry() => new Reading[0];
        public void Wait(int milliseconds) { }
    }
}
