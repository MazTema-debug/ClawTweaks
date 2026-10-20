// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void ControllerResumeTests()
    {
        Test("Unsolicited and duplicate resume notifications do not reopen input", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake();
            var session = new ControllerResume(power, port, 0);
            Assert(!power.SetSuspended(false) && session.Step(4) && port.Pauses == 0 && port.Opens == 0, "resume created a session");
            power.SetSuspended(true); session.Step(8); power.SetSuspended(false); session.Step(12);
            power.SetSuspended(false); session.Step(16);
            Assert(port.Pauses == 1 && port.Opens == 1 && port.Recoveries == 1, "duplicate resume reopened source");
        });
        Test("Suspend neutralizes output closes source and does no reopen work until wake", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            power.SetSuspended(true);
            Assert(!session.Step(4) && port.Neutral && !port.SourceOpen, "source or output remained active");
            for (long now = 104; now < 3600000; now += 1000) Assert(!session.Step(now), "forwarded suspended input");
            Assert(port.Opens == 0 && port.Pauses == 1, "sleep consumed reconnect budget or spun source");
            power.SetSuspended(false);
            Assert(session.Step(3600000) && port.Opens == 1 && port.Recoveries == 1, "wake did not reopen source");
        });
        Test("A suspend resume pair before the worker runs still replaces the stale reader", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            power.SetSuspended(true); power.SetSuspended(false);
            Assert(session.Step(10) && port.Pauses == 1 && port.Opens == 1 && session.WaitingForNeutral, "coalesced notifications lost");
        });
        Test("Resume source retries are bounded and spaced without exhausting deadline during sleep", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake { Openable = false }; var session = new ControllerResume(power, port, 0);
            power.SetSuspended(true); session.Step(4); power.SetSuspended(false);
            Assert(!session.Step(10000) && port.Opens == 1, "no first attempt");
            session.Step(10100); session.Step(10249);
            Assert(port.Opens == 1, "tight reopen loop");
            session.Step(10250); Assert(port.Opens == 2, "spaced retry absent");
            for (long now = 10500; now < 30000; now += 250) session.Step(now);
            bool timedOut = false; try { session.Step(30000); } catch (TimeoutException) { timedOut = true; }
            Assert(timedOut && port.Opens == 80 && port.Recoveries == 0 && port.Neutral, "unbounded or false recovery");
        });
        Test("A second sleep during recovery gets a fresh awake budget", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake { Openable = false }; var session = new ControllerResume(power, port, 0);
            power.SetSuspended(true); session.Step(1); power.SetSuspended(false); session.Step(2); session.Step(19000);
            power.SetSuspended(true); session.Step(19500); session.Step(90000); power.SetSuspended(false);
            Assert(!session.Step(100000), "second sleep forwarded input");
            port.Openable = true; Assert(session.Step(100250) && port.Recoveries == 1, "old deadline survived new sleep");
        });
        Test("Suspend racing a fresh open discards that reader and never publishes recovery", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            power.SetSuspended(true); session.Step(1); power.SetSuspended(false);
            port.OnOpen = () => power.SetSuspended(true);
            Assert(!session.Step(2) && !port.SourceOpen && port.Neutral && port.Recoveries == 0, "racing source published");
            session.Step(3); port.OnOpen = null; power.SetSuspended(false);
            Assert(session.Step(4) && port.Recoveries == 1, "next wake failed");
        });
        Test("A long sampling gap neutralizes and reopens without assuming a power notification", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            Assert(session.Step(4) && !session.ReadWasInterrupted(8), "normal frame treated as wake");
            Assert(session.ReadWasInterrupted(3000) && session.Step(3000) && port.Recoveries == 1, "missed callback left stale reader");
            Assert(power.Read().Revision == 0, "sampling delay fabricated a Windows event");
        });
        Test("Ordinary disconnected reads without sleep or delay do not trigger reconnect", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            session.Step(4); Assert(!session.ReadWasInterrupted(5) && port.Opens == 0, "ordinary unplug treated as suspend");
        });
        Test("Resume does not replay held buttons triggers sticks or auxiliaries", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            var layout = new ControllerLayout { Enabled = true };
            power.SetSuspended(true); session.Step(1); power.SetSuspended(false); session.Step(2);
            foreach (var held in new[] { new GamepadFrame { Buttons = 4096 }, new GamepadFrame { LT = 255 },
                new GamepadFrame { RT = 100 }, new GamepadFrame { LX = short.MinValue }, new GamepadFrame { RY = 20000 },
                new GamepadFrame { LegionButtons = 4 } })
                Assert(!session.AcceptInput(held, layout) && session.WaitingForNeutral, "held input replayed");
            Assert(session.AcceptInput(new GamepadFrame { RX = 700 }, layout) && !session.WaitingForNeutral, "stick noise blocked neutral");
            Assert(session.AcceptInput(new GamepadFrame { Buttons = 4096, LT = 255 }, layout), "fresh input rejected after neutral");
        });
        Test("Fresh frames sampled across a new power transition cannot be submitted", () => {
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0);
            session.Step(4); power.SetSuspended(true); power.SetSuspended(false);
            Assert(!session.AcceptInput(new GamepadFrame { Buttons = 4096 }, new ControllerLayout()), "cross-transition sample forwarded");
        });
        Test("Manual stop prevents reopening and output even if a wake is queued", () => {
            bool canceled = false;
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0, () => canceled);
            session.Step(4); power.SetSuspended(true); session.Step(8);
            canceled = true;
            power.SetSuspended(false); power.SetSuspended(true); power.SetSuspended(false);
            Assert(!session.Step(12) && !session.AcceptInput(new GamepadFrame(), new ControllerLayout())
                && port.Opens == 0 && port.Recoveries == 0, "notification activated an abandoned session");
        });
        Test("Manual stop during source open discards the reader without publishing input", () => {
            bool canceled = false;
            var power = new ControllerPowerState(); var port = new ResumeFake(); var session = new ControllerResume(power, port, 0, () => canceled);
            power.SetSuspended(true); session.Step(1); power.SetSuspended(false); port.OnOpen = () => canceled = true;
            Assert(!session.Step(2) && !port.SourceOpen && port.Recoveries == 0, "canceled open reported ready");
        });
        Test("A delayed native open cannot report recovery after the awake deadline", () => {
            long actual = 2;
            var power = new ControllerPowerState(); var port = new ResumeFake();
            var session = new ControllerResume(power, port, 0, milliseconds: () => actual);
            power.SetSuspended(true); session.Step(1); power.SetSuspended(false); port.OnOpen = () => actual = 30000;
            bool expired = false; try { session.Step(2); } catch (TimeoutException) { expired = true; }
            Assert(expired && !port.SourceOpen && port.Recoveries == 0, "late open reported success");
        });
        Test("Resume failures propagate for existing physical recovery and never report success", () => {
            foreach (string error in new[] { "different identity", "lost player one", "watchdog lost", "target unavailable" })
            {
                var power = new ControllerPowerState(); var port = new ResumeFake { Failure = error }; var session = new ControllerResume(power, port, 0);
                power.SetSuspended(true); session.Step(1); power.SetSuspended(false);
                string actual = null; try { session.Step(2); } catch (InvalidOperationException ex) { actual = ex.Message; }
                Assert(actual == error && port.Recoveries == 0 && port.Neutral, "failure hidden or announced recovery");
            }
        });
        Test("Suspend diagnostics retain only bounded safe event metadata", () => {
            var report = DiagnosticFixture();
            report.Controller = Diagnostics.Controller(new ControllerStatus { Active = true, Suspended = true, RecoveringInput = true,
                WaitingForNeutral = true, ResumeCount = 3, PowerEvent = "resume", PowerEventAtUtc = DateTime.UtcNow.ToString("O") });
            var copy = Diagnostics.SafeCopy(report, "0.1.26.0");
            Assert(copy.Controller.ResumeCount == 3 && copy.Controller.PowerEvent == "resume" && copy.Controller.Suspended, "resume metadata lost");
            report.Controller.PowerEvent = "C:\\private"; report.Controller.PowerEventAtUtc = "private"; report.Controller.ResumeCount = -1;
            copy = Diagnostics.SafeCopy(report, "0.1.26.0");
            Assert(copy.Controller.PowerEvent == null && copy.Controller.PowerEventAtUtc == null && copy.Controller.ResumeCount == 0, "unsafe resume metadata exported");
        });
    }
    private sealed class ResumeFake : IControllerResumePort
    {
        internal int Pauses, Opens, Recoveries;
        internal bool Neutral, SourceOpen = true, Openable = true;
        internal Action OnOpen;
        internal string Failure;
        public void Pause() { Pauses++; Neutral = true; SourceOpen = false; }
        public bool TryOpen() { Opens++; OnOpen?.Invoke(); if (Failure != null) throw new InvalidOperationException(Failure); SourceOpen = Openable; return Openable; }
        public void Waiting(bool suspended) { }
        public void Recovered() { Recoveries++; }
    }
}
