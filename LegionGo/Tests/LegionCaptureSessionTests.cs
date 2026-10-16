// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void LegionCaptureSessionTests()
    {
        Test("Capture requests validate values and protocol before checking applications", () => WithRecordingDirectory(folder => {
            var h = new Fake(); var j = new Journal(); var buttons = new CaptureShortcuts(); int preflights = 0;
            using (var session = new HelperSession(new ControlEngine(h, j), folder, false, shortcuts: buttons,
                capturePreflight: () => preflights++))
            {
                foreach (int[] values in new[] { null, new int[0], new[] { 1 }, new[] { -1, 30 }, new[] { 7, 30 },
                    new[] { 1, 4 }, new[] { 1, 31 }, new[] { 1, 30, 1 } })
                    Assert(!session.Execute(new Request { Operation = "legion_capture_start", Values = values }).Success, "malformed capture started");
                Assert(!session.Execute(new Request { Version = 999, Operation = "legion_capture_start", Values = new[] { 2, 30 } }).Success, "version mismatch allowed");
                Assert(preflights == 0 && buttons.Starts == 0 && h.Writes.Count == 0 && j.Saves == 0, "validation caused side effects");
                var response = session.Execute(new Request { Operation = "legion_capture_start", Values = new[] { 6, 30 } });
                Assert(response.Success && preflights == 1 && buttons.Starts == 1 && buttons.Index == 6 && buttons.Seconds == 30, response.Message);
                Assert(h.Writes.Count == 0 && j.Saves == 0, "capture changed performance");
            }
        }));
        Test("Capture refuses games or player-one preparation but cancellation remains available", () => WithRecordingDirectory(folder => {
            var h = new Fake(); var j = new Journal(); var buttons = new CaptureShortcuts(); var c = new EnginePreparationController { Preparing = true };
            int preflights = 0;
            using (var session = new HelperSession(new ControlEngine(h, j, c), folder, false, c, buttons,
                capturePreflight: () => { preflights++; throw new InvalidOperationException("Cierra el juego"); }))
            {
                var request = new Request { Operation = "legion_capture_start", Values = new[] { 2, 30 } };
                Assert(!session.Execute(request).Success && preflights == 0, "preparation allowed capture");
                c.Preparing = false;
                Assert(!session.Execute(request).Success && preflights == 1 && buttons.Starts == 0, "open-game rejection ignored");
                c.Preparing = true;
                Assert(session.Execute(new Request { Operation = "legion_capture_cancel" }).Success && buttons.Cancels == 1, "cancel blocked");
                Assert(c.Applies == 0 && c.PrepareStarts == 0 && h.Writes.Count == 0 && j.Saves == 0, "capture modified controller or power");
            }
        }));
        Test("Capture requires a supporting service instead of silently accepting old helpers", () => WithRecordingDirectory(folder => {
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false, shortcuts: new FakeShortcuts()))
            {
                Assert(!session.Execute(new Request { Operation = "legion_capture_start", Values = new[] { 2, 30 } }).Success, "old helper accepted capture");
                Assert(!session.Execute(new Request { Operation = "legion_capture_cancel" }).Success, "old helper accepted cancel");
            }
        }));
    }
    private sealed class CaptureShortcuts : ILegionShortcuts, ILegionButtonCapture
    {
        internal int Starts, Cancels, Index, Seconds;
        public void StartCapture(int index, int seconds) { Starts++; Index = index; Seconds = seconds; }
        public void CancelCapture() { Cancels++; }
        public void Configure(LegionShortcutMode mode) { }
        public LegionShortcutStatus GetStatus() => new LegionShortcutStatus { SupportsButtonMapping = true };
    }
}
