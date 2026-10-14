// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.InteropServices;
using LegionTweaks;

internal static partial class Program
{
    private static byte[] LegionReport(bool initialized, byte buttons)
    {
        var report = new byte[64]; report[0] = 4;
        report[1] = initialized ? (byte)0 : (byte)0x3c;
        report[2] = initialized ? (byte)0xa1 : (byte)0x74;
        report[initialized ? 16 : 18] = buttons; return report;
    }
    private static void LegionShortcutTests()
    {
        Test("Legion HID discovery accepts original and recent firmware IDs through both filters", () =>
        {
            foreach (ushort product in new ushort[] { 0x6182, 0x6183, 0x6184, 0x6185, 0x61eb, 0x61ec, 0x61ed, 0x61ee })
                foreach (string path in new[] { @"\\?\hid#vid_17ef&pid_" + product.ToString("x4") + "&mi_01#test",
                    @"\\?\HID#VID_17EF&PID_" + product.ToString("X4") + "&MI_01#test" })
                    Assert(LegionHidReader.IsLenovoPath(path)
                        && LegionHidReader.IsButtonCollection(0x17ef, product, 0xffa0, 1, 64), "compatible firmware excluded");
        });
        Test("Legion HID discovery excludes unrelated devices and non-button collections", () =>
        {
            Assert(!LegionHidReader.IsLenovoPath(null) && !LegionHidReader.IsLenovoPath(@"\\?\hid#vid_045e&pid_028e#test"), "foreign path accepted");
            foreach (ushort product in new ushort[] { 0, 0x6181, 0x6186, 0x61ea, 0x61ef, 0xffff })
                Assert(!LegionHidReader.IsButtonCollection(0x17ef, product, 0xffa0, 1, 64), "unknown controller accepted");
            Assert(!LegionHidReader.IsButtonCollection(0x045e, 0x61eb, 0xffa0, 1, 64), "foreign vendor accepted");
            Assert(!LegionHidReader.IsButtonCollection(0x17ef, 0x61eb, 1, 6, 64), "keyboard collection accepted");
            Assert(!LegionHidReader.IsButtonCollection(0x17ef, 0x61eb, 0xffa0, 2, 64), "wrong usage accepted");
            foreach (ushort length in new ushort[] { 0, 32, 63, 65, 1024 })
                Assert(!LegionHidReader.IsButtonCollection(0x17ef, 0x61eb, 0xffa0, 1, length), "wrong input size accepted");
        });
        Test("HID discovery structures match the Windows x64 ABI", () =>
        {
            Assert(Marshal.SizeOf(typeof(LegionHidReader.DeviceInterface)) == 32, "interface size");
            Assert(Marshal.SizeOf(typeof(LegionHidReader.Attributes)) == 12, "attributes size");
            Assert(Marshal.SizeOf(typeof(LegionHidReader.Caps)) == 64, "caps size");
            Assert(Marshal.OffsetOf(typeof(LegionHidReader.Caps), "InputLength").ToInt32() == 4
                && Marshal.OffsetOf(typeof(LegionHidReader.Caps), "LinkNodes").ToInt32() == 44, "caps layout");
        });
        Test("Legion front buttons decode both vendor formats and ignore ordinary buttons", () =>
        {
            foreach (bool initialized in new[] { false, true })
                foreach (var pair in new[] { Tuple.Create((byte)0x3f, 0), Tuple.Create((byte)0xbf, 1), Tuple.Create((byte)0x7f, 2), Tuple.Create((byte)0xff, 3) })
                {
                    var report = LegionReport(initialized, pair.Item1);
                    report[initialized ? 18 : 16] = (byte)~pair.Item1;
                    Assert(LegionButtonReport.TryDecode(report, out int buttons) && buttons == pair.Item2, "wrong report layout/button mask");
                }
        });
        Test("Legion parser rejects truncated packets, command echoes and status reports", () =>
        {
            foreach (byte[] report in new[] { null, new byte[0], new byte[18], new byte[63], new byte[65], new byte[64] })
                Assert(!LegionButtonReport.TryDecode(report, out int buttons) && buttons == 0, "invalid packet accepted");
            foreach (byte command in new byte[] { 0x02, 0x03, 0xb0, 0x0f })
            {
                var report = LegionReport(true, 0xc0); report[2] = command;
                Assert(!LegionButtonReport.TryDecode(report, out int buttons), "reply triggered a shortcut");
            }
        });
        Test("A held Legion button cannot trigger on enable and repeats only after release", () =>
        {
            var gate = new LegionShortcutGate();
            Assert(!gate.Observe(LegionShortcutMode.Right, 2, 0) && !gate.Observe(LegionShortcutMode.Right, 2, 200), "held at startup");
            gate.Observe(LegionShortcutMode.Right, 0, 250);
            Assert(!gate.Observe(LegionShortcutMode.Right, 2, 300) && !gate.Observe(LegionShortcutMode.Right, 2, 330), "debounce missing");
            Assert(gate.Observe(LegionShortcutMode.Right, 2, 370), "deliberate press ignored");
            Assert(!gate.Observe(LegionShortcutMode.Right, 2, 800), "held button repeated");
            gate.Observe(LegionShortcutMode.Right, 0, 900); gate.Observe(LegionShortcutMode.Right, 2, 950);
            Assert(gate.Observe(LegionShortcutMode.Right, 2, 1020), "second press ignored");
        });
        Test("Legion chord requires both buttons and full release before re-arming", () =>
        {
            var gate = new LegionShortcutGate(); gate.Observe(LegionShortcutMode.Both, 0, 0);
            Assert(!gate.Observe(LegionShortcutMode.Both, 1, 10) && !gate.Observe(LegionShortcutMode.Both, 1, 100), "single fired chord");
            gate.Observe(LegionShortcutMode.Both, 3, 110);
            Assert(gate.Observe(LegionShortcutMode.Both, 3, 180), "both did not fire");
            gate.Observe(LegionShortcutMode.Both, 1, 190); gate.Observe(LegionShortcutMode.Both, 3, 200);
            Assert(!gate.Observe(LegionShortcutMode.Both, 3, 300), "partial release rearmed");
        });
        Test("Legion shortcut ignores disabled mode, short noise, gaps and reconnect-held input", () =>
        {
            var gate = new LegionShortcutGate();
            gate.Observe(LegionShortcutMode.Disabled, 0, 0);
            Assert(!gate.Observe(LegionShortcutMode.Disabled, 3, 200), "disabled fired");
            gate.Observe(LegionShortcutMode.Left, 0, 300); gate.Observe(LegionShortcutMode.Left, 1, 310);
            Assert(!gate.Observe(LegionShortcutMode.Left, 0, 330), "short pulse fired");
            gate.Observe(LegionShortcutMode.Left, 1, 400);
            Assert(!gate.Observe(LegionShortcutMode.Left, 1, 2000), "stale input fired across a gap");
            gate.Observe(LegionShortcutMode.Left, 0, 2100); gate.Observe(LegionShortcutMode.Left, 1, 2110); gate.Reset();
            Assert(!gate.Observe(LegionShortcutMode.Left, 1, 2200), "reconnect inherited press");
        });
        Test("Legion shortcut protocol validates commands and never writes performance settings", () => WithRecordingDirectory(folder =>
        {
            var h = new Fake(); var j = new Journal(); var shortcuts = new FakeShortcuts();
            using (var session = new HelperSession(new ControlEngine(h, j), folder, false, shortcuts: shortcuts))
            {
                var idle = session.Execute(new Request { Operation = "recording_status" });
                Assert(idle.Shortcuts.Mode == LegionShortcutMode.Disabled && shortcuts.Calls == 0, "poll changed shortcuts");
                foreach (int[] bad in new[] { null, new int[0], new[] { -1 }, new[] { 4 }, new[] { 1, 2 } })
                    Assert(!session.Execute(new Request { Operation = "shortcut_set", Values = bad }).Success, "invalid mode accepted");
                Assert(shortcuts.Calls == 0, "invalid request reached service");
                var response = session.Execute(new Request { Operation = "shortcut_set", Values = new[] { 3 } });
                Assert(response.Success && shortcuts.Calls == 1 && h.Writes.Count == 0 && j.Saves == 0, "shortcut touched power");
                var copy = Protocol.Decode<Response>(Protocol.Encode(response));
                Assert(copy.Shortcuts.Mode == LegionShortcutMode.Both, "mode lost over IPC");
                Assert(copy.Shortcuts.Diagnostic == "PID 61EB · uso FFA0:0001 · entrada 64 B", "discovery diagnostic lost over IPC");
                Assert(!session.Execute(new Request { Version = 999, Operation = "shortcut_set", Values = new[] { 1 } }).Success && shortcuts.Calls == 1, "version check bypass");
            }
        }));
        Test("Missing shortcut service and save errors are reported without success", () => WithRecordingDirectory(folder =>
        {
            var request = new Request { Operation = "shortcut_set", Values = new[] { 1 } };
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
                Assert(!session.Execute(request).Success, "missing service accepted");
            var shortcuts = new FakeShortcuts { Fail = true };
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false, shortcuts: shortcuts))
            {
                var response = session.Execute(request);
                Assert(!response.Success && response.Shortcuts.Mode == LegionShortcutMode.Disabled, "failed save was reported applied");
            }
        }));
        Test("Game Bar keyboard INPUT uses the Windows x64 ABI", () =>
        {
            Assert(Marshal.SizeOf(typeof(GameBarShortcut.Input)) == 40, "INPUT size");
            Assert(Marshal.OffsetOf(typeof(GameBarShortcut.Input), "Key").ToInt32() == 8
                && Marshal.OffsetOf(typeof(GameBarShortcut.Input), "Extra").ToInt32() == 24, "keyboard union alignment");
        });
    }
    private sealed class FakeShortcuts : ILegionShortcuts
    {
        internal int Calls; internal bool Fail; private LegionShortcutMode mode;
        public LegionShortcutStatus GetStatus() => new LegionShortcutStatus { Mode = mode, Diagnostic = "PID 61EB · uso FFA0:0001 · entrada 64 B" };
        public void Configure(LegionShortcutMode value) { if (Fail) throw new System.IO.IOException("save failed"); Calls++; mode = value; }
    }
}
