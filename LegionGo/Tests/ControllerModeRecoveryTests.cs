// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private const string ModeSid = "S-1-5-21-100-200-300-1001";
    private const string ModeRoot = @"USB\VID_17EF&PID_61EB\12345678";
    private const string ModeTemporaryRoot = @"USB\VID_17EF&PID_61EC\87654321";
    private const string ModeLocation = "PCIROOT(0)#PCI(0100)#USBROOT(0)#USB(2)";

    private static ControllerModeLease ModeLease(string phase = "Armed") => new ControllerModeLease {
        Token = "0123456789abcdef0123456789abcdef", Sid = ModeSid, OriginalReceiver = ModeRoot,
        OriginalLocation = ModeLocation, OriginalMode = 1, OwnerPid = 42, OwnerStarted = 123, CreatedUtc = 1000000,
        ExpiresUtc = 1000000 + TimeSpan.FromMilliseconds(ControllerModeRecoveryRules.LeaseMilliseconds).Ticks,
        Phase = phase, WatchPid = phase == "Armed" ? 0 : 43, WatchStarted = phase == "Armed" ? 0 : 1000001,
        WorkerPid = phase == "Armed" ? 0 : 44, WorkerStarted = phase == "Armed" ? 0 : 1000002 };

    private static void ControllerModeRecoveryTests()
    {
        Test("Mode query accepts only the complete reviewed configuration response", () => {
            foreach (int value in new[] { 1, 2 }) {
                var frame = new byte[64]; frame[0] = 4; frame[2] = 3; frame[3] = 14; frame[4] = 3; frame[5] = (byte)value;
                int mode;
                Assert(ControllerModeProtocol.TryReadMode(frame, out mode) && mode == value, "valid mode response rejected");
                foreach (int index in new[] { 0, 1, 2, 3, 4, 5 }) {
                    var corrupt = (byte[])frame.Clone(); corrupt[index] = 255;
                    Assert(!ControllerModeProtocol.TryReadMode(corrupt, out mode) && mode == 0, "unknown response became a mode");
                }
                foreach (var bad in new[] { (byte[])null, new byte[0], frame.Take(6).ToArray(), frame.Concat(new byte[1]).ToArray() })
                    Assert(!ControllerModeProtocol.TryReadMode(bad, out mode) && mode == 0, "incomplete response supplied a default mode");
                frame[5] = 0;
                Assert(!ControllerModeProtocol.TryReadMode(frame, out mode) && mode == 0, "unknown state became XInput");
            }
        });
        Test("Mode recovery classifies receiver PIDs without guessing instance suffixes", () => {
            Assert(ControllerModeProtocol.IsOriginalReceiver(ModeRoot) && ControllerModeProtocol.IsOriginalReceiver(ModeRoot.ToLowerInvariant()), "original receiver rejected");
            Assert(ControllerModeProtocol.IsTemporaryReceiver(ModeTemporaryRoot) && !ControllerModeProtocol.IsOriginalReceiver(ModeTemporaryRoot), "temporary identity not recognized");
            foreach (string bad in new[] { null, "", ModeRoot + "\n", ModeRoot + @"\child", "prefix" + ModeRoot,
                ModeRoot.Replace("61EB", "6182"), ModeRoot.Replace("61EB", "61EC"), ModeRoot.Replace("61EB", "61EB&MI_00"),
                ModeRoot.Replace("USB", "HID"), ModeRoot.Replace("17EF", "045E"), @"USB\VID_17EF&PID_61EB\", ModeRoot + new string('x', 129) }) {
                Assert(!ControllerModeProtocol.IsOriginalReceiver(bad), "foreign or noncomposite identity accepted");
            }
            foreach(string bad in new[] { ModeRoot, ModeTemporaryRoot + @"\child", ModeTemporaryRoot + "\n", null })
                Assert(!ControllerModeProtocol.IsTemporaryReceiver(bad), "invalid temporary receiver accepted");
        });
        Test("Mode arming persists the actual original before any mutation without aliasing the caller", () => {
            var events = new List<string>(); var journal = new ModeJournal(events); var device = new ModeDevice(events);
            var intent = ModeLease(); intent.OriginalMode = 0;
            ControllerModeRecoveryRules.Arm(journal, device, intent, ModeSid, Supported());
            Assert(events.SequenceEqual(new[] { "load", "inspect", "create" }) && !device.Writes.Any(), "arming skipped durable ordering or wrote firmware");
            Assert(journal.Value.OriginalMode == 1 && intent.OriginalMode == 0, "original was guessed or the caller snapshot was changed");
            intent.OriginalReceiver = ModeRoot + "other";
            Assert(journal.Load().OriginalReceiver == ModeRoot, "journal retained mutable caller data");
        });
        Test("Mode arming cannot replace a pending journal or proceed after persistence failure", () => {
            var journal = new ModeJournal { Value = ModeLease() }; var device = new ModeDevice();
            MustFail(() => ControllerModeRecoveryRules.Arm(journal, device, ModeLease(), ModeSid, Supported()));
            Assert(device.Inspections == 0 && !device.Writes.Any(), "pending recovery touched hardware");
            journal.Value = null; journal.FailCreate = true;
            MustFail(() => ControllerModeRecoveryRules.Arm(journal, device, ModeLease(), ModeSid, Supported()));
            Assert(journal.Value == null && !device.Writes.Any(), "failed persistence permitted a mutation");
        });
        Test("Mode originals must have fresh coherent XInput identity and health", () => {
            foreach (Action<ControllerModeState> corrupt in new Action<ControllerModeState>[] {
                s => s.Mode = 0, s => s.Mode = 2, s => s.Present = false, s => s.Problem = 10,
                s => s.Receiver = ModeRoot + "other", s => s.Service = "xusb22", s => s.Receiver = ModeTemporaryRoot }) {
                var device = new ModeDevice(); corrupt(device.State); var journal = new ModeJournal();
                MustFail(() => ControllerModeRecoveryRules.Arm(journal, device, ModeLease(), ModeSid, Supported()));
                Assert(journal.Value == null && !device.Writes.Any(), "unknown original was persisted or changed");
            }
        });
        Test("Mode recovery schema rejects missing fields foreign owners and missing supervisor identities", () => {
            MustFail(() => Protocol.Decode<ControllerModeLease>("{\"Version\":1}"));
            var valid = ModeLease("Switching");
            ControllerModeRecoveryRules.Validate(Protocol.Decode<ControllerModeLease>(Protocol.Encode(valid)), ModeSid, valid.Token);
            foreach (Action<ControllerModeLease> corrupt in new Action<ControllerModeLease>[] {
                x => x.Version = 2, x => x.Token = "bad", x => x.Sid += "-other", x => x.OriginalMode = 0,
                x => x.OriginalMode = 2, x => x.OriginalReceiver = ModeTemporaryRoot, x => x.OriginalLocation = null, x => x.ExpiresUtc++,
                x => x.CreatedUtc = long.MaxValue, x => x.OwnerPid = 0, x => x.OwnerStarted = 0,
                x => x.WatchPid = 0, x => x.WorkerStarted = 0, x => x.WatchPid = x.OwnerPid,
                x => x.WorkerPid = x.WatchPid, x => x.Phase = "unknown", x => x.OwnerStarted = long.MaxValue }) {
                var lease = ModeLease("Switching"); corrupt(lease);
                MustFail(() => ControllerModeRecoveryRules.Validate(lease, ModeSid, valid.Token));
            }
            MustFail(() => ControllerModeRecoveryRules.Validate(null, ModeSid));
            MustFail(() => ControllerModeRecoveryRules.Validate(valid, ModeSid, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        });
        Test("Mode transitions require both model and CPU gating independently of matching USB IDs", () => {
            foreach (DeviceIdentity machine in new[] { null, new DeviceIdentity { Manufacturer = "LENOVO", Model = "83E1", Cpu = "other" },
                new DeviceIdentity { Manufacturer = "LENOVO", Model = "other", Cpu = "AMD Ryzen Z1 Extreme" } }) {
                var device = new ModeDevice(); var journal = new ModeJournal();
                MustFail(() => ControllerModeRecoveryRules.Arm(journal, device, ModeLease(), ModeSid, machine));
                journal.Value = ModeLease("Switching");
                MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, journal.Value.Token, machine, () => 1000003));
                journal.Value.Phase = "Recovering";
                MustFail(() => ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, machine));
                journal.Value.Phase = "Restored";
                MustFail(() => ControllerModeRecoveryRules.VerifyRestored(journal, device, ModeSid, journal.Value.Token, machine));
                Assert(device.Inspections == 0 && !device.Writes.Any(), "matching PID bypassed machine gating");
            }
        });
        Test("Temporary mode accepts a changed instance ID only at the saved USB location", () => {
            var journal = new ModeJournal { Value = ModeLease("Switching") }; var device = new ModeDevice();
            device.BeforeChange = () => Assert(journal.Value != null && journal.Value.Phase == "Switching" && journal.Value.OriginalMode == 1, "mutation preceded original intent");
            ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, journal.Value.Token, Supported(), () => 1000003);
            Assert(device.Writes.SequenceEqual(new[] { 2 }) && device.Inspections == 2 && journal.Value != null, "transition guessed success or retired recovery");
            Assert(device.State.Receiver == ModeTemporaryRoot && device.State.Mode == 2, "temporary receiver was not read back");
            Assert(device.State.Receiver.Split('\\').Last() != ModeRoot.Split('\\').Last() && device.State.Location == ModeLocation,
                "test did not exercise a re-enumeration with a different instance suffix");
        });
        Test("A long mode query cannot outlive its authorization or race a recovery phase", () => {
            foreach (bool expire in new[] { false, true }) {
                var journal = new ModeJournal { Value = ModeLease("Switching") }; var device = new ModeDevice(); long now = 1000003;
                device.AfterInspect = () => { if(expire) now = journal.Value.ExpiresUtc; else journal.Value.Phase = "Recovering"; };
                MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, journal.Value.Token, Supported(), () => now));
                Assert(!device.Writes.Any() && journal.Value != null, "expired or revoked operation wrote firmware");
            }
            var sameToken = new ModeJournal { Value = ModeLease("Switching") }; var changedDevice = new ModeDevice();
            changedDevice.AfterInspect = () => sameToken.Value.OriginalReceiver = ModeRoot + "other";
            MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(sameToken, changedDevice, ModeSid, sameToken.Value.Token, Supported(), () => 1000003));
            Assert(!changedDevice.Writes.Any(), "reused token authorized a different original");
        });
        Test("Mode switching rejects stale phases foreign tokens and clock rollback before writing", () => {
            foreach (string phase in new[] { "Armed", "Temporary", "Recovering", "Restored" }) {
                var journal = new ModeJournal { Value = ModeLease(phase) }; var device = new ModeDevice();
                MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, journal.Value.Token, Supported(), () => 1000003));
                Assert(!device.Writes.Any(), "unauthorized phase wrote firmware");
            }
            foreach (string token in new[] { null, "", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }) {
                var journal = new ModeJournal { Value = ModeLease("Switching") }; var device = new ModeDevice();
                MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, token, Supported(), () => 1000003));
                Assert(device.Inspections == 0 && !device.Writes.Any(), "foreign operation reached the device");
            }
            var j = new ModeJournal { Value = ModeLease("Switching") }; var d = new ModeDevice(); int calls = 0;
            MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(j, d, ModeSid, j.Value.Token, Supported(), () => calls++ == 0 ? 1000004 : 1000003));
            Assert(!d.Writes.Any(), "clock rollback extended the authorization");
        });
        Test("A mode change that disconnects then throws still has a recoverable original", () => {
            var journal = new ModeJournal { Value = ModeLease("Switching") }; var device = new ModeDevice { ThrowAfterChange = true };
            MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, journal.Value.Token, Supported(), () => 1000003));
            Assert(device.State.Mode == 2 && journal.Value.OriginalMode == 1, "partial change lost its original");
            journal.Value.Phase = "Recovering";
            ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, Supported());
            Assert(device.Writes.SequenceEqual(new[] { 2, 1 }) && device.State.Receiver == ModeRoot && journal.Value != null, "recovery was skipped or discarded its verification journal");
        });
        Test("Mode recovery remains idempotent after expiry and never assumes XInput from an invalid reading", () => {
            var journal = new ModeJournal { Value = ModeLease("Recovering") }; var device = new ModeDevice();
            Assert(journal.Value.ExpiresUtc < DateTime.UtcNow.Ticks, "test needs an expired original lease");
            ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, Supported());
            ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, Supported());
            Assert(!device.Writes.Any() && device.Inspections == 4, "idempotent recovery wrote firmware or guessed success");
            foreach (Action<ControllerModeState> corrupt in new Action<ControllerModeState>[] {
                s => s.Mode = 0, s => s.Receiver = ModeRoot + "foreign", s => s.Mode = 2, s => s.Present = false, s => s.Problem = 10 }) {
                var bad = new ModeDevice(); corrupt(bad.State);
                MustFail(() => ControllerModeRecoveryRules.Restore(journal, bad, ModeSid, journal.Value.Token, Supported()));
                Assert(!bad.Writes.Any() && journal.Value != null, "invalid response authorized a default restoration");
            }
        });
        Test("A failed mode readback retains intent and prevents final recovery verification", () => {
            var journal = new ModeJournal { Value = ModeLease("Recovering") }; var device = new ModeDevice { IgnoreChange = true };
            device.State.Mode = 2; device.State.Receiver = ModeTemporaryRoot;
            MustFail(() => ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, Supported()));
            Assert(device.Writes.SequenceEqual(new[] { 1 }) && journal.Value != null, "failed readback lost recovery intent");
            journal.Value.Phase = "Restored";
            MustFail(() => ControllerModeRecoveryRules.VerifyRestored(journal, device, ModeSid, journal.Value.Token, Supported()));
            Assert(journal.Value != null, "verification retired an unrecovered original");
            device.State.Mode = 1; device.State.Receiver = ModeRoot;
            ControllerModeRecoveryRules.VerifyRestored(journal, device, ModeSid, journal.Value.Token, Supported());
            Assert(journal.Value != null && device.Writes.Count == 1, "verification itself mutated hardware or storage");
        });
        Test("A transition that passes its deadline after mutation must not report readiness", () => {
            var journal = new ModeJournal { Value = ModeLease("Switching") }; var device = new ModeDevice(); long now = 1000003;
            device.BeforeChange = () => now = journal.Value.ExpiresUtc;
            MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(journal, device, ModeSid, journal.Value.Token, Supported(), () => now));
            Assert(device.State.Mode == 2 && journal.Value != null, "expired partial operation lost its recovery path");
            journal.Value.Phase = "Recovering";
            ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, Supported());
            Assert(device.State.Mode == 1, "expired operation blocked recovery");
        });
        Test("Mode identity requires a bounded USB location and never guesses a missing binding", () => {
            Assert(ControllerModeProtocol.IsLocationPath(ModeLocation) && ControllerModeProtocol.IsLocationPath(ModeLocation.ToLowerInvariant()), "valid USB location rejected");
            foreach(string bad in new[] { null, "", "USB(2)", "PCIROOT(0)", ModeLocation + "\n", ModeLocation + "\0", ModeLocation + new string('x', 1024), "other:" + ModeLocation }) {
                Assert(!ControllerModeProtocol.IsLocationPath(bad), "invalid location accepted");
                var lease = ModeLease(); lease.OriginalLocation = bad;
                MustFail(() => ControllerModeRecoveryRules.Validate(lease, ModeSid));
            }
        });
        Test("A matching PID or old suffix cannot authorize another port or replace the recovered original", () => {
            foreach(bool wrongPort in new[] { false, true }) {
                var journal = new ModeJournal { Value = ModeLease("Recovering") }; var device = new ModeDevice();
                if(wrongPort) {
                    device.State.Mode = 2; device.State.Receiver = ModeRoot.Replace("61EB", "61EC");
                    device.State.Location = ModeLocation.Replace("USB(2)", "USB(3)");
                } else device.State.Receiver = ModeRoot + "other";
                MustFail(() => ControllerModeRecoveryRules.Restore(journal, device, ModeSid, journal.Value.Token, Supported()));
                Assert(!device.Writes.Any() && journal.Value != null, "recovery changed a mismatched receiver");
            }
            var active = new ModeJournal { Value = ModeLease("Switching") }; var source = new ModeDevice();
            source.AfterInspect = () => active.Value.OriginalLocation = ModeLocation.Replace("USB(2)", "USB(3)");
            MustFail(() => ControllerModeRecoveryRules.SwitchToDirectInput(active, source, ModeSid, active.Value.Token, Supported(), () => 1000003));
            Assert(!source.Writes.Any(), "same token replaced the original port during the query");
        });
        Test("USB location selection is stable when Windows includes an alternative ACPI route", () => {
            string alternative = "ACPI(_SB_)#ACPI(PCI0)#ACPI(RHUB)#ACPI(PRT2)";
            Assert(ControllerModeProtocol.SelectLocation(new[] { ModeLocation, alternative }) == ModeLocation
                && ControllerModeProtocol.SelectLocation(new[] { alternative, ModeLocation }) == ModeLocation,
                "alternative description rejected or reordered the USB binding");
            foreach(var paths in new[] { (string[])null, new string[0], new[] { alternative }, new[] { ModeLocation, (string)null },
                Enumerable.Repeat(ModeLocation, 9).ToArray(), new[] { new string('x', 1025) } })
                MustFail(() => ControllerModeProtocol.SelectLocation(paths));
        });
    }

    private sealed class ModeJournal : IControllerModeJournal
    {
        internal ControllerModeLease Value;
        internal bool FailCreate;
        private readonly List<string> events;
        internal ModeJournal(List<string> events = null) { this.events = events; }
        public ControllerModeLease Load() { events?.Add("load"); return Value == null ? null : Clone(Value); }
        public void Create(ControllerModeLease lease) {
            events?.Add("create");
            if(FailCreate || Value != null) throw new System.IO.IOException("cannot persist original");
            Value = Clone(lease);
        }
        private static ControllerModeLease Clone(ControllerModeLease value) => Protocol.Decode<ControllerModeLease>(Protocol.Encode(value));
    }

    private sealed class ModeDevice : IControllerModeDevice
    {
        internal ControllerModeState State = new ControllerModeState { Receiver = ModeRoot, Location = ModeLocation, Service = "usbccgp", Present = true, Mode = 1 };
        internal readonly List<int> Writes = new List<int>();
        internal int Inspections;
        internal Action AfterInspect, BeforeChange;
        internal bool ThrowAfterChange, IgnoreChange;
        private readonly List<string> events;
        internal ModeDevice(List<string> events = null) { this.events = events; }
        public ControllerModeState Inspect(string original, string location) {
            events?.Add("inspect"); Inspections++;
            var state = new ControllerModeState { Receiver = State.Receiver, Location = State.Location, Service = State.Service, Mode = State.Mode, Present = State.Present, Problem = State.Problem };
            AfterInspect?.Invoke(); return state;
        }
        public void ChangeMode(string original, string location, int mode) {
            Assert(original == ModeRoot && location == ModeLocation && (mode == 1 || mode == 2), "mutation targeted a different receiver or unknown command");
            BeforeChange?.Invoke(); Writes.Add(mode);
            if(!IgnoreChange) { State.Mode = mode; State.Receiver = mode == 1 ? ModeRoot : ModeTemporaryRoot; }
            if(ThrowAfterChange) { ThrowAfterChange = false; throw new System.IO.IOException("disconnected after command"); }
        }
    }
}
