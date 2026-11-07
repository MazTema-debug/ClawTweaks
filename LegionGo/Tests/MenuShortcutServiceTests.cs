// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LegionTweaks;

internal static partial class Program
{
    private static void MenuShortcutServiceTests()
    {
        Test("Shared HID reader routes QAM desktop and Game Bar gestures exactly once on release", () => WithRecordingDirectory(folder => {
            using (var bus = new MenuReportBus())
            using (var service = bus.Create(folder)) {
                service.Configure(LegionShortcutMode.Right);
                service.SetMenuActions(() => bus.Qam++, () => bus.Desktop++);
                service.ConfigureMenuShortcuts(LegionShortcutMode.Left, LegionShortcutMode.Both, null);
                bus.Push(service, 0, 0); bus.Push(service, 10, 0x80); bus.Push(service, 80, 0x80);
                Assert(bus.Qam == 0, "QAM opened before release"); bus.Push(service, 90, 0);
                Assert(bus.Qam == 1 && bus.Desktop == 0 && bus.GameBar == 0, "left did not route only QAM");
                bus.Push(service, 200, 0x80); bus.Push(service, 280, 0x80); bus.Push(service, 290, 0xc0); bus.Push(service, 300, 0x40);
                bus.Push(service, 310, 0);
                Assert(bus.Desktop == 1 && bus.Qam == 1 && bus.GameBar == 0, "rolling chord triggered either single");
                bus.Push(service, 400, 0x40); bus.Push(service, 480, 0x40); bus.Push(service, 500, 0);
                Assert(bus.GameBar == 1 && bus.Qam == 1 && bus.Desktop == 1 && service.GetStatus().ReservedButtons == 3, "right route/reservations failed");
                Assert(bus.MaximumActive == 1, "menu routing opened an extra HID reader");
                service.Suspend(true); service.Suspend(false); bus.Push(service, 600, 0x80); bus.Push(service, 680, 0x80); bus.Push(service, 700, 0);
                Assert(bus.Qam == 1, "resume-held input acted before neutral");
                bus.Push(service, 800, 0x80); bus.Push(service, 880, 0x80); bus.Push(service, 900, 0);
                Assert(bus.Qam == 2, "new gesture did not rearm after resume");
            }
        }));
        Test("Shared menu assignment preserves stored Game Bar and live routes on either conflict or save failure", () => WithRecordingDirectory(folder => {
            using (var bus = new MenuReportBus()) using (var service = bus.Create(folder)) {
                service.Configure(LegionShortcutMode.Right);
                string saved = File.ReadAllText(Path.Combine(folder, "legion-shortcut.json"));
                service.ConfigureMenuShortcuts(LegionShortcutMode.Left, LegionShortcutMode.Both, null);
                bool conflict = false;
                try { service.Configure(LegionShortcutMode.Left); } catch (InvalidOperationException) { conflict = true; }
                Assert(conflict && service.GetStatus().Mode == LegionShortcutMode.Right && File.ReadAllText(Path.Combine(folder, "legion-shortcut.json")) == saved, "Game Bar changed before rejecting conflict");
                int writes = 0; conflict = false;
                try { service.ConfigureMenuShortcuts(LegionShortcutMode.Right, 0, () => writes++); } catch (InvalidOperationException) { conflict = true; }
                Assert(conflict && writes == 0 && service.QamShortcut == LegionShortcutMode.Left, "menu conflict persisted");
                bool failed = false;
                try { service.ConfigureMenuShortcuts(LegionShortcutMode.Both, LegionShortcutMode.Left, () => { throw new IOException("synthetic durable failure"); }); }
                catch (IOException) { failed = true; }
                Assert(failed && service.QamShortcut == LegionShortcutMode.Left && service.DesktopShortcut == LegionShortcutMode.Both, "failed durable save changed route");
            }
        }));
    }
    private sealed class MenuReportBus : IDisposable
    {
        private readonly Queue<Tuple<long, byte>> queue = new Queue<Tuple<long, byte>>();
        private readonly AutoResetEvent available = new AutoResetEvent(false);
        private long time;
        internal int Qam, Desktop, GameBar, Active, MaximumActive;
        internal LegionShortcutService Create(string directory) => new LegionShortcutService(directory, () => Interlocked.Read(ref time), diagnostic => {
            int active = Interlocked.Increment(ref Active); MaximumActive = Math.Max(MaximumActive, active); return new Reader(this);
        }, () => GameBar++);
        internal void Push(LegionShortcutService service, long time, byte buttons)
        {
            long before = service.GetStatus().Reports;
            lock (queue) queue.Enqueue(Tuple.Create(time, buttons)); available.Set();
            Assert(SpinWait.SpinUntil(() => service.GetStatus().Reports > before, 3000), "synthetic menu report not processed");
        }
        public void Dispose() { available.Dispose(); }
        private sealed class Reader : ILegionButtonReader
        {
            private readonly MenuReportBus bus;
            internal Reader(MenuReportBus bus) { this.bus = bus; }
            public byte[] Read(WaitHandle stop, WaitHandle changed)
            {
                while (true) {
                    if (stop.WaitOne(0) || changed.WaitOne(0)) throw new OperationCanceledException();
                    lock (bus.queue) if (bus.queue.Count != 0) {
                        var next = bus.queue.Dequeue(); Interlocked.Exchange(ref bus.time, next.Item1); return LegionReport(false, next.Item2);
                    }
                    if (WaitHandle.WaitAny(new[] { stop, changed, bus.available }, 100) < 2) throw new OperationCanceledException();
                }
            }
            public void Dispose() { Interlocked.Decrement(ref bus.Active); }
        }
    }
}
