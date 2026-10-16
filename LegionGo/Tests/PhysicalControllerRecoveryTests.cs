// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private const string PhysicalRecoverySid = "S-1-5-21-100-200-300-1001";
    private const string PhysicalRecoveryDevice = @"USB\VID_17EF&PID_6182&MI_00\7&ABC&0&0000";
    private static PhysicalRecoveryLease RecoveryLease() => new PhysicalRecoveryLease {
        Token = "0123456789abcdef0123456789abcdef", Sid = PhysicalRecoverySid, Device = PhysicalRecoveryDevice,
        OwnerPid = 42, OwnerStarted = 123, CreatedUtc = 1000000,
        ExpiresUtc = 1000000 + TimeSpan.FromMilliseconds(PhysicalRecoveryRules.LeaseMilliseconds).Ticks,
        OriginalStatus = PhysicalRecoveryRules.Started, Phase = "Armed" };
    private static void PhysicalControllerRecoveryTests()
    {
        Test("PNP recovery accepts only the exact original-Go XUSB MI_00 endpoint", () => {
            foreach (string id in new[] { PhysicalRecoveryDevice, PhysicalRecoveryDevice.ToLowerInvariant(),
                PhysicalRecoveryDevice.Replace("6182", "61EB") }) Assert(PhysicalRecoveryRules.IsEndpoint(id), "supported endpoint rejected");
            foreach (string id in new[] { null, "", @"USB\VID_17EF&PID_6182\ABC", @"HID\VID_17EF&PID_6182&MI_00\ABC",
                PhysicalRecoveryDevice.Replace("MI_00", "MI_01"), PhysicalRecoveryDevice.Replace("6182", "6183"),
                PhysicalRecoveryDevice.Replace("6182", "61820"), PhysicalRecoveryDevice + @"\child", PhysicalRecoveryDevice + "\n",
                PhysicalRecoveryDevice.Replace("MI_00", "MI_000"), "prefix" + PhysicalRecoveryDevice })
                Assert(!PhysicalRecoveryRules.IsEndpoint(id), "non-XUSB endpoint accepted");
            // Numeric ABI from Microsoft's cfgmgr32.h, not from our implementation:
            // UI_NOT_OK=4; ABSOLUTE=1, HARDWARE=2, PERSIST=8 must all remain absent.
            Assert(PhysicalRecoveryRules.NonPersistentDisableFlags == 0x4 &&
                (PhysicalRecoveryRules.NonPersistentDisableFlags & (0x1 | 0x2 | 0x8)) == 0, "wrong Configuration Manager disable flags");
        });
        Test("Consumed recovery journals report completed rollback rather than corruption", () => {
            var lease = RecoveryLease();
            var outcome = PhysicalRecoveryRules.RecordOutcome(null, lease, PhysicalRecoverySid, 123, "disable", "native veto", 1, true, true);
            try { PhysicalRecoveryRules.RequireCurrent(null, PhysicalRecoverySid, lease.Token, outcome); throw new Exception("missing journal accepted"); }
            catch (InvalidOperationException ex) { Assert(ex.Message.Contains("recuperado") && ex.Message.Contains("native veto") && !ex.Message.Contains("inválido"), "rollback lost the original reason"); }
        });
        Test("Busy reader reconnection is explicit and remains restartable and nonpersistent", () => {
            var legacy = Protocol.Decode<PhysicalRecoveryLease>("{\"Version\":1}");
            Assert(!legacy.AllowBusyReaders && !RecoveryLease().AllowBusyReaders, "old or ordinary operations forced removal");
            var lease = RecoveryLease(); lease.AllowBusyReaders = true;
            Assert(Protocol.Decode<PhysicalRecoveryLease>(Protocol.Encode(lease)).AllowBusyReaders, "explicit permission not journaled");
            Assert(PhysicalRecoveryRules.BusyReaderDisableFlags == 0x5
                && (PhysicalRecoveryRules.BusyReaderDisableFlags & (0x2 | 0x8)) == 0, "nonrestartable or persistent flags selected");
        });
        Test("Missing journals without a matching result do not claim corrupted backup data", () => {
            var lease = RecoveryLease();
            foreach (var outcome in new[] { (PhysicalRecoveryOutcome)null, new PhysicalRecoveryOutcome { Version = 1, Token = "other", Sid = PhysicalRecoverySid, CapturedUtc = 1, Recovered = true, Failure = "foreign" } })
                Assert(!PhysicalRecoveryRules.InterruptedMessage(outcome, PhysicalRecoverySid, lease.Token).Contains("foreign")
                    && !PhysicalRecoveryRules.InterruptedMessage(outcome, PhysicalRecoverySid, lease.Token).Contains("inválido"), "unrelated result was trusted");
        });
        Test("A malformed existing recovery journal still fails closed", () => {
            var lease = RecoveryLease(); lease.Device = @"USB\VID_17EF&PID_6182\parent";
            bool invalid = false;
            try { PhysicalRecoveryRules.RequireCurrent(lease, PhysicalRecoverySid, lease.Token, null); }
            catch (System.IO.InvalidDataException) { invalid = true; }
            Assert(invalid, "actual invalid journal was mistaken for completed rollback");
        });
        Test("Recovery diagnostics retain the first native failure across later cleanup errors", () => {
            var lease = RecoveryLease();
            var original = PhysicalRecoveryRules.RecordOutcome(null, lease, PhysicalRecoverySid, 123, "disable", "first native error", 7);
            var final = PhysicalRecoveryRules.RecordOutcome(original, lease, PhysicalRecoverySid, 456, "recovery", "later error", 9, true, true);
            Assert(final.Failure == "first native error" && final.FailureStage == "disable" && final.WorkerExitCode == 7
                && final.Recovered && final.Completed && !original.Recovered, "cleanup replaced the causal error or mutated the snapshot");
        });
        Test("A new recovery operation cannot inherit a previous failure or recovery success", () => {
            var lease = RecoveryLease();
            var old = PhysicalRecoveryRules.RecordOutcome(null, lease, PhysicalRecoverySid, 1, "disable", "old failure", 1, true, true);
            lease.Token = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            var fresh = PhysicalRecoveryRules.RecordOutcome(old, lease, PhysicalRecoverySid, 2);
            Assert(fresh.Token == lease.Token && fresh.Failure == null && !fresh.Recovered && !fresh.Completed, "previous operation contaminated the new one");
        });
        Test("Native diagnostic text is bounded and preserves its operation identity over serialization", () => {
            var lease = RecoveryLease();
            var result = PhysicalRecoveryRules.RecordOutcome(null, lease, PhysicalRecoverySid, 42, new string('s', 100), new string('e', 4000), 5);
            var copy = Protocol.Decode<PhysicalRecoveryOutcome>(Protocol.Encode(result));
            Assert(copy.Failure.Length == 2500 && copy.FailureStage.Length == 64 && copy.Token == lease.Token
                && copy.Sid == PhysicalRecoverySid && copy.WorkerExitCode == 5, "result is unbounded or lost identity");
        });
        Test("PNP recovery journals the actual healthy original before authorizing any disable", () => {
            var events = new List<string>(); var journal = new RecoveryJournal(events); var device = new RecoveryDevice(events);
            var lease = RecoveryLease(); device.State.Status |= 0x200;
            PhysicalRecoveryRules.Arm(journal, device, lease, PhysicalRecoverySid);
            Assert(events.SequenceEqual(new[] { "inspect", "save" }) && device.Disables == 0, "arming mutated hardware or missed durable ordering");
            Assert(journal.Value.OriginalStatus == device.State.Status && journal.Value.OriginalProblem == 0 && journal.Value.Phase == "Armed", "original status was guessed");
            journal.Value.Phase = "Disabling";
            device.BeforeDisable = () => Assert(journal.Value != null && journal.Value.OriginalStatus == device.State.Status, "mutation preceded original journal");
            PhysicalRecoveryRules.Disable(journal, device, PhysicalRecoverySid, lease.Token, lease.CreatedUtc);
            Assert(device.Disables == 1 && journal.Value != null, "disable discarded recovery intent");
        });
        Test("PNP recovery never mutates when original persistence fails or a lease already exists", () => {
            var journal = new RecoveryJournal { FailCreate = true }; var device = new RecoveryDevice();
            MustFail(() => PhysicalRecoveryRules.Arm(journal, device, RecoveryLease(), PhysicalRecoverySid));
            Assert(device.Disables == 0 && device.Enables == 0 && journal.Value == null, "persistence failure permitted a mutation");
            journal.Value = RecoveryLease(); device.Inspections = 0;
            MustFail(() => PhysicalRecoveryRules.Arm(journal, device, RecoveryLease(), PhysicalRecoverySid));
            Assert(device.Inspections == 0 && device.Disables == 0 && journal.Value.Token == RecoveryLease().Token, "pending original was overwritten");
        });
        Test("PNP recovery refuses an originally disabled stopped or unhealthy device", () => {
            foreach (var state in new[] {
                new PhysicalDeviceState { Status = 0, Problem = 0 },
                new PhysicalDeviceState { Status = PhysicalRecoveryRules.HasProblem, Problem = 22 },
                new PhysicalDeviceState { Status = PhysicalRecoveryRules.Started | PhysicalRecoveryRules.HasProblem, Problem = 10 },
                new PhysicalDeviceState { Status = PhysicalRecoveryRules.Started, Problem = 22 } })
            {
                state.Device = PhysicalRecoveryDevice; state.Service = "xusb22";
                var device = new RecoveryDevice { State = state }; var journal = new RecoveryJournal();
                MustFail(() => PhysicalRecoveryRules.Arm(journal, device, RecoveryLease(), PhysicalRecoverySid));
                Assert(journal.Value == null && device.Disables == 0 && device.Enables == 0, "unhealthy original was changed");
            }
        });
        Test("PNP recovery checks the device identity and xusb22 service before saving or mutating", () => {
            foreach (bool wrongService in new[] { false, true })
            {
                var device = new RecoveryDevice(); var journal = new RecoveryJournal();
                if (wrongService) device.State.Service = "usbccgp"; else device.State.Device += "-different";
                MustFail(() => PhysicalRecoveryRules.Arm(journal, device, RecoveryLease(), PhysicalRecoverySid));
                Assert(journal.Value == null && device.Disables == 0, "wrong function reached the mutation boundary");
            }
        });
        Test("PNP recovery rejects malformed foreign or guessed recovery journals", () => {
            Action<PhysicalRecoveryLease>[] corruptions = {
                x => x.Version = 2, x => x.Sid = "S-1-5-21-999", x => x.Token = "not-a-token", x => x.OwnerPid = 0,
                x => x.OwnerStarted = 0, x => x.ExpiresUtc++, x => x.OriginalStatus = 0,
                x => x.OriginalStatus |= PhysicalRecoveryRules.HasProblem, x => x.OriginalProblem = 22,
                x => x.Phase = "unknown", x => x.WatchPid = 1, x => x.WorkerStarted = 1,
                x => x.Device = @"USB\VID_17EF&PID_6182\parent" };
            foreach (var corrupt in corruptions)
            {
                var lease = RecoveryLease(); corrupt(lease);
                MustFail(() => PhysicalRecoveryRules.Validate(lease, PhysicalRecoverySid));
            }
            MustFail(() => PhysicalRecoveryRules.Validate(null, PhysicalRecoverySid));
            MustFail(() => PhysicalRecoveryRules.Validate(RecoveryLease(), PhysicalRecoverySid, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        });
        Test("PNP disable requires a current durable Disabling phase and the same operation token", () => {
            foreach (long now in new[] { RecoveryLease().CreatedUtc - 1, RecoveryLease().ExpiresUtc, RecoveryLease().ExpiresUtc + 1 })
            {
                var lease = RecoveryLease(); lease.Phase = "Disabling"; var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice();
                MustFail(() => PhysicalRecoveryRules.Disable(journal, device, PhysicalRecoverySid, lease.Token, now));
                Assert(device.Disables == 0 && device.Inspections == 0, "expired or backward-clock authorization reached the device");
            }
            foreach (string phase in new[] { "Armed", "Enabling", "Enabled", "Recovering", "Disabled" })
            {
                var lease = RecoveryLease(); lease.Phase = phase; var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice();
                MustFail(() => PhysicalRecoveryRules.Disable(journal, device, PhysicalRecoverySid, lease.Token, lease.CreatedUtc));
                Assert(device.Disables == 0, "another phase allowed disable");
            }
        });
        Test("PNP disable rechecks the journal after inspection and requires actual disabled readback", () => {
            var lease = RecoveryLease(); lease.Phase = "Disabling";
            var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice();
            device.OnInspect = () => journal.Value.Phase = "Recovering";
            MustFail(() => PhysicalRecoveryRules.Disable(journal, device, PhysicalRecoverySid, lease.Token, lease.CreatedUtc));
            Assert(device.Disables == 0, "stale authorization survived recovery starting");
            device.OnInspect = null; journal.Value.Phase = "Disabling"; device.IgnoreDisable = true;
            MustFail(() => PhysicalRecoveryRules.Disable(journal, device, PhysicalRecoverySid, lease.Token, lease.CreatedUtc));
            Assert(device.Disables == 1 && journal.Value != null, "ignored disable was reported successful or lost backup");
        });
        Test("PNP failed disable keeps the journal even after partial native mutation", () => {
            var lease = RecoveryLease(); lease.Phase = "Disabling";
            var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice { FailAfterDisable = true };
            MustFail(() => PhysicalRecoveryRules.Disable(journal, device, PhysicalRecoverySid, lease.Token, lease.CreatedUtc));
            Assert(device.Disables == 1 && device.State.Problem == 22 && journal.Value != null, "partial disable lost recovery intent");
            journal.Value.Phase = "Recovering";
            PhysicalRecoveryRules.Enable(journal, device, PhysicalRecoverySid, lease.Token);
            Assert(device.Enables == 1 && PhysicalRecoveryRules.IsStarted(device.State) && journal.Value != null, "partial mutation could not be recovered");
        });
        Test("PNP restore is idempotent and retains the journal until final verification", () => {
            var lease = RecoveryLease(); lease.Phase = "Enabling";
            var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice();
            device.State.Status = PhysicalRecoveryRules.HasProblem; device.State.Problem = 22;
            PhysicalRecoveryRules.Enable(journal, device, PhysicalRecoverySid, lease.Token);
            PhysicalRecoveryRules.Enable(journal, device, PhysicalRecoverySid, lease.Token);
            Assert(device.Enables == 1 && device.Disables == 0 && journal.Value != null, "restore changed a healthy original or discarded its journal");
            journal.Value.Phase = "Enabled"; PhysicalRecoveryRules.VerifyEnabled(journal, device, PhysicalRecoverySid, lease.Token);
            Assert(journal.Value != null, "readback alone discarded recovery ownership");
        });
        Test("PNP restore failures and unplugged functions keep the exact original for retry", () => {
            var lease = RecoveryLease(); lease.Phase = "Recovering";
            var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice { IgnoreEnable = true };
            device.State.Status = PhysicalRecoveryRules.HasProblem; device.State.Problem = 22;
            MustFail(() => PhysicalRecoveryRules.Enable(journal, device, PhysicalRecoverySid, lease.Token));
            Assert(device.Enables == 1 && journal.Value != null, "ignored enable lost backup");
            device.FailInspect = true;
            MustFail(() => PhysicalRecoveryRules.Enable(journal, device, PhysicalRecoverySid, lease.Token));
            Assert(device.Enables == 1 && journal.Value.Device == PhysicalRecoveryDevice, "missing device triggered a blind enable or different recovery");
        });
        Test("PNP completion requires the same healthy MI_00 instance and an Enabled phase", () => {
            var lease = RecoveryLease(); var journal = new RecoveryJournal { Value = lease }; var device = new RecoveryDevice();
            MustFail(() => PhysicalRecoveryRules.VerifyEnabled(journal, device, PhysicalRecoverySid, lease.Token));
            journal.Value.Phase = "Enabled";
            device.State.Status |= PhysicalRecoveryRules.HasProblem; device.State.Problem = 10;
            MustFail(() => PhysicalRecoveryRules.VerifyEnabled(journal, device, PhysicalRecoverySid, lease.Token));
            device.State.Status = PhysicalRecoveryRules.Started; device.State.Problem = 0; device.State.Device += "-other";
            MustFail(() => PhysicalRecoveryRules.VerifyEnabled(journal, device, PhysicalRecoverySid, lease.Token));
            Assert(journal.Value != null && device.Disables == 0 && device.Enables == 0, "completion accepted a different or unhealthy device");
        });
        Test("PNP recovery journal serializes the SID owner start deadline and original state", () => {
            var lease = RecoveryLease(); lease.WatchPid = 3; lease.WatchStarted = 9; lease.WorkerPid = 4; lease.WorkerStarted = 10;
            var copy = Protocol.Decode<PhysicalRecoveryLease>(Protocol.Encode(lease));
            PhysicalRecoveryRules.Validate(copy, PhysicalRecoverySid, lease.Token);
            Assert(copy.OwnerPid == 42 && copy.OwnerStarted == 123 && copy.ExpiresUtc == lease.ExpiresUtc &&
                copy.WatchStarted == 9 && copy.WorkerStarted == 10 && copy.OriginalStatus == PhysicalRecoveryRules.Started &&
                copy.Sid == PhysicalRecoverySid && copy.Device == PhysicalRecoveryDevice, "recovery ownership or original state lost in storage");
        });
    }
    private sealed class RecoveryJournal : IPhysicalRecoveryJournal
    {
        internal PhysicalRecoveryLease Value; internal bool FailCreate;
        private readonly List<string> events;
        internal RecoveryJournal(List<string> events = null) { this.events = events; }
        public PhysicalRecoveryLease Load() => Value == null ? null : Protocol.Decode<PhysicalRecoveryLease>(Protocol.Encode(Value));
        public void Create(PhysicalRecoveryLease lease)
        {
            events?.Add("save"); if (FailCreate) throw new InvalidOperationException("disk full");
            if (Value != null) throw new InvalidOperationException("exists");
            Value = Protocol.Decode<PhysicalRecoveryLease>(Protocol.Encode(lease));
        }
    }
    private sealed class RecoveryDevice : IPhysicalRecoveryDevice
    {
        internal PhysicalDeviceState State = new PhysicalDeviceState { Device = PhysicalRecoveryDevice, Service = "xusb22", Status = PhysicalRecoveryRules.Started };
        internal int Disables, Enables, Inspections; internal bool IgnoreDisable, IgnoreEnable, FailAfterDisable, FailInspect;
        internal Action BeforeDisable, OnInspect;
        private readonly List<string> events;
        internal RecoveryDevice(List<string> events = null) { this.events = events; }
        public PhysicalDeviceState Inspect(string instance)
        { events?.Add("inspect"); Inspections++; OnInspect?.Invoke(); if (FailInspect) throw new InvalidOperationException("unplugged"); return State; }
        public void Disable(string instance)
        {
            BeforeDisable?.Invoke(); events?.Add("disable"); Disables++;
            if (!IgnoreDisable) { State.Status = PhysicalRecoveryRules.HasProblem; State.Problem = 22; }
            if (FailAfterDisable) throw new InvalidOperationException("driver failed after mutation");
        }
        public void Enable(string instance)
        { events?.Add("enable"); Enables++; if (!IgnoreEnable) { State.Status = PhysicalRecoveryRules.Started; State.Problem = 0; } }
    }
}
