// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LegionTweaks;

internal static partial class Program
{
    private static void LegionSpaceTests()
    {
        Test("Legion Space construction status and repeated polls do not write or create originals", () => {
            var b = new SpaceBackend(); var j = new SpaceJournal(); var control = new LegionSpaceControl(b, j, true);
            Assert(b.Reads == 0, "constructor accessed service");
            var status = control.GetStatus(); control.GetStatus();
            Assert(status.Available && status.CanChange && status.Original == null && status.State.Running, "status lost real state");
            Assert(b.Writes.Count == 0 && j.Saves == 0, "status changed service");
        });
        Test("Unsupported model and invalid service commands never reach the backend", () => {
            var b = new SpaceBackend(); var j = new SpaceJournal(); var control = new LegionSpaceControl(b, j, false);
            Assert(!control.GetStatus().Available, "unsupported read enabled UI");
            MustFail(() => control.Execute("space_disable"));
            MustFail(() => new LegionSpaceControl(b, j, true).Execute("stop_any_service"));
            Assert(b.Reads == 0 && b.Writes.Count == 0 && j.Saves == 0, "unsupported command reached service");
        });
        Test("Disable and restore retain automatic delayed startup and running state across restart", () => {
            var b = new SpaceBackend(); b.State.DelayedAutoStart = true; var original = b.State.Copy(); var j = new SpaceJournal();
            new LegionSpaceControl(b, j, true).Execute("space_disable");
            Assert(!b.State.Running && b.State.StartMode == 4 && b.State.DelayedAutoStart, "disable not verified");
            var restarted = new LegionSpaceControl(b, j, true); restarted.Execute("space_start");
            Assert(b.State.StartMode == 3 && b.State.Running && j.Saves == 1, "manual start replaced original");
            restarted.Execute("space_restore");
            Assert(b.State.SameAs(original) && j.Original == null, "restore guessed original startup or run state");
        });
        Test("An originally disabled Legion Space stays disabled when explicitly restored", () => {
            var b = new SpaceBackend { State = new LegionSpaceState { StartMode = 4, Running = false } }; var j = new SpaceJournal();
            var control = new LegionSpaceControl(b, j, true); control.Execute("space_start"); control.Execute("space_restore");
            Assert(b.State.StartMode == 4 && !b.State.Running, "restore unexpectedly enabled OEM service");
        });
        Test("Disabled but running originals are restored without starting a disabled service", () => {
            var b = new SpaceBackend { State = new LegionSpaceState { StartMode = 4, Running = true, DelayedAutoStart = true } };
            var original = b.State.Copy(); var control = new LegionSpaceControl(b, new SpaceJournal(), true);
            control.Execute("space_disable"); control.Execute("space_restore");
            Assert(b.State.SameAs(original), "unusual legal Windows state was lost");
        });
        Test("An unavailable service and corrupt backup never authorize service writes", () => {
            var b = new SpaceBackend { FailRead = true }; var j = new SpaceJournal(); var control = new LegionSpaceControl(b, j, true);
            Assert(!control.GetStatus().Available && !control.GetStatus().CanChange, "unavailable service appears actionable");
            MustFail(() => control.Execute("space_disable")); b.FailRead = false; j.FailLoad = true;
            Assert(control.GetStatus().Available && !control.GetStatus().CanChange, "corrupt backup was ignored");
            MustFail(() => control.Execute("space_start"));
            Assert(b.Writes.Count == 0, "failure changed OEM state");
        });
        Test("Service backup failure prevents stop and configuration changes", () => {
            var b = new SpaceBackend(); var j = new SpaceJournal { FailSave = true };
            MustFail(() => new LegionSpaceControl(b, j, true).Execute("space_disable"));
            Assert(b.Writes.Count == 0 && b.State.Running, "service stopped without durable original");
        });
        Test("Partial service configuration failure rolls back exact pre-operation state", () => {
            var b = new SpaceBackend { FailConfigureOnce = true }; var original = b.State.Copy(); var j = new SpaceJournal();
            MustFail(() => new LegionSpaceControl(b, j, true).Execute("space_disable"));
            Assert(b.State.SameAs(original) && j.Original.State.SameAs(original), "rollback lost state or original");
        });
        Test("Silent service rejection is not reported as success", () => {
            var b = new SpaceBackend { IgnoreConfigureOnce = true }; var original = b.State.Copy();
            MustFail(() => new LegionSpaceControl(b, new SpaceJournal(), true).Execute("space_disable"));
            Assert(b.State.SameAs(original), "verification failed to roll back stopped service");
        });
        Test("Failed service rollback retains its original and reports pending recovery", () => {
            var b = new SpaceBackend { FailConfigureAlways = true }; var j = new SpaceJournal();
            string error = MustFail(() => new LegionSpaceControl(b, j, true).Execute("space_disable"));
            Assert(j.Original != null && error.Contains("Recuperación pendiente"), "incomplete recovery was hidden");
        });
        Test("Service restore without a backup and invalid journal versions do not mutate", () => {
            var b = new SpaceBackend(); var j = new SpaceJournal(); var control = new LegionSpaceControl(b, j, true);
            MustFail(() => control.Execute("space_restore"));
            j.Original = new LegionSpaceOriginal { Version = 99, State = b.State.Copy() };
            MustFail(() => control.Execute("space_disable"));
            Assert(b.Writes.Count == 0, "invalid restoration changed service");
        });
        Test("Service restore leaves backup recoverable if cleanup cannot complete", () => {
            var b = new SpaceBackend(); var original = b.State.Copy(); var j = new SpaceJournal(); var control = new LegionSpaceControl(b, j, true);
            control.Execute("space_disable"); j.FailClear = true; MustFail(() => control.Execute("space_restore"));
            Assert(b.State.SameAs(original) && j.Original != null, "cleanup failure undid restoration or erased backup");
        });
        Test("Service IPC is separate from power edits and periodic recording polls", () => {
            var h = new Fake(); var b = new SpaceBackend(); var space = new LegionSpaceControl(b, new SpaceJournal(), true);
            using (var session = new HelperSession(new ControlEngine(h, new Journal()), Path.GetTempPath(), false, legionSpace: space))
            {
                Assert(session.Execute(new Request { Operation = "space_disable" }).Success, "IPC action failed");
                Assert(h.Writes.Count == 0 && !session.Recorder.GetStatus().Active, "service action touched performance or recording");
                int reads = b.Reads; var poll = session.Execute(new Request { Operation = "recording_status" });
                Assert(b.Reads == reads && poll.LegionSpace == null, "2s poll unnecessarily queried SCM");
                int writes = b.Writes.Count;
                Assert(!session.Execute(new Request { Version = 99, Operation = "space_start" }).Success && b.Writes.Count == writes, "protocol bypassed gate");
                var response = Protocol.Decode<Response>(Protocol.Encode(session.Execute(new Request { Operation = "space_status" })));
                Assert(response.LegionSpace.State.StartMode == 4 && !response.LegionSpace.State.Running, "IPC lost service state");
            }
        });
        Test("SCM records use the Windows x64 ABI without invoking service mutations", () => {
            var type = typeof(LegionSpaceBackend);
            Assert(Marshal.SizeOf(type.GetNestedType("ServiceConfig", BindingFlags.NonPublic)) == (IntPtr.Size == 8 ? 64 : 36), "QUERY_SERVICE_CONFIG layout");
            Assert(Marshal.SizeOf(type.GetNestedType("DelayedStart", BindingFlags.NonPublic)) == 4, "delayed bool is not Win32 BOOL");
            Assert(Marshal.SizeOf(type.GetNestedType("ServiceStatus", BindingFlags.NonPublic)) == 36, "SERVICE_STATUS_PROCESS layout");
            var validate = type.GetMethod("ValidateBinary", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (string command in new[] { "", "\"C:\\Windows\\System32\\LSDaemon.exe\"", "\"C:\\Program Files\\Lenovo\\LegionSpace-backup\\LSDaemon.exe\"", "C:\\Program Files\\Lenovo\\LegionSpace\\LSDaemon.exe", "\"unterminated" })
                MustFail(() => validate.Invoke(null, new object[] { command }));
        });
        Test("Service journal survives restart rejects corruption and never replaces first original", () => {
            string directory = Path.Combine(Path.GetTempPath(), "LegionTweaks-space-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "legion-space-original.json");
            try
            {
                var original = new LegionSpaceOriginal { State = new LegionSpaceState { StartMode = 3, Running = false, DelayedAutoStart = true } };
                var journal = new LegionSpaceJournal(directory); journal.Save(original);
                MustFail(() => journal.Save(new LegionSpaceOriginal { State = new LegionSpaceState { StartMode = 4 } }));
                Assert(new LegionSpaceJournal(directory).Load().State.SameAs(original.State), "restart lost original");
                File.WriteAllText(path, "{broken"); MustFail(() => journal.Load());
                Assert(File.ReadAllText(path) == "{broken", "corrupt original overwritten");
                journal.Clear(); Assert(journal.Load() == null, "clear did not remove original");
            }
            finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); Directory.Delete(directory); }
        });
    }
    private static string MustFail(Action action)
    {
        try { action(); } catch (Exception ex) { return ex.Message; }
        throw new Exception("Expected failure did not occur.");
    }
    private sealed class SpaceBackend : ILegionSpaceBackend
    {
        public LegionSpaceState State = new LegionSpaceState { StartMode = 2, Running = true };
        public bool FailRead, FailConfigureOnce, FailConfigureAlways, IgnoreConfigureOnce;
        public int Reads; public readonly List<string> Writes = new List<string>();
        public LegionSpaceState Read() { Reads++; if (FailRead) throw new InvalidOperationException("SCM unavailable"); return State.Copy(); }
        public void Configure(int mode, bool delayed)
        {
            Writes.Add("config:" + mode + ":" + delayed);
            if (IgnoreConfigureOnce) { IgnoreConfigureOnce = false; return; }
            State.StartMode = mode; State.DelayedAutoStart = delayed;
            if (FailConfigureAlways || FailConfigureOnce) { FailConfigureOnce = false; throw new InvalidOperationException("configuration failed"); }
        }
        public void SetRunning(bool running)
        {
            Writes.Add("running:" + running);
            if (running && State.StartMode == 4) throw new InvalidOperationException("disabled service cannot start");
            State.Running = running;
        }
    }
    private sealed class SpaceJournal : ILegionSpaceJournal
    {
        public LegionSpaceOriginal Original; public int Saves; public bool FailLoad, FailSave, FailClear;
        public LegionSpaceOriginal Load() { if (FailLoad) throw new InvalidDataException("damaged journal"); return Original; }
        public void Save(LegionSpaceOriginal original) { if (FailSave) throw new IOException("disk full"); Saves++; Original = new LegionSpaceOriginal { State = original.State.Copy() }; }
        public void Clear() { if (FailClear) throw new IOException("backup locked"); Original = null; }
    }
}
