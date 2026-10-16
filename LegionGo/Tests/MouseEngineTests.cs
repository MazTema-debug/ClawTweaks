// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void MouseEngineTests()
    {
        Test("Mouse commands require valid explicit values and never apply power", () => {
            var h = new Fake(); var j = new Journal(); var c = new MouseEngineController(); var engine = new ControlEngine(h, j, c);
            Assert(engine.Execute(new Request { Operation = "mouse_set", Values = new[] { 1, 8, 15 } }).Success && c.Sets == 1 && c.Enabled, "explicit mouse request failed");
            Assert(h.Writes.Count == 0 && j.Saves == 0 && c.Applies == 0, "mouse command changed power or layout");
            foreach (var values in new[] { null, new int[0], new[] { 1, 8 }, new[] { 2, 8, 15 }, new[] { 1, 0, 15 }, new[] { 1, 21, 15 }, new[] { 1, 8, 4 }, new[] { 1, 8, 31 } })
                Assert(!engine.Execute(new Request { Operation = "mouse_set", Values = values }).Success && c.Sets == 1, "invalid mouse request changed mode");
        });
        Test("Mouse commands remain model gated and status never enables the mouse", () => {
            var h = new Fake(); var c = new MouseEngineController(); var engine = new ControlEngine(h, new Journal(), c);
            engine.Execute(new Request { Operation = "status" });
            Assert(c.Sets == 0, "poll enabled mouse");
            h.Identity.Cpu = "Other";
            Assert(!engine.Execute(new Request { Operation = "mouse_set", Values = new[] { 1, 8, 15 } }).Success && c.Sets == 0, "unsupported device enabled mouse");
            h.Identity.Cpu = "AMD Ryzen Z1 Extreme";
            Assert(!engine.Execute(new Request { Version = 999, Operation = "mouse_set", Values = new[] { 1, 8, 15 } }).Success && c.Sets == 0, "wrong protocol enabled mouse");
        });
        Test("Diagnostic lookup is read only and missing providers do not fabricate success", () => {
            var h = new Fake(); var j = new Journal(); int calls = 0;
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "legion-diagnostic-test-" + Guid.NewGuid().ToString("N"));
            using (var session = new HelperSession(new ControlEngine(h, j), directory, false, diagnostics: () => { calls++; return new DiagnosticReport(); }))
            {
                Assert(session.Execute(new Request { Operation = "diagnostics" }).Diagnostics != null && calls == 1, "diagnosis not returned");
                Assert(!session.Execute(new Request { Version = 999, Operation = "diagnostics" }).Success && calls == 1, "invalid protocol queried diagnostic provider");
            }
            using (var session = new HelperSession(new ControlEngine(h, j), directory, false))
                Assert(!session.Execute(new Request { Operation = "diagnostics" }).Success, "missing provider fabricated diagnosis");
            Assert(h.Reads.Count == 0 && h.Writes.Count == 0 && j.Saves == 0, "diagnostic polled or changed hardware");
        });
    }
    private sealed class MouseEngineController : IControllerService, IStickMouseControllerService
    {
        internal int Sets, Applies; internal bool Enabled;
        public ControllerLayout Layout => new ControllerLayout();
        public void Preflight(ControllerLayout next) { }
        public void Apply(ControllerLayout next) { Applies++; }
        public ControllerStatus GetStatus() => new ControllerStatus();
        public void SetMouse(bool enabled, int sensitivity, int deadzone) { Sets++; Enabled = enabled; }
        public StickMouseStatus GetMouseStatus() => new StickMouseStatus { Enabled = Enabled };
        public void Dispose() { }
    }
}
