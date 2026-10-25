// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void ControllerSourceReadinessTests()
    {
        Test("Recovery keeps its source open while required HID input is not yet fresh", () => {
            var port = new SourceReadinessFake();
            for (int i = 0; i < 10; i++) Assert(!port.TryOpen(), "missing companion reported ready");
            Assert(port.Opens == 1 && port.Enables == 1 && port.Closes == 0, "polling repeatedly invalidated the source or HID reader");
            port.DirectReady = true;
            Assert(port.TryOpen() && port.Opens == 1 && port.Enables == 1, "fresh companion did not complete the same open");
        });
        Test("An ordinary gameplay recovery does not require foreground-only HID input", () => {
            var port = new SourceReadinessFake { DirectRequired = false };
            Assert(port.TryOpen() && port.Opens == 1 && port.Enables == 1 && !port.DirectReady, "ordinary source was coupled to Game Bar-only readiness");
        });
        Test("A source that disconnects again is closed and must reopen before recovery", () => {
            var port = new SourceReadinessFake();
            Assert(!port.TryOpen(), "companion should be pending");
            port.Readable = false; port.DirectReady = true;
            Assert(!port.TryOpen() && port.Closes == 1, "stale source was accepted with a fresh companion");
            port.Present = false;
            Assert(!port.TryOpen() && port.Enables == 1, "failed source open enabled its companion");
            port.Present = port.Readable = true;
            Assert(!port.TryOpen() && port.Enables == 2 && !port.DirectReady, "reopen retained old companion evidence");
            port.DirectReady = true; Assert(port.TryOpen(), "new source and companion did not recover");
        });
        Test("A delayed HID return gets the existing twenty second budget without publishing premature recovery", () => {
            var power = new ControllerPowerState(); var port = new SourceReadinessFake();
            var session = new ControllerResume(power, port, 0);
            session.SourceDisconnected(1);
            for (long now = 1; now <= 3001; now += 250)
                Assert(!session.Step(now) && port.Recoveries == 0 && port.Neutral, "SDL alone published recovery or output");
            port.DirectReady = true;
            Assert(session.Step(3251) && port.Recoveries == 1 && port.Opens == 1 && port.Enables == 1, "late HID did not complete the retained source");
            var layout = new ControllerLayout();
            Assert(!session.AcceptInput(new GamepadFrame { LT = 255 }, layout), "held input bypassed recovery release");
            Assert(session.AcceptInput(new GamepadFrame(), layout)
                && session.AcceptInput(new GamepadFrame { LT = 255 }, layout), "release did not rearm fresh input");
        });
        Test("A companion that never returns cannot extend the recovery deadline", () => {
            var power = new ControllerPowerState(); var port = new SourceReadinessFake();
            var session = new ControllerResume(power, port, 0);
            session.SourceDisconnected(1);
            for (long now = 1; now < 20001; now += 250) Assert(!session.Step(now), "missing companion reported success");
            port.DirectReady = true;
            bool expired = false; try { session.Step(20001); } catch (TimeoutException) { expired = true; }
            Assert(expired && port.Recoveries == 0 && port.Opens == 1 && port.Enables == 1 && port.Neutral,
                "missing HID renewed its budget or resumed output");
        });
        Test("A second pause discards the opened source and its companion evidence", () => {
            var port = new SourceReadinessFake();
            Assert(!port.TryOpen(), "companion should be pending"); port.DirectReady = true;
            port.Pause();
            Assert(!port.TryOpen() && port.Opens == 2 && port.Enables == 2 && !port.DirectReady,
                "new pause reused stale source or HID evidence");
        });
        Test("Manual stop while waiting for HID cannot publish a late recovery", () => {
            bool stopped = false; var power = new ControllerPowerState(); var port = new SourceReadinessFake();
            var session = new ControllerResume(power, port, 0, () => stopped);
            session.SourceDisconnected(1); session.Step(1); stopped = true; port.DirectReady = true;
            Assert(!session.Step(251) && port.Recoveries == 0 && port.Neutral
                && !session.AcceptInput(new GamepadFrame(), new ControllerLayout()), "manual stop revived the companion or output");
        });
    }

    private sealed class SourceReadinessFake : IControllerResumePort
    {
        internal bool Present = true, Readable = true, DirectRequired = true, DirectReady, SourceOpen, Neutral;
        internal int Opens, Enables, Closes, Recoveries;
        private readonly ControllerSourceReadiness readiness;
        internal SourceReadinessFake()
        {
            readiness = new ControllerSourceReadiness(
                () => { Opens++; SourceOpen = Present; return Present; },
                () => SourceOpen && Readable,
                () => { Enables++; DirectReady = false; },
                () => DirectRequired,
                () => DirectReady,
                () => { Closes++; SourceOpen = false; });
        }
        public bool TryOpen() => readiness.TryOpen();
        public void Pause() { readiness.Reset(); SourceOpen = false; Neutral = true; }
        public void Waiting(bool suspended, ControllerRecoveryKind kind) { }
        public void Recovered(ControllerRecoveryKind kind) { Recoveries++; }
    }
}
