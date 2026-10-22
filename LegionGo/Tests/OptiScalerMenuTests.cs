// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;
using LegionTweaks;

internal static partial class Program
{
    private static OptiScalerWindow OptiBar => new OptiScalerWindow { Available = true, GameBar = true, Handle = 1, ProcessId = 10 };
    private static OptiScalerWindow OptiGame => new OptiScalerWindow { Available = true, Eligible = true, Handle = 2, ProcessId = 20 };
    private static void OptiScalerMenuTests()
    {
        Test("OptiScaler closes Game Bar then sends one Insert after stable focus", () => {
            var port = new OptiPort(); var shortcut = new OptiScalerMenuShortcut(true, port);
            Assert(port.Closes == 0 && port.Sends == 0, "construction injected input");
            shortcut.OpenMenu();
            Assert(port.Closes == 1 && port.Sends == 1 && port.Waits == 6 && port.Target.Same(OptiGame), "close/stable/insert order wrong");
        });
        Test("OptiScaler unsupported device and absent Game Bar never inject", () => {
            var port = new OptiPort(); bool failed = false;
            try { new OptiScalerMenuShortcut(false, port).OpenMenu(); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && port.Inspections == 0 && port.Closes == 0, "unsupported input accessed");
            foreach (var initial in new[] { OptiGame, default(OptiScalerWindow) }) {
                port = new OptiPort { Initial = initial }; failed = false;
                try { new OptiScalerMenuShortcut(true, port).OpenMenu(); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && port.Closes == 0 && port.Sends == 0, "standalone/locked desktop injected");
            }
        });
        Test("OptiScaler blocked desktop after closing Game Bar cancels Insert", () => {
            foreach (var destination in new[] { default(OptiScalerWindow), new OptiScalerWindow { Available = true, Handle = 99, ProcessId = 99 } }) {
                var port = new OptiPort { Destination = destination }; bool failed = false;
                try { new OptiScalerMenuShortcut(true, port).OpenMenu(); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && port.Closes == 1 && port.Sends == 0 && port.Waits == 1, "blocked window accepted");
            }
        });
        Test("OptiScaler cancels changed focus process and reopened Game Bar", () => {
            foreach (var changed in new[] { OptiBar, new OptiScalerWindow { Available = true, Eligible = true, Handle = 3, ProcessId = 20 },
                new OptiScalerWindow { Available = true, Eligible = true, Handle = 2, ProcessId = 99 } }) {
                var port = new OptiPort { Changed = changed, ChangeAt = 3 }; bool failed = false;
                try { new OptiScalerMenuShortcut(true, port).OpenMenu(); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && port.Closes == 1 && port.Sends == 0 && port.Waits == 3, "focus change injected to wrong window");
            }
        });
        Test("OptiScaler timeout and keyboard rejection never retry the shortcut", () => {
            var port = new OptiPort { Destination = OptiBar }; bool failed = false;
            try { new OptiScalerMenuShortcut(true, port).OpenMenu(); } catch (TimeoutException) { failed = true; }
            Assert(failed && port.Closes == 1 && port.Sends == 0 && port.Waits == 80, "timeout unbounded or toggled again");
            port = new OptiPort { FailSend = true }; failed = false;
            try { new OptiScalerMenuShortcut(true, port).OpenMenu(); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && port.Sends == 1 && port.Closes == 1, "keyboard failure retried");
        });
        Test("OptiScaler session accepts only the explicit fixed action and reports capability", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var port = new OptiPort();
            using (var session = new HelperSession(new ControlEngine(hardware, journal), folder, false,
                optiScaler: new OptiScalerMenuShortcut(true, port))) {
                var idle = session.Execute(new Request { Operation = "recording_status" });
                Assert(idle.SupportsOptiScalerMenu && port.Closes == 0 && port.Inspections == 0, "status injected or read focus");
                foreach (var request in new[] { new Request { Version = 2, Operation = "optiscaler_menu" },
                    new Request { Operation = "optiscaler_menu", Key = "Insert" }, new Request { Operation = "optiscaler_menu", Values = new[] { 45 } },
                    new Request { Operation = "optiscaler_menu", Ac = true }, new Request { Operation = "optiscaler_menu", Profile = new ProfileDefinition() },
                    new Request { Operation = "optiscaler_menu", RtssAppearance = new RtssAppearance() } })
                    Assert(!session.Execute(request).Success, "arbitrary keyboard payload accepted");
                Assert(port.Closes == 0 && port.Inspections == 0, "invalid request reached native action");
                var response = session.Execute(new Request { Operation = "optiscaler_menu" });
                Assert(response.Success && port.Sends == 1 && response.Message.Contains("no se confirmó"), "sent key claims loaded OptiScaler");
                Assert(hardware.Reads.Count == 0 && hardware.Writes.Count == 0 && journal.Saves == 0, "shortcut touched hardware/power");
            }
        }));
        Test("OptiScaler missing capability and focus failure stay explicit", () => WithRecordingDirectory(folder => {
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
                Assert(!session.Execute(new Request { Operation = "optiscaler_menu" }).Success
                    && !session.Execute(new Request { Operation = "recording_status" }).SupportsOptiScalerMenu, "old helper claims support");
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false,
                optiScaler: new OptiScalerMenuShortcut(true, new OptiPort { Initial = OptiGame })))
                Assert(!session.Execute(new Request { Operation = "optiscaler_menu" }).Success, "focus failure became success");
        }));
        Test("OptiScaler Insert uses x64 INPUT with extended down and up flags", () => {
            var down = NativeOptiScalerMenuPort.Insert(false); var up = NativeOptiScalerMenuPort.Insert(true);
            Assert(Marshal.SizeOf(typeof(GameBarShortcut.Input)) == 40 && down.Type == 1 && down.Key == 0x2d && down.Flags == 1
                && up.Type == 1 && up.Key == 0x2d && up.Flags == 3, "Insert flags or ABI wrong");
        });
        Test("OptiScaler keyboard preserves user held modifiers and Insert", () => {
            foreach (int held in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x2d }) {
                var keyboard = new OptiKeyboard { Held = held }; bool failed = false;
                try { OptiScalerInsert.Send(keyboard, OptiGame); } catch (InvalidOperationException) { failed = true; }
                Assert(failed && keyboard.Sends == 0, "user-held key injected/released");
            }
        });
        Test("OptiScaler keyboard rechecks focus and freshness immediately before injection", () => {
            var keyboard = new OptiKeyboard { ChangeSecond = true }; bool failed = false;
            try { OptiScalerInsert.Send(keyboard, OptiGame); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && keyboard.Sends == 0, "focus changed during modifier check");
            keyboard = new OptiKeyboard(); failed = false;
            try { OptiScalerInsert.Send(keyboard, OptiGame, () => 101); } catch (InvalidOperationException) { failed = true; }
            Assert(failed && keyboard.Sends == 0, "stale focus observation injected");
        });
        Test("OptiScaler keyboard cleans up only a partially accepted down event", () => {
            foreach (uint accepted in new uint[] { 0, 1, 2 }) {
                var keyboard = new OptiKeyboard { Accepted = accepted }; bool failed = false;
                try { OptiScalerInsert.Send(keyboard, OptiGame); } catch (System.ComponentModel.Win32Exception) { failed = true; }
                Assert(failed == (accepted != 2) && keyboard.Sends == (accepted == 1 ? 2 : 1), "input failure retried or released an unowned key");
                if (accepted == 1) Assert(keyboard.Last.Length == 1 && keyboard.Last[0].Flags == 3, "cleanup sent another press");
            }
        });
        Test("OptiScaler keyboard ownership rejects overlapping shortcuts without blocking HID", () => {
            using (var ready = new System.Threading.ManualResetEvent(false))
            using (var release = new System.Threading.ManualResetEvent(false)) {
                var owner = System.Threading.Tasks.Task.Run(() => GameBarShortcut.WithKeyboardGate(() => {
                    ready.Set(); release.WaitOne(5000);
                }));
                bool entered = false, rejected = false;
                try {
                    Assert(ready.WaitOne(5000), "owner not started");
                    try { GameBarShortcut.WithKeyboardGate(() => entered = true); } catch (InvalidOperationException) { rejected = true; }
                    Assert(rejected && !entered && !owner.IsCompleted, "contending input reader waited or executed");
                }
                finally { release.Set(); owner.GetAwaiter().GetResult(); }
            }
            bool nested = false;
            GameBarShortcut.WithKeyboardGate(() => GameBarShortcut.WithKeyboardGate(() => nested = true));
            Assert(nested, "owned close-Game-Bar step cannot reenter");
        });
    }
    private sealed class OptiPort : IOptiScalerMenuPort
    {
        internal OptiScalerWindow Initial = OptiBar, Destination = OptiGame, Changed, Target;
        internal int Inspections, Closes, Sends, Waits, ChangeAt = int.MaxValue;
        internal bool FailSend;
        public OptiScalerWindow Inspect() { Inspections++; return Closes == 0 ? Initial : Waits >= ChangeAt ? Changed : Destination; }
        public void CloseGameBar() { Closes++; }
        public void Wait(int milliseconds) { Assert(milliseconds == 25, "unexpected wait"); Waits++; }
        public void SendInsert(OptiScalerWindow expected) { Sends++; if (FailSend) throw new InvalidOperationException("Keyboard rejected"); Target = expected; }
    }
    private sealed class OptiKeyboard : IOptiScalerKeyboardPort
    {
        internal int Held = -1, Inspections, Sends;
        internal bool ChangeSecond;
        internal uint Accepted = 2;
        internal GameBarShortcut.Input[] Last;
        public OptiScalerWindow Inspect() { Inspections++; return ChangeSecond && Inspections > 1 ? OptiBar : OptiGame; }
        public bool IsKeyDown(int key) => Held == key;
        public uint SendKeys(GameBarShortcut.Input[] keys) { Sends++; Last = keys; return Accepted; }
    }
}
