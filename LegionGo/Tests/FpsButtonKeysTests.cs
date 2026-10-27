// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LegionTweaks;

internal static partial class Program
{
    private static byte[] FpsReport(byte bits = 0) => LegionCandidateReport(false, 0, bits);
    private static void FpsButtonKeysTests()
    {
        Test("FPS decoder uses observed rear masks without relabelling the gamepad map", () => {
            int buttons, legacy;
            Assert(FpsButtonKeys.TryDecode(0x61ee, FpsReport(0x20), out buttons) && buttons == 1, "Y3 observation lost");
            Assert(FpsButtonKeys.TryDecode(0x61ee, FpsReport(0x04), out buttons) && buttons == 2, "FPS M3 observation lost");
            Assert(FpsButtonKeys.TryDecode(0x61ee, FpsReport(0x24), out buttons) && buttons == 3, "simultaneous buttons lost");
            Assert(FpsButtonKeys.TryDecode(0x61ee, FpsReport(0x48), out buttons) && buttons == 0, "Y2/gamepad M3 became an FPS key");
            Assert(LegionButtonReport.TryDecodeControllerButtons(FpsReport(4), out legacy) && legacy == 32, "global gamepad M2 mapping changed");
            Assert(LegionButtonReport.TryDecodeControllerButtons(FpsReport(8), out legacy) && legacy == 64, "global gamepad M3 mapping changed");
        });
        Test("FPS decoder rejects other modes formats lengths and command replies", () => {
            int buttons;
            foreach (ushort product in new ushort[] { 0, 0x6182, 0x61eb, 0x61ec, 0x61ed })
                Assert(!FpsButtonKeys.TryDecode(product, FpsReport(0x24), out buttons) && buttons == 0, "non-FPS PID decoded");
            foreach (byte[] report in new[] { null, new byte[0], new byte[63], new byte[65], LegionCandidateReport(true, 0, 0x24) })
                Assert(!FpsButtonKeys.TryDecode(0x61ee, report, out buttons) && buttons == 0, "unknown report decoded");
            var unrelated = FpsReport(0x24); unrelated[2] = 0x75;
            Assert(!FpsButtonKeys.TryDecode(0x61ee, unrelated, out buttons), "unobserved header decoded");
        });
        Test("FPS keys start disabled require release and pulse once per independent press", () => {
            var keys = new FpsButtonKeys();
            Assert(keys.Observe(0x61ee, FpsReport(0x24), 0, true, 1) == 0 && !keys.Enabled, "default activated output");
            keys.Configure(true);
            Assert(keys.Observe(0x61ee, FpsReport(0x24), 0, true, 1) == 0 && keys.WaitingForRelease, "held enable escaped");
            Assert(keys.Observe(0x61ee, FpsReport(), 1, true, 1) == 0 && !keys.WaitingForRelease, "release did not arm");
            Assert(keys.Observe(0x61ee, FpsReport(0x20), 2, true, 1) == 1, "Insert missing");
            Assert(keys.Observe(0x61ee, FpsReport(0x20), 3, true, 1) == 0, "held Insert repeated");
            Assert(keys.Observe(0x61ee, FpsReport(0x24), 4, true, 1) == 2, "independent Escape missing");
            Assert(keys.Observe(0x61ee, FpsReport(), 5, true, 1) == 0, "release emitted a key");
            Assert(keys.Observe(0x61ee, FpsReport(0x24), 6, true, 1) == 3, "new simultaneous press missing");
            keys.Configure(false);
            Assert(keys.Observe(0x61ee, FpsReport(0x24), 7, true, 1) == 0, "disabled keys emitted");
        });
        Test("FPS focus stale source unknown reports and blocked modifiers rearm only after release", () => {
            for (int reason = 0; reason < 6; reason++) {
                var keys = new FpsButtonKeys(); keys.Configure(true);
                keys.Observe(0x61ee, FpsReport(), 0, true, 1);
                long time = reason == 0 ? 251 : reason == 1 ? -1 : 1;
                long window = reason == 2 ? 2 : 1;
                int actions = keys.Observe(reason == 5 ? (ushort)0x61eb : (ushort)0x61ee,
                    reason == 4 ? null : FpsReport(0x24), time, reason != 3, window);
                Assert(actions == 0, "interrupted input emitted keys");
                Assert(keys.Observe(0x61ee, FpsReport(0x24), 252, true, window) == 0, "held recovery emitted keys");
                keys.Observe(0x61ee, FpsReport(), 253, true, window);
                Assert(keys.Observe(0x61ee, FpsReport(0x24), 254, true, window) == 3, "fresh release did not rearm");
            }
        });
        Test("FPS output emits fixed paired keys and protects modifiers focus and user-held keys", () => {
            var held = new HashSet<int>(); var batches = new List<GameBarShortcut.Input[]>();
            var desktop = new MouseDesktop { Available = true, Foreground = 7 };
            var output = new FpsKeyOutput(() => desktop, held.Contains, keys => { batches.Add(keys); return (uint)keys.Length; });
            foreach (int key in new[] { 0x2d, 0x1b }) Assert(output.Pulse(key, desktop), "paired key missing");
            Assert(batches.Count == 2 && batches[0][0].Key == 0x2d && batches[0][0].Flags == 1
                && batches[0][1].Flags == 3 && batches[1][0].Key == 0x1b && batches[1][1].Flags == 2, "wrong key ABI or missing release");
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c, 0x2d }) {
                held.Add(key); Assert(!output.Pulse(0x2d, desktop), "user-held key released or modifier combined"); held.Clear();
            }
            Assert(!output.Pulse(0x1b, new MouseDesktop { Available = true, Foreground = 8 }), "changed focus accepted");
            desktop.Available = false;
            Assert(!output.Pulse(0x1b, desktop) && batches.Count == 2, "secure/unavailable desktop accepted");
            LegionButtonThrows<ArgumentOutOfRangeException>(() => output.Pulse(0x41, desktop));
        });
        Test("FPS partial send releases only its accepted key and retains failed cleanup", () => {
            var desktop = new MouseDesktop { Available = true, Foreground = 1 };
            var batches = new List<GameBarShortcut.Input[]>(); bool allowRelease = false;
            var output = new FpsKeyOutput(() => desktop, key => false, keys => {
                batches.Add(keys); return keys.Length == 2 || allowRelease ? 1u : 0u;
            });
            LegionButtonThrows<System.ComponentModel.Win32Exception>(() => output.Pulse(0x2d, desktop));
            Assert(batches.Count == 4 && batches.Skip(1).All(x => x.Length == 1 && x[0].Key == 0x2d && x[0].Flags == 3), "cleanup released unrelated keys");
            Assert(!output.ReleaseOwned(), "failed cleanup was discarded");
            allowRelease = true; Assert(output.ReleaseOwned(), "pending owned key was not retried");
            int count = batches.Count; Assert(output.ReleaseOwned() && batches.Count == count, "cleanup released an unowned key");
        });
        Test("FPS IPC is explicit rejects mixed requests and never applies power or starts recording", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var shortcuts = new FpsSessionShortcuts();
            var controller = new EnginePreparationController();
            using (var session = new HelperSession(new ControlEngine(hardware, journal, controller), folder, false, controller, shortcuts)) {
                Assert(session.Execute(new Request { Operation = "recording_status" }).Success && shortcuts.Calls == 0, "poll enabled keys");
                foreach (int[] values in new[] { null, new int[0], new[] { -1 }, new[] { 2 }, new[] { 1, 0 } })
                    Assert(!session.Execute(new Request { Operation = "fps_keys_set", Values = values }).Success, "invalid FPS request accepted");
                Assert(!session.Execute(new Request { Operation = "fps_keys_set", Values = new[] { 1 }, Profile = FullProfile() }).Success, "profile applied with keys");
                Assert(!session.Execute(new Request { Operation = "fps_keys_set", Values = new[] { 1 }, Ac = true }).Success, "power request accepted");
                controller.Preparing = true;
                Assert(!session.Execute(new Request { Operation = "fps_keys_set", Values = new[] { 1 } }).Success, "preparation allowed FPS");
                Assert(session.Execute(new Request { Operation = "fps_keys_set", Values = new[] { 0 } }).Success, "OFF was blocked");
                controller.Preparing = false;
                Assert(session.Execute(new Request { Operation = "fps_keys_set", Values = new[] { 1 } }).Success && shortcuts.Enabled, "explicit enable refused");
                Assert(hardware.Writes.Count == 0 && journal.Saves == 0 && controller.Applies == 0 && !session.Recorder.GetStatus().Active, "FPS applied unrelated state");
            }
        }));
        Test("FPS IPC remains absent on older helpers", () => WithRecordingDirectory(folder => {
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false, shortcuts: new FakeShortcuts()))
                Assert(!session.Execute(new Request { Operation = "fps_keys_set", Values = new[] { 1 } }).Success, "old helper accepted FPS");
            Assert(Protocol.Decode<LegionShortcutStatus>("{}").FpsKeys == null, "old response advertised FPS support");
        }));
        Test("FPS service reuses one reader counts pulses and disables on capture suspension virtual or disconnect", () => {
            for (int reason = 0; reason < 5; reason++) WithLegionButtonDirectory(folder => {
                using (var bus = new FpsTestBus()) using (var service = bus.Create(folder)) {
                    Assert(!service.GetStatus().FpsKeys.Enabled && bus.Opens == 0, "startup opened FPS reader");
                    service.SetFpsKeys(true); LegionButtonWait(() => bus.Active == 1, "FPS reader not opened");
                    bus.Push(service, 0); bus.Push(service, 0x20); bus.Push(service, 0x20); bus.Push(service, 0x24);
                    var status = service.GetStatus().FpsKeys;
                    Assert(status.InsertSent == 1 && status.EscapeSent == 1 && bus.Output.Sent.SequenceEqual(new[] { 0x2d, 0x1b }), "service output differs");
                    if (reason == 0) service.StartCapture(4, 1);
                    if (reason == 1) { service.Suspend(true); service.Suspend(false); }
                    if (reason == 2) service.SetGamepadInputEnabled(true);
                    if (reason == 3) service.SetPreparationPaused(true);
                    if (reason == 4) { bus.Fail(); LegionButtonWait(() => !service.GetStatus().FpsKeys.Enabled, "disconnect retained FPS enable"); }
                    Assert(!service.GetStatus().FpsKeys.Enabled && bus.MaximumActive == 1, "lifecycle reenabled FPS or opened second reader");
                }
            });
        });
        Test("FPS service refuses non-FPS receiver and stops on keyboard output failure", () => {
            foreach (bool wrongMode in new[] { false, true }) WithLegionButtonDirectory(folder => {
                using (var bus = new FpsTestBus { Product = wrongMode ? (ushort)0x61eb : (ushort)0x61ee })
                using (var service = bus.Create(folder)) {
                    service.SetFpsKeys(true);
                    if (wrongMode) LegionButtonWait(() => !service.GetStatus().FpsKeys.Enabled, "gamepad source accepted for FPS");
                    else {
                        LegionButtonWait(() => bus.Active == 1, "reader missing");
                        bus.Push(service, 0); bus.Output.Fail = true; bus.Push(service, 0x20);
                        Assert(!service.GetStatus().FpsKeys.Enabled && service.GetStatus().FpsKeys.Error != null, "output error retained enable");
                    }
                }
            });
        });
        Test("An old pending reader open cannot disable a newer explicit FPS request", () => WithLegionButtonDirectory(folder => {
            using (var bus = new FpsTestBus { Product = 0x61eb, BlockOpen = true })
            using (var service = bus.Create(folder)) {
                service.Configure(LegionShortcutMode.Left);
                Assert(bus.OpenEntered.WaitOne(3000), "old open did not start");
                try { bus.Product = 0x61ee; service.SetFpsKeys(true); }
                finally { bus.ReleaseOpen.Set(); }
                LegionButtonWait(() => bus.Opens >= 2 && bus.Active == 1, "new request did not reopen the reader");
                Assert(service.GetStatus().FpsKeys.Enabled, "stale mode check disabled a newer request");
                bus.Push(service, 0); bus.Push(service, 0x20);
                Assert(service.GetStatus().FpsKeys.InsertSent == 1 && bus.MaximumActive == 1, "reader overlap or missing Insert");
            }
        }));
    }
    private sealed class FpsSessionShortcuts : ILegionShortcuts, IFpsButtonKeys
    {
        internal int Calls; internal bool Enabled;
        public void SetFpsKeys(bool enabled) { Calls++; Enabled = enabled; }
        public void Configure(LegionShortcutMode mode) { }
        public LegionShortcutStatus GetStatus() => new LegionShortcutStatus { FpsKeys = new FpsButtonKeysStatus { Enabled = Enabled } };
    }
    private sealed class FpsTestOutput : IFpsKeyOutput
    {
        internal readonly List<int> Sent = new List<int>(); internal bool Fail;
        public MouseDesktop Inspect() => new MouseDesktop { Available = true, Foreground = 1 };
        public bool Pulse(int key, MouseDesktop expected) { if (Fail) throw new IOException("output failure"); Sent.Add(key); return true; }
        public bool ReleaseOwned() => true;
    }
    private sealed class FpsTestBus : IDisposable
    {
        internal ushort Product = 0x61ee;
        internal int Active, MaximumActive, Opens;
        internal readonly FpsTestOutput Output = new FpsTestOutput();
        internal bool BlockOpen;
        internal readonly ManualResetEvent OpenEntered = new ManualResetEvent(false), ReleaseOpen = new ManualResetEvent(false);
        private readonly Queue<byte[]> reports = new Queue<byte[]>();
        private readonly AutoResetEvent ready = new AutoResetEvent(false);
        private long time; private bool fail;
        internal LegionShortcutService Create(string folder) => new LegionShortcutService(folder, () => Interlocked.Read(ref time), diagnostic => {
            int active = Interlocked.Increment(ref Active); if (active > MaximumActive) MaximumActive = active;
            Interlocked.Increment(ref Opens); ushort product = Product;
            if (BlockOpen) { BlockOpen = false; OpenEntered.Set(); ReleaseOpen.WaitOne(); }
            return new Reader(this, product);
        }, () => { }, Output);
        internal void Push(LegionShortcutService service, byte bits)
        {
            long before = service.GetStatus().Reports;
            lock (reports) reports.Enqueue(FpsReport(bits)); ready.Set();
            LegionButtonWait(() => service.GetStatus().Reports > before, "FPS report not processed");
        }
        internal void Fail() { lock (reports) fail = true; ready.Set(); }
        public void Dispose() { ready.Dispose(); OpenEntered.Dispose(); ReleaseOpen.Dispose(); }
        private sealed class Reader : ILegionButtonReader, IFpsButtonReader
        {
            private readonly FpsTestBus bus; public ushort Product { get; }
            internal Reader(FpsTestBus bus, ushort product) { this.bus = bus; Product = product; }
            public byte[] Read(WaitHandle stop, WaitHandle changed)
            {
                while (true) {
                    if (stop.WaitOne(0) || changed.WaitOne(0)) throw new OperationCanceledException();
                    lock (bus.reports) {
                        if (bus.fail) { bus.fail = false; throw new IOException("reader disconnected"); }
                        if (bus.reports.Count != 0) { Interlocked.Add(ref bus.time, 10); return bus.reports.Dequeue(); }
                    }
                    if (WaitHandle.WaitAny(new[] { stop, changed, bus.ready }, 50) < 2) throw new OperationCanceledException();
                }
            }
            public void Dispose() { Interlocked.Decrement(ref bus.Active); }
        }
    }
}
