// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private static void QamLifecycleTests()
    {
        Test("QAM lifecycle accepts only fixed menu operations and rejects settings payloads", () => {
            foreach (string operation in new[] { "qam_open", "qam_status", "qam_configure" }) {
                var request = new Request { Operation = operation, Values = operation == "qam_configure" ? new[] { 1, 1, 3 } : null };
                QamLifecycleRules.Validate(request);
                request.Key = "Mode"; bool rejected = false;
                try { QamLifecycleRules.Validate(request); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "menu accepted a settings field");
            }
            foreach (int[] values in new[] { null, new int[0], new[] { 1 }, new[] { 2, 1, 0 }, new[] { 1, 4, 0 }, new[] { 1, 0, -1 } }) {
                bool rejected = false;
                try { QamLifecycleRules.Validate(new Request { Operation = "qam_configure", Values = values }); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "invalid menu configuration admitted");
            }
            Assert(!ReadOnlyQueries.Allows(new Request { Operation = "telemetry", DesktopView = new DesktopViewRegistration { Window = 1, ViewId = Guid.NewGuid().ToString("N") } }), "desktop payload leaked into read-only channel");
        });
        Test("Menu gestures distinguish both singles and a rolling chord only on full release", () => {
            foreach (int mode in new[] { 1, 2, 3 }) {
                var router = new MenuShortcutRouter();
                Assert(router.Observe(mode, 0) == 0 && router.Observe(mode, 70) == 0 && router.Observe(0, 80) == 0, "startup held button acted");
                Assert(router.Observe(mode, 100) == 0 && router.Observe(mode, 170) == 0 && router.Observe(0, 180) == (LegionShortcutMode)mode, "gesture did not fire on release");
                Assert(router.Observe(0, 190) == 0, "gesture repeated");
            }
            var chord = new MenuShortcutRouter(); chord.Observe(0, 0); chord.Observe(1, 10); chord.Observe(1, 80);
            Assert(chord.Observe(3, 90) == 0 && chord.Observe(2, 100) == 0 && chord.Observe(0, 110) == LegionShortcutMode.Both, "chord fired either single");
        });
        Test("Menu gestures discard taps stale reports invalid bits and reconnect-held inputs", () => {
            var router = new MenuShortcutRouter(); router.Observe(0, 0); router.Observe(1, 10);
            Assert(router.Observe(0, 20) == 0, "short tap accepted");
            router.Observe(2, 30); router.Observe(2, 100);
            Assert(router.Observe(0, 2000) == 0, "stale release acted");
            router.Observe(3, 2010); router.Reset();
            Assert(router.Observe(0, 2020) == 0 && router.Observe(99, 2030) == 0, "reset/invalid bits acted");
        });
        Test("Menu assignment permits distinct L R and L+R and rejects duplicate destinations", () => {
            Assert(!QamLifecycleRules.Conflicts(LegionShortcutMode.Left, LegionShortcutMode.Both)
                && QamLifecycleRules.Conflicts(LegionShortcutMode.Right, LegionShortcutMode.Right)
                && !QamLifecycleRules.Conflicts(0, 0), "gesture conflict rules wrong");
            var pref = new QamPreferences { ToggleMode = LegionShortcutMode.Left, DesktopToggleMode = LegionShortcutMode.Both }; pref.Validate();
            pref.DesktopToggleMode = LegionShortcutMode.Left; bool rejected = false;
            try { pref.Validate(); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "two destinations used one gesture");
        });
        Test("QAM session checks reject session zero and a different logon session", () => {
            Assert(!QamLifecycleRules.SameInteractiveSession(0, 0) && !QamLifecycleRules.SameInteractiveSession(2, 3)
                && QamLifecycleRules.SameInteractiveSession(2, 2), "session boundary wrong");
            var runtime = new MenuRuntime { SessionId = 0 };
            using (var control = new QamLifecycleService(true, new MenuStore(), runtime, new MenuButtons())) {
                bool rejected = false; try { control.Open(); } catch (InvalidOperationException) { rejected = true; }
                control.RunAtStartup(); Assert(rejected && runtime.Commands == 0 && runtime.Starts == 0, "session zero launched UI");
            }
        });
        Test("QAM missing preferences default to hidden-start off and never create a file when read", () => WithRecordingDirectory(folder => {
            var store = new QamPreferenceStore(folder); var value = store.Load();
            Assert(!value.StartWithLegionTweaks && value.ToggleMode == LegionShortcutMode.Left && value.DesktopToggleMode == 0
                && Directory.GetFiles(folder).Length == 0, "query wrote preferences or opted in");
            store.Save(new QamPreferences { StartWithLegionTweaks = true, ToggleMode = LegionShortcutMode.Right, DesktopToggleMode = LegionShortcutMode.Both });
            var actual = new QamPreferenceStore(folder).Load();
            Assert(actual.StartWithLegionTweaks && actual.ToggleMode == LegionShortcutMode.Right && actual.DesktopToggleMode == LegionShortcutMode.Both
                && Directory.GetFiles(folder).Length == 1, "preferences did not survive reopen");
        }));
        Test("QAM invalid preferences remain preserved and explicit repair keeps an invalid backup", () => WithRecordingDirectory(folder => {
            var path = Path.Combine(folder, QamPreferenceStore.Name); const string broken = "{broken"; File.WriteAllText(path, broken);
            var runtime = new MenuRuntime();
            using (var control = new QamLifecycleService(true, new QamPreferenceStore(folder), runtime, new MenuButtons())) {
                control.RunAtStartup();
                Assert(!control.GetStatus().PreferenceKnown && runtime.Starts == 0 && File.ReadAllText(path) == broken, "invalid preference applied or overwritten on load");
                control.Configure(false, LegionShortcutMode.Left, 0);
                Assert(Directory.GetFiles(folder, "*.invalid-*.json").Length == 1
                    && File.ReadAllText(Directory.GetFiles(folder, "*.invalid-*.json")[0]) == broken, "repair lost original bytes");
            }
        }));
        Test("QAM configure persists only menus and does not launch apply hardware or replace Game Bar", () => {
            var store = new MenuStore(); var runtime = new MenuRuntime(); var buttons = new MenuButtons();
            using (var control = new QamLifecycleService(true, store, runtime, buttons)) {
                var saved = control.Configure(true, LegionShortcutMode.Left, LegionShortcutMode.Both);
                Assert(saved.StartWithLegionTweaks && buttons.QamShortcut == LegionShortcutMode.Left && buttons.DesktopShortcut == LegionShortcutMode.Both
                    && buttons.GameBar == LegionShortcutMode.Right && store.Saves == 1 && runtime.Starts == 0 && runtime.Commands == 0, "saving launched or reassigned Game Bar");
                control.Configure(false, 0, 0);
                Assert(!control.GetStatus().StartWithLegionTweaks && runtime.Commands == 0, "disabling startup sent a close");
            }
        });
        Test("QAM rejected assignment or failed durable save leaves live routing and preferences untouched", () => {
            var store = new MenuStore(); var buttons = new MenuButtons();
            using (var control = new QamLifecycleService(true, store, new MenuRuntime(), buttons)) {
                bool conflict = false; try { control.Configure(true, LegionShortcutMode.Right, 0); } catch (InvalidOperationException) { conflict = true; }
                Assert(conflict && store.Saves == 0 && buttons.QamShortcut == LegionShortcutMode.Left, "conflict partially saved");
                store.Fail = true; bool failed = false;
                try { control.Configure(true, LegionShortcutMode.Both, 0); } catch (IOException) { failed = true; }
                Assert(failed && buttons.QamShortcut == LegionShortcutMode.Left && !control.GetStatus().StartWithLegionTweaks, "failed save changed live route");
            }
        });
        Test("QAM default conflict preserves existing Game Bar and leaves desktop open usable", () => {
            var buttons = new MenuButtons { GameBar = LegionShortcutMode.Left }; var runtime = new MenuRuntime();
            using (var control = new QamLifecycleService(true, new MenuStore(), runtime, buttons)) {
                Assert(buttons.GameBar == LegionShortcutMode.Left && buttons.QamShortcut == 0 && control.GetStatus().Error != null, "boot overwrote Game Bar");
                Assert(control.Open().Running && runtime.Starts == 1 && control.GetStatus().Error != null, "desktop open depended on assigned button or hid its unresolved conflict");
            }
        });
        Test("QAM desktop Open starts or reuses one instance and never toggles it closed", () => {
            var runtime = new MenuRuntime();
            using (var control = new QamLifecycleService(true, new MenuStore(), runtime, new MenuButtons())) {
                Assert(control.Open().Visible && control.Open().Visible && runtime.Starts == 1 && runtime.Commands == 2, "Open hid or duplicated QAM");
                runtime.Running = false;
                Assert(!control.GetStatus().Running && control.Open().Running && runtime.Starts == 2, "closed instance was not restarted");
            }
        });
        Test("QAM enabled startup stays hidden and a helper restart reuses the same live instance", () => {
            var store = new MenuStore { Value = new QamPreferences { StartWithLegionTweaks = true } }; var runtime = new MenuRuntime();
            using (var first = new QamLifecycleService(true, store, runtime, new MenuButtons())) {
                first.RunAtStartup(); WaitMenu(() => runtime.Starts == 1);
                Assert(runtime.Running && !runtime.Visible && runtime.Commands == 0, "automatic start showed menu");
            }
            using (var second = new QamLifecycleService(true, store, runtime, new MenuButtons())) {
                second.RunAtStartup(); WaitMenu(() => runtime.Ensures >= 2);
                Assert(runtime.Starts == 1 && runtime.Running && !runtime.Visible, "helper restart duplicated or opened QAM");
            }
        });
        Test("QAM suspend blocks actions and resume only warms the opted-in hidden instance", () => {
            var store = new MenuStore { Value = new QamPreferences { StartWithLegionTweaks = true } }; var runtime = new MenuRuntime();
            var buttons = new MenuButtons();
            using (var control = new QamLifecycleService(true, store, runtime, buttons)) {
                control.Suspend(); buttons.QamAction?.Invoke();
                Assert(runtime.Commands == 0, "suspended action was queued");
                control.Resume(); WaitMenu(() => runtime.Starts == 1);
                Assert(!runtime.Visible && runtime.Commands == 0, "resume applied/opened a draft");
                control.Configure(false, LegionShortcutMode.Left, 0); runtime.Running = false;
                control.Suspend(); control.Resume();
                Assert(runtime.Starts == 1 && !runtime.Running, "opt-out resumed a closed process");
            }
        });
        Test("QAM desktop registration rejects missing peer identity and forwards only verified views", () => {
            var runtime = new MenuRuntime(); var buttons = new MenuButtons();
            using (var control = new QamLifecycleService(true, new MenuStore(), runtime, buttons)) {
                var view = new DesktopViewRegistration { Window = 100, ViewId = Guid.NewGuid().ToString("N") };
                bool rejected = false; try { control.RegisterDesktop(view, 0); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && runtime.Marked == 0, "missing authenticated PID accepted");
                control.RegisterDesktop(view, 42); Assert(control.GetStatus().DesktopRegistered && runtime.Marked == 1, "desktop did not register");
                control.Configure(false, LegionShortcutMode.Left, LegionShortcutMode.Both);
                buttons.DesktopAction(); WaitMenu(() => runtime.Commands == 1 && !control.GetStatus().Busy);
                Assert(runtime.LastOperation == "desktop_toggle" && runtime.LastWindow == 100 && runtime.LastClient == 42, "desktop gesture affected QAM or wrong view");
                runtime.ValidWindow = false; Assert(!control.GetStatus().DesktopRegistered, "destroyed view stayed registered");
                buttons.DesktopAction(); WaitMenu(() => runtime.Commands == 2 && !control.GetStatus().Busy);
                Assert(runtime.LastWindow == 0 && runtime.LastClient == 0, "stale window was minimized instead of activating app");
            }
        });
        Test("QAM helper operations never dispatch energy controller display or originals writes", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var runtime = new MenuRuntime();
            using (var control = new QamLifecycleService(true, new MenuStore(), runtime, new MenuButtons()))
            using (var helper = new HelperSession(new ControlEngine(hardware, journal), folder, false, qam: control)) {
                Assert(helper.Execute(new Request { Operation = "qam_status" }).Success, "status failed");
                Assert(helper.Execute(new Request { Operation = "qam_configure", Values = new[] { 0, 1, 3 } }).Success, "config failed");
                Assert(helper.Execute(new Request { Operation = "qam_open" }).Success, "open failed");
                Assert(!helper.Execute(new Request { Operation = "qam_open", Values = new[] { 1 } }).Success
                    && !helper.Execute(new Request { Operation = "qam_fake" }).Success, "mixed/unknown operation accepted");
                Assert(hardware.Writes.Count == 0 && hardware.Reads.Count == 0 && journal.Saves == 0, "menu lifecycle reached hardware engine");
            }
        }));
        Test("QAM instance protocol rejects arbitrary operations desktop payloads and unknown versions", () => {
            Assert(new QamInstanceRequest { Operation = "status" }.Valid && new QamInstanceRequest { Operation = "open", RequestId = Guid.NewGuid().ToString("N") }.Valid, "fixed instance request invalid");
            Assert(!new QamInstanceRequest { Operation = "restore", RequestId = Guid.NewGuid().ToString("N") }.Valid
                && !new QamInstanceRequest { Operation = "status", DesktopWindow = 123 }.Valid
                && !new QamInstanceRequest { Operation = "toggle", RequestId = "bad" }.Valid
                && !new QamInstanceRequest { Version = 2, Operation = "status" }.Valid, "instance protocol accepts other payloads");
        });
        Test("QAM managed reader never duplicates the helper front-button action", () => {
            var input = new LegionTweaks.QamProbe.QamInput();
            long time = 0;
            foreach (byte bits in new byte[] { 0, 0x80, 0x80, 0, 0x40, 0x40, 0, 0xc0, 0xc0, 0 }) {
                Assert(input.Observe(LegionReport(false, bits), time += 70, false, true)
                    == LegionTweaks.QamProbe.QamAction.None, "managed QAM emitted its legacy toggle");
            }
        });
    }
    private static void WaitMenu(Func<bool> condition) { Assert(SpinWait.SpinUntil(condition, 3000), "menu operation did not finish"); }
    private sealed class MenuStore : IQamPreferenceStore
    {
        internal QamPreferences Value = new QamPreferences(); internal int Saves; internal bool Fail;
        public QamPreferences Load() => Protocol.Decode<QamPreferences>(Protocol.Encode(Value));
        public void Save(QamPreferences value) { if (Fail) throw new IOException("synthetic save failure"); Saves++; Value = Protocol.Decode<QamPreferences>(Protocol.Encode(value)); }
    }
    private sealed class MenuButtons : IQamShortcutControl
    {
        internal LegionShortcutMode GameBar = LegionShortcutMode.Right;
        internal Action QamAction, DesktopAction;
        public LegionShortcutMode QamShortcut { get; private set; }
        public LegionShortcutMode DesktopShortcut { get; private set; }
        public void SetMenuActions(Action qam, Action desktop) { QamAction = qam; DesktopAction = desktop; }
        public void ConfigureMenuShortcuts(LegionShortcutMode qam, LegionShortcutMode desktop, Action persist)
        {
            if (QamLifecycleRules.Conflicts(qam, GameBar) || QamLifecycleRules.Conflicts(desktop, GameBar) || QamLifecycleRules.Conflicts(qam, desktop))
                throw new InvalidOperationException("synthetic conflict");
            persist?.Invoke(); QamShortcut = qam; DesktopShortcut = desktop;
        }
    }
    private sealed class MenuRuntime : IQamRuntime
    {
        public int SessionId { get; set; } = 1;
        internal int Starts, Ensures, Commands, Marked, LastClient;
        internal long LastWindow;
        internal string LastOperation;
        internal bool Running, Visible, ValidWindow = true;
        public QamInstanceResponse EnsureRunning() {
            if (!Running) { Starts++; Running = true; Visible = false; }
            Interlocked.Increment(ref Ensures); return State();
        }
        public QamInstanceResponse Command(string op, DesktopViewRegistration view, int pid) {
            EnsureRunning(); LastOperation = op; LastWindow = view?.Window ?? 0; LastClient = pid;
            if (op == "open") Visible = true; else if (op == "toggle") Visible = !Visible;
            Interlocked.Increment(ref Commands); return State();
        }
        private QamInstanceResponse State() => new QamInstanceResponse { Success = true, SessionId = SessionId, Running = Running, Visible = Visible, ProcessId = 99, Message = "synthetic" };
        public bool IsRunning(int pid) => Running && pid == 99;
        public bool IsDesktopWindow(long window, int pid) => ValidWindow && window == 100 && pid == 42;
        public void RegisterDesktop(DesktopViewRegistration view, int pid) { Marked++; }
    }
}
