// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static void AmdGraphicsTests()
    {
        Test("AMD construction and unsupported inspection do not open a driver or write a journal", () => {
            int opens = 0; var backup = new AmdMemoryJournal();
            var control = new AmdGraphicsControl(false, () => { opens++; return new AmdGraphicsFake(); }, backup);
            Assert(!control.Inspect().Available && opens == 0 && backup.Saves == 0, "unsupported access");
        });
        Test("AMD inspection only reads and preserves unknown game activity", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal();
            var control = new AmdGraphicsControl(true, () => native, journal);
            var state = control.Inspect();
            Assert(state.Available && state.ActiveInGame == null && state.Revision.Length == 64 && native.Writes.Count == 0 && journal.Saves == 0, "inspection mutated state or invented activity");
            string revision = state.Revision; state.CapturedAtUtc = DateTime.UtcNow.AddDays(-2).ToString("o");
            Assert(state.Fingerprint() == revision, "timestamp altered revision");
        });
        Test("AMD unavailable runtime is reported without invented feature values", () => {
            var state = new AmdGraphicsControl(true, () => { throw new DllNotFoundException(); }, new AmdMemoryJournal()).Inspect();
            Assert(!state.Available && state.Values.Length == 0 && state.Revision == null, "runtime error became a false feature reading");
        });
        Test("AMD rejects an obsolete selection before saving originals or writing", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            var request = AmdSelection(control, "rsr", 1); native.State.Find("afmf").Value = 1;
            AmdFailure(() => control.Apply(request), "cambiaron");
            Assert(native.Writes.Count == 0 && journal.Saves == 0, "stale request was applied");
        });
        Test("AMD durable originals are required before a native change", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal { FailSave = true }; var control = AmdControl(native, journal);
            AmdFailure(() => control.Apply(AmdSelection(control, "afmf", 1)));
            Assert(native.Writes.Count == 0, "write preceded durable backup");
        });
        Test("AMD explicit AFMF change is read back and originals survive later changes", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            var first = control.Apply(AmdSelection(control, "afmf", 1));
            var second = control.Apply(new AmdGraphicsRequest { Revision = first.Revision, Key = "rsr_sharpness", Value = 30 });
            Assert(first.Find("afmf").Value == 1 && second.Find("rsr_sharpness").Value == 30 && second.HasOriginals
                && journal.Value.Original.Find("afmf").Value == 0 && journal.Value.Original.Find("rsr_sharpness").Value == 80, "original values were replaced");
        });
        Test("AMD restores RSR dependencies as well as its enable flag", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            string original = native.State.Fingerprint();
            var enabled = control.Apply(AmdSelection(control, "rsr", 1));
            Assert(enabled.Find("display:1:gpu_scaling").Value == 1 && enabled.Find("gpu:1:boost").Value == 0, "driver dependencies were not simulated");
            var restored = control.Restore(new AmdGraphicsRequest { Revision = enabled.Revision });
            Assert(restored.Fingerprint() == original && journal.Value == null && !restored.HasOriginals, "dependency originals were not restored");
        });
        Test("AMD failed readback attempts rollback instead of reporting success", () => {
            var native = new AmdGraphicsFake { IgnoreWrites = true }; var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            AmdFailure(() => control.Apply(AmdSelection(control, "afmf", 1)), "recuperada");
            Assert(native.State.Find("afmf").Value == 0 && journal.Value == null, "unconfirmed change was retained");
        });
        Test("AMD partial RSR failure restores the entire previous configuration", () => {
            var native = new AmdGraphicsFake { FailWrites = 1 }; var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            string original = native.State.Fingerprint();
            AmdFailure(() => control.Apply(AmdSelection(control, "rsr", 1)), "recuperada");
            Assert(native.State.Fingerprint() == original && journal.Value == null, "partial dependency change remained");
        });
        Test("AMD concurrent unrelated change is retained while the requested change is undone", () => {
            var native = new AmdGraphicsFake { ChangeUnrelatedOnWrite = true }; var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            AmdFailure(() => control.Apply(AmdSelection(control, "afmf", 1)), "ajenos");
            Assert(native.State.Find("afmf").Value == 0 && native.State.Find("rsr_sharpness").Value == 25
                && journal.Value.Pending && journal.Value.Expected == null, "unrelated concurrent change was overwritten or auto-accepted");
        });
        Test("AMD failed rollback leaves a durable recovery and rejects further edits", () => {
            var native = new AmdGraphicsFake { FailWrites = 99 }; var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            AmdFailure(() => control.Apply(AmdSelection(control, "rsr", 1)), "pendiente");
            Assert(journal.Value.Pending && journal.Value.Original.Find("rsr").Value == 0, "recovery backup lost");
            int writes = native.Writes.Count;
            AmdFailure(() => control.Apply(AmdSelection(control, "afmf", 1)), "pendiente");
            Assert(native.Writes.Count == writes, "new edit bypassed pending recovery");
            native.FailWrites = 0;
            var restored = control.Restore(new AmdGraphicsRequest { Revision = control.Inspect().Revision });
            Assert(!restored.HasOriginals && journal.Value == null, "explicit recovery failed");
        });
        Test("AMD restoration refuses to overwrite a later external change", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            control.Apply(AmdSelection(control, "afmf", 1)); native.State.Find("rsr_sharpness").Value = 20;
            var current = control.Inspect(); int writes = native.Writes.Count;
            AmdFailure(() => control.Restore(new AmdGraphicsRequest { Revision = current.Revision }), "externos");
            Assert(native.Writes.Count == writes && journal.Value != null, "foreign configuration overwritten");
        });
        Test("AMD general restoration is lazy without originals and restores a durable backup", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); int opens = 0;
            var control = new AmdGraphicsControl(true, () => { opens++; return native; }, journal);
            Assert(AmdGraphicsControl.RestoreSaved(control) == null && opens == 0, "general restore unnecessarily initialized ADLX");
            control.Apply(AmdSelection(control, "afmf", 1));
            var restored = AmdGraphicsControl.RestoreSaved(control);
            Assert(restored.Find("afmf").Value == 0 && journal.Value == null, "general restore left AMD settings applied");
        });
        Test("AMD validates the actual driver sharpness range and increment", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            native.State.Find("rsr_sharpness").Step = 5;
            foreach (int value in new[] { -1, 101, 23 }) AmdFailure(() => control.Apply(AmdSelection(control, "rsr_sharpness", value)), "Nitidez");
            Assert(native.Writes.Count == 0 && journal.Saves == 0, "invalid sharpness reached the driver");
        });
        Test("AMD no-op does not save a backup or invoke a setter", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            var result = control.Apply(AmdSelection(control, "afmf", 0));
            Assert(!result.HasOriginals && journal.Saves == 0 && native.Writes.Count == 0, "no-op caused a write");
        });
        Test("AMD unknown dependency or ambiguous topology blocks all native writes", () => {
            foreach (int kind in new[] { 0, 1, 2 }) {
                var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
                if (kind == 0) native.State.GpuCount = 2;
                if (kind == 1) native.State.Find("gpu:1:boost").Value = null;
                if (kind == 2) native.State.Find("afmf").Supported = false;
                AmdFailure(() => control.Apply(AmdSelection(control, "afmf", 1)));
                Assert(native.Writes.Count == 0 && journal.Saves == 0, "unsupported configuration was changed");
            }
        });
        Test("AMD wire contracts round trip and reject arbitrary native addresses", () => {
            var request = new Request { Operation = "amd_graphics_apply", AmdGraphics = new AmdGraphicsRequest { Revision = new string('a', 64), Key = "afmf", Value = 1 } };
            Assert(Protocol.Decode<Request>(Protocol.Encode(request)).AmdGraphics.Value == 1, "AMD request lost");
            var response = new Response { AmdGraphics = new AmdGraphicsFake().Read(), SupportsAmdGraphics = true };
            Assert(Protocol.Decode<Response>(Protocol.Encode(response)).AmdGraphics.Find("afmf").Value == 0, "AMD response lost");
            foreach (string key in new[] { "gpu:1:boost", "gpu_scaling", "AFMF", "", null }) {
                request.AmdGraphics.Key = key; AmdFailure(() => request.AmdGraphics.Validate(false));
            }
        });
        Test("AMD restoration handles integer scaling support changing with GPU scaling", () => {
            var native = new AmdGraphicsFake { DynamicIntegerSupport = true };
            native.State.Find("display:1:integer_scaling").Supported = false; native.State.Find("display:1:integer_scaling").Value = 0;
            var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal); string original = native.State.Fingerprint();
            var enabled = control.Apply(AmdSelection(control, "rsr", 1));
            Assert(enabled.Find("display:1:integer_scaling").Supported == true, "dynamic support not exercised");
            Assert(control.Restore(new AmdGraphicsRequest { Revision = enabled.Revision }).Fingerprint() == original, "conditional support prevented restoration");
        });
        Test("AMD AFMF restoration does not toggle an unrelated enabled RSR", () => {
            var native = new AmdGraphicsFake(); native.State.Find("rsr").Value = 1; native.State.Find("display:1:gpu_scaling").Value = 1;
            native.State.Find("gpu:1:boost").Value = 0; native.State.Find("gpu:1:image_sharpening").Value = 0;
            var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            var enabled = control.Apply(AmdSelection(control, "afmf", 1)); native.Writes.Clear();
            control.Restore(new AmdGraphicsRequest { Revision = enabled.Revision });
            Assert(native.Writes.SequenceEqual(new[] { "afmf=0" }), "restoring AFMF changed unrelated RSR");
        });
        Test("AMD captures dormant integer scaling before RSR can overwrite it", () => {
            var native = new AmdGraphicsFake { DynamicIntegerSupport = true, HideIntegerWhileOff = true, StoredInteger = 1 };
            native.State.Find("display:1:integer_scaling").Supported = false; native.State.Find("display:1:integer_scaling").Value = null;
            var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal); string original = native.State.Fingerprint();
            var enabled = control.Apply(AmdSelection(control, "rsr", 1));
            Assert(native.Writes[0] == "display:1:gpu_scaling=1" && journal.Value.DormantOriginals.Single().Value == 1
                && enabled.Find("display:1:integer_scaling").Value == 0, "latent original lost before RSR");
            var restored = control.Restore(new AmdGraphicsRequest { Revision = enabled.Revision });
            Assert(restored.Fingerprint() == original && native.StoredInteger == 1, "latent integer state was not restored before GPU scaling off");
        });
        Test("AMD preparatory scaling failure never sends the RSR command", () => {
            var native = new AmdGraphicsFake { DynamicIntegerSupport = true, HideIntegerWhileOff = true, StoredInteger = 1, FailWrites = 1 };
            native.State.Find("display:1:integer_scaling").Supported = false; native.State.Find("display:1:integer_scaling").Value = null;
            var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal); string original = native.State.Fingerprint();
            AmdFailure(() => control.Apply(AmdSelection(control, "rsr", 1)), "recuperada");
            Assert(!native.Writes.Contains("rsr=1") && native.State.Fingerprint() == original && native.StoredInteger == 1, "preparatory failure lost settings");
        });
        Test("AMD topology changes prevent restoration and preserve originals", () => {
            var native = new AmdGraphicsFake(); var journal = new AmdMemoryJournal(); var control = AmdControl(native, journal);
            control.Apply(AmdSelection(control, "afmf", 1)); native.State.Topology = "different-display"; int writes = native.Writes.Count;
            AmdFailure(() => control.Restore(new AmdGraphicsRequest { Revision = control.Inspect().Revision }));
            Assert(native.Writes.Count == writes && journal.Value != null, "changed topology overwritten");
        });
        Test("AMD session rejects cross-module payloads before any backend access", () => {
            string folder = Path.Combine(Path.GetTempPath(), "LegionTweaks-amd-session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                var native = new AmdGraphicsFake(); var backup = new AmdMemoryJournal(); int opens = 0;
                var control = new AmdGraphicsControl(true, () => { opens++; return native; }, backup);
                using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, automaticSampling: false, amdGraphics: control)) {
                    var status = session.Execute(new Request { Operation = "status" });
                    Assert(status.SupportsAmdGraphics && opens == 0, "general status initialized ADLX");
                    foreach (var request in new[] {
                        new Request { Operation = "amd_graphics_status", Values = new[] { 1 } },
                        new Request { Operation = "amd_graphics_status", AmdGraphics = new AmdGraphicsRequest() },
                        new Request { Operation = "amd_graphics_apply", Display = new DisplayRequest(), AmdGraphics = new AmdGraphicsRequest() },
                        new Request { Operation = "set", Key = "boost", AmdGraphics = new AmdGraphicsRequest() },
                        new Request { Operation = "recording_status", AmdGraphics = new AmdGraphicsRequest() }
                    }) Assert(!session.Execute(request).Success, "mixed payload accepted");
                    Assert(opens == 0 && native.Writes.Count == 0, "rejected payload accessed ADLX");
                    Assert(session.Execute(new Request { Operation = "amd_graphics_status" }).AmdGraphics.Available && opens == 1, "explicit inspection missing");
                }
            }
            finally { Directory.Delete(folder, true); }
        });
        Test("AMD file journal persists originals and fails closed on corrupt content", () => {
            string folder = Path.Combine(Path.GetTempPath(), "LegionTweaks-amd-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                var journal = new AmdGraphicsJournal(folder); var state = new AmdGraphicsFake().Read();
                journal.Save(new AmdGraphicsBackup { Original = state, Expected = state, Pending = true });
                Assert(journal.Load().Pending && journal.Load().Original.Find("rsr_sharpness").Value == 80, "durable original missing");
                journal.Save(null); Assert(journal.Load() == null, "journal did not clear");
                File.WriteAllText(Path.Combine(folder, "amd-graphics-originals.json"), "{}");
                AmdFailure(() => journal.Load());
                Assert(File.ReadAllText(Path.Combine(folder, "amd-graphics-originals.json")) == "{}", "corrupt journal replaced");
            }
            finally { Directory.Delete(folder, true); }
        });
    }
    private static AmdGraphicsControl AmdControl(AmdGraphicsFake native, AmdMemoryJournal journal) => new AmdGraphicsControl(true, () => native, journal);
    private static AmdGraphicsRequest AmdSelection(AmdGraphicsControl control, string key, int value) => new AmdGraphicsRequest { Revision = control.Inspect().Revision, Key = key, Value = value };
    private static void AmdFailure(Action action, string text = null)
    {
        try { action(); } catch (Exception ex) { if (text != null) Assert(ex.Message.Contains(text), "unexpected AMD failure: " + ex.Message); return; }
        throw new Exception("AMD operation should have failed");
    }
    private sealed class AmdMemoryJournal : IAmdGraphicsJournal
    {
        internal AmdGraphicsBackup Value; internal int Saves; internal bool FailSave;
        public AmdGraphicsBackup Load() => Value == null ? null : Protocol.Decode<AmdGraphicsBackup>(Protocol.Encode(Value));
        public void Save(AmdGraphicsBackup value) { if (FailSave) throw new IOException("disk full"); Saves++; Value = value == null ? null : Protocol.Decode<AmdGraphicsBackup>(Protocol.Encode(value)); }
    }
    private sealed class AmdGraphicsFake : IAmdGraphicsBackend
    {
        internal readonly List<string> Writes = new List<string>();
        internal int FailWrites, StoredInteger; internal bool IgnoreWrites, DynamicIntegerSupport, HideIntegerWhileOff, ChangeUnrelatedOnWrite;
        internal readonly AmdGraphicsStatus State = new AmdGraphicsStatus { Available = true, GpuCount = 1, DisplayCount = 1, RuntimeVersion = "1.5.0.124", Topology = "gpu1|display1",
            Values = new[] { AmdValue("rsr", 0), AmdValue("afmf", 0), new AmdGraphicsValue { Key = "rsr_sharpness", Supported = true, Value = 80, Minimum = 0, Maximum = 100, Step = 1 },
                AmdValue("display:1:gpu_scaling", 0), AmdValue("display:1:integer_scaling", 0), AmdValue("display:1:scaling_mode", 0), AmdValue("gpu:1:boost", 1), AmdValue("gpu:1:image_sharpening", 1), AmdValue("gpu:1:chill", 0) } };
        private static AmdGraphicsValue AmdValue(string key, int value) => new AmdGraphicsValue { Key = key, Supported = true, Value = value };
        public AmdGraphicsStatus Read() { var value = Protocol.Decode<AmdGraphicsStatus>(Protocol.Encode(State)); value.CapturedAtUtc = DateTime.UtcNow.ToString("o"); return value; }
        public void Write(string key, int value)
        {
            Writes.Add(key + "=" + value);
            if (!IgnoreWrites) {
                State.Find(key).Value = value;
                if (ChangeUnrelatedOnWrite) { ChangeUnrelatedOnWrite = false; State.Find("rsr_sharpness").Value = 25; }
                if (key == "display:1:integer_scaling") StoredInteger = value;
                if (key == "rsr" && value == 1) {
                    State.Find("display:1:gpu_scaling").Value = 1; State.Find("display:1:integer_scaling").Value = 0;
                    State.Find("gpu:1:boost").Value = 0; State.Find("gpu:1:image_sharpening").Value = 0;
                    StoredInteger = 0;
                }
                if (DynamicIntegerSupport && (key == "rsr" && value == 1 || key == "display:1:gpu_scaling")) {
                    bool scaling = State.Find("display:1:gpu_scaling").Value == 1;
                    State.Find("display:1:integer_scaling").Supported = scaling;
                    State.Find("display:1:integer_scaling").Value = scaling ? StoredInteger : HideIntegerWhileOff ? (int?)null : 0;
                }
            }
            if (FailWrites > 0) { FailWrites--; throw new InvalidOperationException("driver rejected after a partial write"); }
        }
        public void Dispose() { }
    }
}
