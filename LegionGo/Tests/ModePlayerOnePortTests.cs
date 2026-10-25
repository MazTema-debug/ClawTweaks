// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private const string ModeP1Root = @"USB\VID_17EF&PID_61EB\12345678";
    private const string ModeP1Temporary = @"USB\VID_17EF&PID_61EC\87654321";
    private const string ModeP1Endpoint = @"USB\VID_17EF&PID_61EB&MI_00\sample";
    private const string ModeP1Location = "PCIROOT(0)#PCI(0100)#USBROOT(0)#USB(2)";

    private static void ModePlayerOnePortTests()
    {
        Test("Mode preparation protects the absent physical endpoint only after final targets hold their slots", () => {
            foreach (int count in new[] { 1, 2 }) {
                ModeP1Fixture f = null; int protections = 0;
                f = new ModeP1Fixture(targets: count, protect: () => {
                    protections++;
                    Assert(f.Mode.Mode == 2 && f.Connected && f.Journal, "physical source was exposed or targets were not connected");
                });
                AutomaticPlayerOnePreparation.Run(f.Port, null, count);
                Assert(protections == 1 && f.Calls.SequenceEqual(new[] { "enter", "connect", "protect", "restore", "complete" }),
                    "protection did not precede restoration of the original source");
            }
        });
        Test("A partial protection failure still restores original mode and cannot repeat its setter", () => {
            int protections = 0;
            var f = new ModeP1Fixture(protect: () => { protections++; throw new InvalidOperationException("protección parcial"); });
            Assert(MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null)).Contains("protección parcial"), "original failure was lost");
            Assert(protections == 1 && f.Restores == 1 && f.Mode.Mode == 1 && f.Journal && f.Completions == 0,
                "partial isolation skipped recovery or retired an incomplete operation");
            MustFail(() => f.Port.ProtectPhysicalReturn());
            Assert(protections == 1, "partial protection was repeated");
        });
        Test("Physical return protection refuses changed target slots identity cancellation and expired budgets", () => {
            foreach (int fault in new[] { 0, 1, 2, 3 }) {
                int protections = 0; var f = new ModeP1Fixture(protect: () => protections++);
                f.Port.DisablePhysical(); f.Port.ConnectVirtual();
                if (fault == 0) f.VirtualSlotsOverride = new int?[] { 1 };
                if (fault == 1) f.Mode.Location = "PCIROOT(0)#PCI(0100)#USBROOT(0)#USB(3)";
                if (fault == 2) f.CanceledValue = true;
                if (fault == 3) f.Clock = 30000;
                MustFail(() => f.Port.ProtectPhysicalReturn());
                Assert(protections == 0, "unsafe protection reached its setter");
                f.Port.EnablePhysical();
                Assert(f.Mode.Mode == 1, "safety failure prevented independent physical recovery");
            }
        });
        Test("Mode player one composes the coordinator with a bound temporary mode and verified retirement", () => {
            var f = new ModeP1Fixture();
            AutomaticPlayerOnePreparation.Run(f.Port, null);
            Assert(f.Calls.SequenceEqual(new[] { "enter", "connect", "restore", "complete" }), "mode coordination order changed");
            Assert(f.Enters == 1 && f.Connects == 1 && f.Restores == 1 && f.Completions == 1 && !f.Journal,
                "transition or retirement was unbalanced");
            Assert(f.BoundRoot == ModeP1Root && f.BoundLocation == ModeP1Location && f.Mode.Mode == 1,
                "the original binding or final mode was not preserved");
            f.Port.EnablePhysical();
            Assert(f.Restores == 1, "retired recovery was requested again");
        });
        Test("Mode player one skips every mode operation when the first slot is already free", () => {
            var f = new ModeP1Fixture { PhysicalSlot = 1 };
            AutomaticPlayerOnePreparation.Run(f.Port, null);
            Assert(f.Connects == 1 && f.Enters == 0 && f.ModeReads == 0 && f.Restores == 0 && f.Completions == 0,
                "an already free first slot caused a mode query or mutation");
        });
        Test("Mode player one rejects unknown unhealthy foreign and invalid original mode observations before arming", () => {
            var invalid = new List<ControllerModeState> { null };
            foreach (Action<ControllerModeState> change in new Action<ControllerModeState>[] {
                s => s.Mode = 0, s => s.Mode = 2, s => s.Receiver = @"USB\VID_17EF&PID_61EB\foreign",
                s => s.Receiver = ModeP1Temporary, s => s.Location = null, s => s.Location = "unknown",
                s => s.Service = "HidUsb", s => s.Present = false, s => s.Problem = 1 })
            { var s = ModeP1State(1); change(s); invalid.Add(s); }
            foreach (var state in invalid)
            {
                var f = new ModeP1Fixture { Mode = state };
                MustFail(() => f.Port.DisablePhysical());
                f.Port.EnablePhysical();
                Assert(f.Enters == 0 && f.Connects == 0 && f.Restores == 0 && !f.Journal,
                    "an invalid original mode reached arming or unnecessary recovery");
            }
        });
        Test("Mode player one rejects ambiguous missing foreign and unsupported physical sources", () => {
            foreach (string endpoint in new[] { null, "", @"USB\VID_17EF&PID_61EB\parent",
                @"USB\VID_045E&PID_028E\virtual", @"USB\VID_17EF&PID_6182&MI_00\older" })
            {
                var f = new ModeP1Fixture { Endpoint = endpoint };
                MustFail(() => f.Port.DisablePhysical());
                Assert(f.Enters == 0 && f.ModeReads == 0, "a foreign source reached mode inspection or arming");
            }
            foreach (var state in new[] { AutomaticPreparationState(2, 0, 0), AutomaticPreparationState(1, 1, 0),
                AutomaticPreparationState(1, 0, 0, 1), AutomaticPreparationState(0, 0, 0), AutomaticPreparationState(1, 0, 1),
                new PlayerOneEnvironment { PhysicalDevices = 1, OtherDevices = 0, ConnectedSlots = new bool[3] } })
            {
                var f = new ModeP1Fixture { OverrideEnvironment = state };
                MustFail(() => f.Port.DisablePhysical());
                Assert(f.Enters == 0 && f.ModeReads == 0, "an ambiguous environment reached mode inspection or arming");
            }
        });
        Test("Mode player one rereads physical identity and environment after the original mode query", () => {
            foreach (bool changeIdentity in new[] { true, false })
            {
                var f = new ModeP1Fixture();
                f.OnModeInspect = () => {
                    if (changeIdentity) f.Endpoint = @"USB\VID_17EF&PID_61EB&MI_00\replacement";
                    else f.OverrideEnvironment = AutomaticPreparationState(1, 1, 0);
                };
                MustFail(() => f.Port.DisablePhysical());
                Assert(f.Enters == 0 && !f.Journal, "a stale identity query authorized mode switching");
            }
        });
        Test("Mode player one recovers a setter that changed the mode and then threw", () => {
            var f = new ModeP1Fixture { OnEnter = () => { throw new InvalidOperationException("setter parcial"); } };
            string error = MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null));
            Assert(error.Contains("setter parcial") && f.Enters == 1 && f.Connects == 0 && f.Restores == 1
                && f.Mode.Mode == 1 && f.Journal && f.Completions == 0,
                "partial setter failure lost recovery or retired an incomplete operation");
        });
        Test("Mode player one preserves the setter failure and recovery failure without retiring the journal", () => {
            var f = new ModeP1Fixture {
                OnEnter = () => { throw new InvalidOperationException("setter parcial"); },
                OnRestore = () => { throw new InvalidOperationException("recuperación rechazada"); }
            };
            AggregateException errors = null;
            try { AutomaticPlayerOnePreparation.Run(f.Port, null); }
            catch (Exception error) { errors = error as AggregateException; }
            Assert(errors != null && errors.InnerExceptions.Count == 2 && errors.InnerExceptions[0].Message.Contains("setter parcial")
                && errors.InnerExceptions[1].Message.Contains("recuperación rechazada") && f.Journal
                && f.Connects == 0 && f.Restores == 1 && f.Completions == 0,
                "the initial failure was hidden or incomplete recovery was retired");
        });
        Test("Mode player one restores after cancellation or expiration inside the setter", () => {
            foreach (bool cancel in new[] { true, false })
            {
                var f = new ModeP1Fixture();
                f.OnEnter = () => { if (cancel) f.CanceledValue = true; else f.Clock = 30000; };
                string error = MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null));
                Assert(error.Contains(cancel ? "cancelada" : "30 segundos") && f.Connects == 0 && f.Restores == 1
                    && f.Mode.Mode == 1 && f.Journal && f.Completions == 0,
                    "the canceled or expired preparation prevented recovery");
            }
        });
        Test("Mode player one requires a fresh healthy DirectInput observation at the bound location before connecting", () => {
            foreach (Action<ControllerModeState> change in new Action<ControllerModeState>[] {
                s => s.Mode = 0, s => s.Mode = 1, s => s.Receiver = ModeP1Root,
                s => s.Receiver = @"USB\VID_045E&PID_028E\foreign", s => s.Location = ModeP1Location.Replace("USB(2)", "USB(3)"),
                s => s.Location = null, s => s.Service = "HidUsb", s => s.Present = false, s => s.Problem = 4 })
            {
                var f = new ModeP1Fixture(); f.Port.DisablePhysical(); change(f.Mode);
                MustFail(() => f.Port.ConnectVirtual());
                Assert(f.Connects == 0 && f.Journal, "an unconfirmed temporary mode connected the virtual");
                f.Port.EnablePhysical();
                Assert(f.Restores == 1, "temporary mode rejection prevented recovery");
            }
            var absent = new ModeP1Fixture(); absent.Port.DisablePhysical(); absent.Mode = null;
            MustFail(() => absent.Port.ConnectVirtual());
            Assert(absent.Connects == 0, "absent mode reply connected the virtual");
        });
        Test("Mode player one rechecks the journal and budget after the temporary query", () => {
            foreach (int failure in new[] { 0, 1, 2 })
            {
                var f = new ModeP1Fixture(); f.Port.DisablePhysical();
                f.OnModeInspect = () => {
                    if (failure == 0) f.Journal = false;
                    else if (failure == 1) f.CanceledValue = true;
                    else f.Clock = 30000;
                };
                MustFail(() => f.Port.ConnectVirtual());
                Assert(f.Connects == 0, "a query outliving its authorization connected a target");
                f.Port.EnablePhysical();
                Assert(f.Restores == 1 && f.Mode.Mode == 1, "query cancellation blocked physical recovery");
            }
        });
        Test("Mode player one rejects a foreign controller or returning source during the final connect preflight", () => {
            foreach (var state in new[] { AutomaticPreparationState(1, 0), AutomaticPreparationState(0, 1, 1),
                AutomaticPreparationState(0, 0, 0), AutomaticPreparationState(0, 0, 2) })
            {
                var f = new ModeP1Fixture(); f.Port.DisablePhysical(); f.OverrideEnvironment = state;
                MustFail(() => f.Port.ConnectVirtual());
                Assert(f.Connects == 0, "a changed environment passed the final connect preflight");
            }
        });
        Test("Mode player one makes only one connection attempt even after a partial target failure", () => {
            var f = new ModeP1Fixture(); f.Port.DisablePhysical();
            f.OnConnect = () => { throw new InvalidOperationException("conexión parcial"); };
            MustFail(() => f.Port.ConnectVirtual());
            MustFail(() => f.Port.ConnectVirtual());
            Assert(f.Connects == 1, "the adapter retried a potentially connected virtual target");
        });
        Test("Mode player one observes cancellation and expiration after target connection and still restores", () => {
            foreach (bool cancel in new[] { true, false })
            {
                var f = new ModeP1Fixture();
                f.OnConnect = () => { if (cancel) f.CanceledValue = true; else f.Clock = 30000; };
                MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null));
                Assert(f.Connects == 1 && f.Restores == 1 && f.Journal && f.Mode.Mode == 1,
                    "a late target error skipped physical recovery");
            }
        });
        Test("Mode player one never completes a journal without fresh original mode and physical identity", () => {
            foreach (int failure in new[] { 0, 1, 2, 3 })
            {
                var f = new ModeP1Fixture(); f.Port.DisablePhysical(); f.Port.EnablePhysical();
                if (failure == 0) f.Mode.Mode = 2;
                if (failure == 1) f.Mode.Receiver = @"USB\VID_17EF&PID_61EB\replacement";
                if (failure == 2) f.Mode.Location = ModeP1Location.Replace("USB(2)", "USB(3)");
                if (failure == 3) f.Endpoint = null;
                MustFail(() => f.Port.CompleteRecovery());
                Assert(f.Completions == 0 && f.Journal, "unverified original recovery retired its journal");
                f.Port.EnablePhysical();
                Assert(f.Restores == 2, "failed completion cleared pending recovery");
            }
        });
        Test("Mode player one retains pending recovery when final retirement throws", () => {
            var f = new ModeP1Fixture { OnComplete = () => { throw new InvalidOperationException("retiro rechazado"); } };
            string error = MustFail(() => AutomaticPlayerOnePreparation.Run(f.Port, null));
            Assert(error.Contains("retiro rechazado") && f.Completions == 1 && f.Journal && f.Restores == 2,
                "failed journal retirement was announced as ready or cleared recovery");
        });
        Test("Mode player one does not gate restore on journal activity cancellation or its original deadline", () => {
            var f = new ModeP1Fixture(); f.Port.DisablePhysical();
            f.CanceledValue = true; f.Clock = 40000; f.CheckActiveFailure = true;
            f.Port.EnablePhysical();
            Assert(f.Restores == 1 && f.Mode.Mode == 1, "recovery depended on expired operation authorization");
        });
        Test("Mode player one enforces one shared monotonic 30 second budget across delegated operations", () => {
            foreach (int operation in new[] { 0, 1, 2 })
            {
                var f = new ModeP1Fixture();
                if (operation == 0) f.OnEnvironment = () => f.Clock = 30000;
                if (operation == 1) f.OnSlot = () => f.Clock = 30000;
                if (operation == 2) f.OnWait = () => f.Clock = 30000;
                string error = MustFail(() => { if (operation == 0) f.Port.Inspect(); else if (operation == 1) f.Port.ReadVirtualSlot(); else f.Port.Wait(100); });
                Assert(error.Contains("30 segundos") && f.Enters == 0 && f.Connects == 0, "delegated time escaped the mode budget");
            }
            var backward = new ModeP1Fixture(); backward.Clock = 100; backward.Port.Inspect(); backward.Clock = 99;
            Assert(MustFail(() => backward.Port.Inspect()).Contains("retrocedió"), "backward clock was accepted");
        });
        Test("Mode player one rejects cancellation during supervisor checks before delegated I/O", () => {
            var f = new ModeP1Fixture(); f.Port.DisablePhysical();
            f.OnActive = () => f.CanceledValue = true;
            int reads = f.EnvironmentReads;
            MustFail(() => f.Port.Inspect());
            Assert(f.EnvironmentReads == reads, "a canceled supervisor check reached the device inspection");
            f.Port.EnablePhysical();
            Assert(f.Restores == 1, "supervisor-check cancellation blocked recovery");
        });
        Test("Mode player one rejects an invalid initial clock and cancellation before inspection", () => {
            MustFail(() => new ModeP1Fixture(-1));
            MustFail(() => new ModeP1Fixture(0, true));
            var f = new ModeP1Fixture(80000);
            f.Clock = 109999; f.Port.Inspect();
            f.Clock = 110000;
            Assert(MustFail(() => f.Port.Inspect()).Contains("30 segundos") && f.EnvironmentReads == 1,
                "the shared budget was absolute or reset by a repeated inspection");
        });
    }

    private static ControllerModeState ModeP1State(int mode) => new ControllerModeState {
        Receiver = mode == 1 ? ModeP1Root : ModeP1Temporary, Location = ModeP1Location,
        Mode = mode, Service = "usbccgp", Present = true, Problem = 0
    };

    private sealed class ModeP1Fixture : IPlayerOnePort, IPlayerOneModeRecovery
    {
        internal readonly ModePlayerOnePort Port;
        internal string Endpoint = ModeP1Endpoint, BoundRoot, BoundLocation;
        internal ControllerModeState Mode = ModeP1State(1);
        internal long Clock;
        internal bool CanceledValue, Journal, Connected, CheckActiveFailure;
        internal int PhysicalSlot, Enters, Connects, Restores, Completions, ModeReads, EnvironmentReads;
        private readonly int targets;
        internal int?[] VirtualSlotsOverride;
        internal bool NullSlotRead;
        internal Action OnEnter, OnConnect, OnRestore, OnComplete, OnModeInspect, OnEnvironment, OnSlot, OnWait, OnActive;
        internal PlayerOneEnvironment OverrideEnvironment;
        internal readonly List<string> Calls = new List<string>();
        internal ModeP1Fixture(long clock = 0, bool canceled = false, int targets = 1, Action protect = null)
        {
            this.targets = targets;
            Clock = clock; CanceledValue = canceled;
            Port = new ModePlayerOnePort(this, this,
                () => Mode != null && Mode.Mode == 1 ? Endpoint : null,
                endpoint => endpoint == ModeP1Endpoint ? ModeP1Root : null, targets, () => {
                    OnSlot?.Invoke();
                    if (NullSlotRead) return null;
                    return VirtualSlotsOverride ?? (Connected ? (targets == 2 ? new int?[] { 0, 1 } : new int?[] { 0 }) : new int?[targets]);
                }, protect == null ? (Action)null : () => { Calls.Add("protect"); protect(); });
        }
        public long ElapsedMilliseconds => Clock;
        public bool Canceled => CanceledValue;
        PlayerOneEnvironment IPlayerOnePort.Inspect()
        {
            EnvironmentReads++; OnEnvironment?.Invoke();
            if (OverrideEnvironment != null) return OverrideEnvironment;
            bool physical = Mode != null && Mode.Mode == 1;
            if (Connected) return targets == 2 ? (physical ? AutomaticPreparationState(1, 2, 0, 1, 2) : AutomaticPreparationState(0, 2, 0, 1))
                : physical ? AutomaticPreparationState(1, 1, 0, 1) : AutomaticPreparationState(0, 1, 0);
            return physical ? AutomaticPreparationState(1, 0, PhysicalSlot) : AutomaticPreparationState(0, 0);
        }
        public void ConnectVirtual() { Connects++; Calls.Add("connect"); Connected = true; OnConnect?.Invoke(); }
        public int? ReadVirtualSlot() { OnSlot?.Invoke(); return Connected ? 0 : (int?)null; }
        public void Wait(int milliseconds) { Clock += milliseconds; OnWait?.Invoke(); }
        ControllerModeState IPlayerOneModeRecovery.Inspect()
        { ModeReads++; OnModeInspect?.Invoke(); return Mode == null ? null : Protocol.Decode<ControllerModeState>(Protocol.Encode(Mode)); }
        public void EnterDirectInput(string originalReceiver, string originalLocation)
        {
            Enters++; Calls.Add("enter"); BoundRoot = originalReceiver; BoundLocation = originalLocation;
            Journal = true; Mode = ModeP1State(2); OnEnter?.Invoke();
        }
        public void CheckActive()
        { OnActive?.Invoke(); if (!Journal || CheckActiveFailure) throw new InvalidOperationException("respaldo inactivo"); }
        public void Restore() { Restores++; Calls.Add("restore"); Mode = ModeP1State(1); OnRestore?.Invoke(); }
        public void CompleteRecovery()
        { Completions++; Calls.Add("complete"); OnComplete?.Invoke(); Journal = false; }
    }
}
