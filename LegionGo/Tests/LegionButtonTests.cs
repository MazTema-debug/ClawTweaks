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
    private static byte[] LegionCandidateReport(bool initialized, int buttons = 0, byte extra = 0)
    {
        var report = new byte[64]; report[0] = 4;
        report[1] = initialized ? (byte)0 : (byte)0x3c; report[2] = initialized ? (byte)0xa1 : (byte)0x74;
        int offset = initialized ? 16 : 18;
        report[offset] = (byte)(((buttons & 1) != 0 ? 0x80 : 0) | ((buttons & 2) != 0 ? 0x40 : 0));
        report[offset + 2] = extra; return report;
    }
    private static void LegionButtonThrows<T>(Action body) where T : Exception
    {
        try { body(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void LegionButtonWait(Func<bool> condition, string message)
    { Assert(SpinWait.SpinUntil(condition, 3000), message); }
    private static void WithLegionButtonDirectory(Action<string> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), "LegionButtons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { action(folder); }
        finally
        {
            File.Delete(Path.Combine(folder, "legion-shortcut.json"));
            File.Delete(Path.Combine(folder, "legion-shortcut.json.tmp"));
            Directory.Delete(folder);
        }
    }
    private static void LegionButtonTests()
    {
        Test("Legacy layouts omit special mappings and optional frame input safely", () =>
        {
            var old = new ControllerLayout();
            string encoded = Protocol.Encode(old);
            Assert(!encoded.Contains("LegionMap"), "old/default profile grew an active mapping");
            var decoded = Protocol.Decode<ControllerLayout>(encoded); decoded.Validate();
            Assert(decoded.LegionMap == null && !decoded.UsesLegionButtons, "omitted map did not mean no assignment");
            Assert(decoded.Transform(new GamepadFrame { LegionButtons = 3 }).Buttons == 0, "unassigned Legion buttons forwarded");
            Assert(!Protocol.Encode(new GamepadFrame()).Contains("LegionButtons"), "default frame field not optional");
        });
        Test("Special mapping validation bounds seven slots and refuses invalid assignments", () =>
        {
            foreach (int length in new[] { 0, 1, 2, 6, 8, 100 })
                LegionButtonThrows<ArgumentException>(() => new ControllerLayout { LegionMap = new int[length] }.Validate());
            foreach (int value in new[] { -1, 3, 2048, 65535, int.MaxValue })
                LegionButtonThrows<ArgumentException>(() => new ControllerLayout { LegionMap = new[] { value, 0, 0, 0, 0, 0, 0 } }.Validate());
            for (int i = 0; i < 7; i++)
            {
                var map = new int[7]; map[i] = 3;
                LegionButtonThrows<ArgumentException>(() => new ControllerLayout { LegionMap = map }.Validate());
                map[i] = 4096;
                if ((LegionButtonReport.SupportedControllerButtons & (1 << i)) == 0)
                    LegionButtonThrows<ArgumentException>(() => new ControllerLayout { LegionMap = map }.Validate());
                else new ControllerLayout { LegionMap = map }.Validate();
            }
            foreach (int button in ControllerLayout.Buttons)
                new ControllerLayout { LegionMap = new[] { button, button, 0, 0, 0, 0, 0 } }.Validate();
        });
        Test("Legion mappings combine after the standard map and release without masking ordinary buttons", () =>
        {
            var map = new ControllerLayout { LegionMap = new[] { 4096, 8192, 0, 0, 0, 0, 0 } };
            map.Map[0] = 16384; map.Validate();
            Assert(map.UsesLegionButtons && map.Transform(new GamepadFrame { LegionButtons = 1 }).Buttons == 4096, "special output was mapped a second time");
            Assert(map.Transform(new GamepadFrame { Buttons = 4096, LegionButtons = 3 }).Buttons == (4096 | 8192 | 16384), "ordinary/special combination lost");
            Assert(map.Transform(new GamepadFrame()).Buttons == 0, "special release stuck");
            map.LegionMap[1] = 4096;
            Assert(map.Transform(new GamepadFrame { LegionButtons = 3 }).Buttons == 4096, "many-to-one special mapping failed");
            map.LegionMap[0] = 1; map.LegionMap[1] = 2;
            Assert(map.Transform(new GamepadFrame { LegionButtons = 3 }).Buttons == 0, "special opposed D-pad escaped cancellation");
            Assert(map.Transform(new GamepadFrame { LegionButtons = 0xfc }).Buttons == 0, "unassigned auxiliary input mapped");
        });
        Test("Identified auxiliary buttons decode independently without changing front shortcuts", () =>
        {
            foreach (bool initialized in new[] { false, true })
            {
                var report = LegionCandidateReport(initialized, 0, 0x83);
                for (int i = 3; i < 16; i++) report[i] = 0xff;
                int mapped, front;
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 4, "Y1 did not decode independently");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 0, "Y1 leaked into the Game Bar buttons");
                report = LegionCandidateReport(initialized, 0, 0x40);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 8, "held Y2 did not decode independently");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 0, "Y2 leaked into the Game Bar buttons");
                report = LegionCandidateReport(initialized, 0, 0xc0);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 12, "Y1 and Y2 lost their independent bits");
                report = LegionCandidateReport(initialized, 0, 0x20);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 16, "held Y3 did not decode independently");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 0, "Y3 leaked into the Game Bar buttons");
                report = LegionCandidateReport(initialized, 0, 0x04);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 32, "held M2 did not decode independently");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 0, "M2 leaked into the Game Bar buttons");
                report = LegionCandidateReport(initialized, 0, 0x08);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 64, "held M3 did not decode independently");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 0, "M3 leaked into the Game Bar buttons");
                report = LegionCandidateReport(initialized, 0, 0xe0);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 28, "three Y buttons lost their independent bits");
                report = LegionCandidateReport(initialized, 3, 0x93);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 7, "front and Y1 combination lost");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 3, "front shortcut format changed");
                report = LegionCandidateReport(initialized, 3, 0xff);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 127, "combined front and auxiliary buttons changed");
                Assert(LegionButtonReport.TryDecode(report, out front) && front == 3, "auxiliary combination affected shortcut parsing");
                report = LegionCandidateReport(initialized, 3, 0x13);
                Assert(LegionButtonReport.TryDecodeControllerButtons(report, out mapped) && mapped == 3, "unmapped M1 or Start/Back became auxiliary input");
            }
            int ignored;
            Assert(!LegionButtonReport.TryDecodeControllerButtons(new byte[63], out ignored) && ignored == 0, "short report accepted");
            Assert(!LegionButtonReport.TryDecodeControllerButtons(new byte[64], out ignored) && ignored == 0, "unknown header accepted");
        });
        Test("Auxiliary-only layouts activate their source and combine with standard mapping", () =>
        {
            for (int slot = 2; slot <= 6; slot++)
            {
                byte pressed = (byte)(1 << slot);
                var map = new ControllerLayout { LegionMap = new int[7] };
                map.LegionMap[slot] = 4096; map.Validate(); map.Map[0] = 8192;
                Assert(map.UsesLegionButtons, "auxiliary-only layout would not enable its source");
                Assert(map.Transform(new GamepadFrame { LegionButtons = pressed }).Buttons == 4096, "auxiliary output mapped twice");
                Assert(map.Transform(new GamepadFrame { Buttons = 4096, LegionButtons = pressed }).Buttons == (4096 | 8192), "standard and auxiliary output lost");
                Assert(map.Transform(new GamepadFrame()).Buttons == 0 && map.Transform(new GamepadFrame { LegionButtons = 0x80 }).Buttons == 0, "release or unknown input stuck");
                var copy = map.Copy(); copy.LegionMap[slot] = 16384;
                Assert(map.LegionMap[slot] == 4096 && copy.Transform(new GamepadFrame { LegionButtons = pressed }).Buttons == 16384, "auxiliary map aliased");
                map.LegionMap[0] = 1; map.LegionMap[slot] = 2;
                Assert(map.Transform(new GamepadFrame { LegionButtons = (byte)(pressed | 1) }).Buttons == 0, "opposed auxiliary D-pad directions escaped cancellation");
            }
            var paired = new ControllerLayout { LegionMap = new[] { 0, 0, 4096, 8192, 0, 0, 0 } };
            Assert(paired.Transform(new GamepadFrame { LegionButtons = 12 }).Buttons == 12288, "paired Y1/Y2 output lost");
            Assert(paired.Transform(new GamepadFrame { LegionButtons = 8 }).Buttons == 8192, "releasing Y1 also released Y2");
        });
        Test("Auxiliary cached presses retain neutral TTL and only front Game Bar reservations", () =>
        {
            var gate = new LegionButtonInputGate();
            gate.Observe(4, 0); Assert(gate.Read(0, LegionShortcutMode.Disabled) == 0, "held Y1 armed at startup");
            gate.Observe(0, 10); gate.Observe(7, 20);
            Assert(gate.Read(20, LegionShortcutMode.Both) == 4, "front reservation swallowed auxiliary input");
            Assert(gate.Read(1021, LegionShortcutMode.Disabled) == 0, "stale auxiliary press survived");
            gate.Observe(4, 1030); Assert(gate.Read(1030, LegionShortcutMode.Disabled) == 0, "stale held auxiliary rearmed");
            gate.Observe(0, 1040); gate.Observe(4, 1050);
            Assert(gate.Read(1050, LegionShortcutMode.Right) == 4, "fresh auxiliary press missing");
            gate.Reset(); gate.Observe(4, 1060); Assert(gate.Read(1060, LegionShortcutMode.Disabled) == 0, "reset retained auxiliary press");
            gate.Observe(0, 1070); gate.Observe(127, 1080);
            Assert(gate.Read(1080, LegionShortcutMode.Both) == 124, "auxiliary buttons were reserved for Game Bar");
            gate.Observe(64, 1090); Assert(gate.Read(1090, LegionShortcutMode.Right) == 64, "M3 release state changed");
            LegionButtonThrows<ArgumentOutOfRangeException>(() => gate.Observe(128, 1100));
        });
        Test("Shared auxiliary input neither fires Game Bar nor bypasses capture neutral rearming", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                service.Configure(LegionShortcutMode.Right); service.SetControllerInputEnabled(true);
                LegionButtonWait(() => bus.Active == 1, "shared reader missing");
                Assert(service.GetStatus().SupportedButtons == LegionButtonReport.SupportedControllerButtons, "helper did not advertise its mapping capabilities");
                bus.Push(service, 0, 0); bus.Push(service, 10, 0, 0xec); bus.Push(service, 80, 0, 0xec);
                Assert(service.GetControllerButtons() == 124 && bus.Toggles == 0, "auxiliary press fired a front shortcut");
                bus.Push(service, 90, 3, 0xec); bus.Push(service, 160, 3, 0xec);
                Assert(service.GetControllerButtons() == 125 && bus.Toggles == 1 && bus.OpenCount == 1 && bus.MaximumActive == 1, "reservation or single-reader ownership changed");
                bus.Push(service, 170, 0); service.StartCapture(2, 30);
                bus.Push(service, 1400, 0, 0xec);
                Assert(service.GetControllerButtons() == 0, "capture leaked an auxiliary mapping");
                service.CancelCapture(); bus.Push(service, 1500, 0, 0xec);
                Assert(service.GetControllerButtons() == 0, "capture end inherited held auxiliary input");
                bus.Push(service, 1510, 0); bus.Push(service, 1520, 0, 0x40);
                Assert(service.GetControllerButtons() == 8, "neutral did not rearm auxiliary input");
                bus.Push(service, 1530, 0, 0x20); Assert(service.GetControllerButtons() == 16, "Y3 was not independent of the Y1/Y2 release");
                bus.Push(service, 1540, 0, 0x04); Assert(service.GetControllerButtons() == 32, "M2 output did not replace the released Y3");
                bus.Push(service, 1550, 0, 0x08); Assert(service.GetControllerButtons() == 64, "M3 output did not replace the released M2");
                bus.Push(service, 1560, 0); Assert(service.GetControllerButtons() == 0, "auxiliary release missing");
            }
        }));
        Test("Special mapping round trip copies independently without changing old mappings", () =>
        {
            var original = new ControllerLayout { LegionMap = new[] { 4096, 0, 0, 0, 0, 0, 0 } };
            var copy = original.Copy(); copy.LegionMap[0] = 8192;
            Assert(original.LegionMap[0] == 4096 && copy.Map.SequenceEqual(original.Map), "copy aliased or changed standard map");
        });
        Test("Legion cached input requires neutral after enable reset and disconnect", () =>
        {
            var gate = new LegionButtonInputGate();
            gate.Observe(1, 0); Assert(gate.Read(100, LegionShortcutMode.Disabled) == 0, "held startup accepted");
            gate.Observe(0, 110); gate.Observe(3, 120); Assert(gate.Read(120, LegionShortcutMode.Disabled) == 3, "fresh press missing");
            gate.Observe(0, 130); Assert(gate.Read(130, LegionShortcutMode.Disabled) == 0, "release missing");
            gate.Reset(); gate.Observe(2, 140); Assert(gate.Read(140, LegionShortcutMode.Disabled) == 0, "reset inherited held input");
        });
        Test("Game Bar reservations take precedence for single buttons and the chord", () =>
        {
            var gate = new LegionButtonInputGate(); gate.Observe(0, 0); gate.Observe(3, 10);
            Assert(gate.Read(10, LegionShortcutMode.Left) == 2 && gate.Read(10, LegionShortcutMode.Right) == 1
                && gate.Read(10, LegionShortcutMode.Both) == 0, "reserved button leaked into virtual output");
            LegionButtonThrows<ArgumentOutOfRangeException>(() => gate.Read(10, (LegionShortcutMode)4));
        });
        Test("Legion TTL and backward clocks release and require a new neutral report", () =>
        {
            var gate = new LegionButtonInputGate(); gate.Observe(0, 100); gate.Observe(1, 200);
            Assert(gate.Read(1200, LegionShortcutMode.Disabled) == 1 && gate.Read(1201, LegionShortcutMode.Disabled) == 0, "TTL boundary incorrect");
            gate.Observe(1, 1210); Assert(gate.Read(1210, LegionShortcutMode.Disabled) == 0, "stale held input rearmed");
            gate.Observe(0, 1220); gate.Observe(2, 1230); Assert(gate.Read(1229, LegionShortcutMode.Disabled) == 0, "backward read retained press");
            gate.Observe(2, 1240); Assert(gate.Read(1240, LegionShortcutMode.Disabled) == 0, "backward clock failed to disarm");
            gate.Observe(0, 1250); gate.Observe(1, 1260); gate.Observe(1, 2300);
            Assert(gate.Read(2300, LegionShortcutMode.Disabled) == 0, "report gap failed to disarm");
        });
        Test("Capture bounds duration and labels without naming unconfirmed physical bits", () =>
        {
            foreach (int seconds in new[] { -1, 0, 31, int.MaxValue })
                LegionButtonThrows<ArgumentOutOfRangeException>(() => new LegionButtonCaptureSession(2, seconds, 0));
            foreach (int index in new[] { -1, 7, int.MaxValue })
                LegionButtonThrows<ArgumentOutOfRangeException>(() => new LegionButtonCaptureSession(index, 30, 0));
            Assert(new LegionButtonCaptureSession(6, 30, 0).GetStatus(29999).Active, "ended before 30 seconds");
        });
        Test("Capture stores only masked candidate bits in both known headers", () =>
        {
            foreach (bool initialized in new[] { false, true })
            {
                var capture = new LegionButtonCaptureSession(2, 30, 0);
                var baseline = LegionCandidateReport(initialized, 0, 3);
                for (int i = 3; i < 16; i++) baseline[i] = (byte)(i * 9);
                capture.Observe(baseline, 0); capture.Observe(LegionCandidateReport(initialized, 0, 0x83), 1200);
                capture.Observe(LegionCandidateReport(initialized, 0, 3), 1300);
                var state = capture.GetStatus(1400);
                Assert(state.CandidateOffset == (initialized ? 18 : 20) && state.BaselineBits == 0 && state.CurrentBits == 0
                    && state.ChangedBits == 0x80 && state.PressedBits == 0x80 && state.ReleasedBits == 0x80 && state.Transitions == 2, "candidate mask/layout incorrect");
                string json = Protocol.Encode(state);
                Assert(!json.Contains("report") && !json.Contains("Axis") && !json.Contains("Raw"), "raw report/axis data exposed");
            }
        });
        Test("Capture front buttons mask ordinary controls and warmup cannot count a press", () =>
        {
            var capture = new LegionButtonCaptureSession(0, 30, 0);
            var report = LegionCandidateReport(true, 3); report[16] |= 0x3f;
            capture.Observe(report, 0); capture.Observe(LegionCandidateReport(true, 0), 900);
            capture.Observe(LegionCandidateReport(true, 1), 1100);
            var state = capture.GetStatus(1200);
            Assert(state.BaselineBits == 0 && state.CurrentBits == 0x80 && state.ChangedBits == 0x80 && state.Transitions == 1, "warmup or front mask incorrect");
        });
        Test("Capture rejects unknown packets and cancels instead of merging changing formats", () =>
        {
            var capture = new LegionButtonCaptureSession(2, 30, 0);
            capture.Observe(new byte[64], 0); capture.Observe(new byte[63], 1);
            Assert(capture.GetStatus(2).Samples == 0, "unrecognized packet captured");
            capture.Observe(LegionCandidateReport(true), 10); capture.Observe(LegionCandidateReport(false, 0, 0xfc), 1200);
            var state = capture.GetStatus(1200);
            Assert(!state.Active && state.Cancelled && state.ChangedBits == 0, "mixed formats became false button transitions");
        });
        Test("Capture timeout cancellation and clock rollback freeze results", () =>
        {
            var capture = new LegionButtonCaptureSession(2, 1, 100);
            capture.Observe(LegionCandidateReport(true), 100);
            Assert(capture.Tick(1100) && capture.GetStatus(1100).Completed, "timeout missing");
            capture.Observe(LegionCandidateReport(true, 0, 0xfc), 1200);
            Assert(capture.GetStatus(1200).ChangedBits == 0, "late data changed completed capture");
            capture = new LegionButtonCaptureSession(2, 30, 0); capture.Cancel();
            capture.Observe(LegionCandidateReport(true, 0, 0xfc), 10);
            Assert(capture.GetStatus(10).Cancelled && capture.GetStatus(10).Samples == 0, "cancel did not stop capture");
            capture = new LegionButtonCaptureSession(2, 30, 100); capture.Tick(90);
            Assert(capture.GetStatus(100).Cancelled, "clock rollback left capture active");
        });
        Test("One service shares its only reader across Game Bar virtual mapping and capture", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                service.Configure(LegionShortcutMode.Left);
                LegionButtonWait(() => bus.OpenCount == 1, "shortcut did not open reader");
                service.SetControllerInputEnabled(true);
                bus.Push(service, 0, 0); bus.Push(service, 10, 3); bus.Push(service, 80, 3);
                Assert(bus.OpenCount == 1 && bus.MaximumActive == 1 && service.GetControllerButtons() == 2 && bus.Toggles == 1, "reader duplicated or reservation failed");
                service.StartCapture(2, 30);
                bus.Push(service, 100, 0); bus.Push(service, 1200, 1, 0x80); bus.Push(service, 1300, 1, 0x80);
                Assert(bus.OpenCount == 1 && bus.Toggles == 1 && service.GetControllerButtons() == 0, "capture opened extra reader or triggered actions");
                service.CancelCapture(); bus.Push(service, 1400, 1); bus.Push(service, 1500, 1);
                Assert(bus.Toggles == 1 && service.GetControllerButtons() == 0, "capture resume inherited held press");
                bus.Push(service, 1600, 0); bus.Push(service, 1610, 3); bus.Push(service, 1680, 3);
                Assert(bus.Toggles == 2 && service.GetControllerButtons() == 2, "neutral did not rearm actions");
            }
        }));
        Test("Controller stop cancels capture without disabling an existing Game Bar shortcut", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                service.Configure(LegionShortcutMode.Right); service.SetControllerInputEnabled(true);
                LegionButtonWait(() => bus.Active == 1, "reader missing");
                service.StartCapture(2, 30); service.SetControllerInputEnabled(false);
                Assert(service.GetStatus().Capture.Cancelled && service.GetControllerButtons() == 0, "stop did not cancel/neutralize");
                bus.Push(service, 0, 0); bus.Push(service, 10, 2); bus.Push(service, 80, 2);
                Assert(bus.Toggles == 1 && service.GetStatus().Mode == LegionShortcutMode.Right, "stop changed global shortcut");
            }
        }));
        Test("Preparation acknowledges reader closure rejects capture and composes with suspension", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                service.Configure(LegionShortcutMode.Right); service.SetControllerInputEnabled(true);
                LegionButtonWait(() => bus.Active == 1, "reader missing");
                service.StartCapture(2, 30); service.SetPreparationPaused(true);
                Assert(bus.Active == 0 && service.GetStatus().Capture.Cancelled && service.GetControllerButtons() == 0, "pause returned before reader closed");
                LegionButtonThrows<InvalidOperationException>(() => service.StartCapture(2, 30));
                service.Suspend(true); service.SetPreparationPaused(false);
                Assert(bus.Active == 0 && service.GetControllerButtons() == 0, "preparation resume cleared system suspension");
                service.Suspend(false); LegionButtonWait(() => bus.Active == 1, "resume did not restart shared reader");
                bus.Push(service, 100, 1); Assert(service.GetControllerButtons() == 0, "resume inherited held input");
                bus.Push(service, 110, 0); bus.Push(service, 120, 1);
                Assert(service.GetControllerButtons() == 1, "resume neutral did not rearm");
            }
        }));
        Test("A capture alone opens only on request and timeout closes its reader", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                Assert(bus.OpenCount == 0 && service.GetStatus().Capture == null, "automatic capture started");
                service.StartCapture(2, 1); LegionButtonWait(() => bus.Active == 1, "capture reader missing");
                bus.SetTime(1000); Assert(service.GetStatus().Capture.Completed, "capture did not expire");
                LegionButtonWait(() => bus.Active == 0, "expired capture retained reader");
                Assert(!service.GetStatus().ControllerInputEnabled && service.GetControllerButtons() == 0, "capture enabled virtual input");
            }
        }));
        Test("Preparation waits for an in-flight open and aborts if reader ownership is not released", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                bus.BlockNextOpen(); service.SetControllerInputEnabled(true);
                Assert(bus.OpenEntered.WaitOne(3000), "reader open was not pending");
                try
                {
                    var pause = Task.Run(() => service.SetPreparationPaused(true));
                    LegionButtonWait(() => service.GetStatus().PreparationPaused, "pause not entered");
                    Assert(!pause.IsCompleted, "pause acknowledged while enumeration/open was in flight");
                    Assert(SpinWait.SpinUntil(() => pause.IsCompleted, 3000) && pause.IsFaulted
                        && pause.Exception.InnerException is TimeoutException, "unclosed reader did not abort preparation");
                    Assert(service.GetStatus().PreparationPaused, "timeout silently resumed reader");
                }
                finally { bus.ReleaseOpen.Set(); }
                LegionButtonWait(() => bus.Active == 0, "pending open was not disposed while paused");
            }
        }));
        Test("Read failures disarm cached input and cancellation never enables a stopped virtual source", () => WithLegionButtonDirectory(folder =>
        {
            using (var bus = new FakeLegionButtonBus())
            using (var service = bus.CreateService(folder))
            {
                service.SetControllerInputEnabled(true); LegionButtonWait(() => bus.Active == 1, "reader missing");
                bus.Push(service, 0, 0); bus.Push(service, 10, 1); Assert(service.GetControllerButtons() == 1, "input missing");
                bus.FailRead(); LegionButtonWait(() => service.GetStatus().Error != null, "failure not observed");
                Assert(service.GetControllerButtons() == 0, "read failure left virtual press stuck");
                service.SetControllerInputEnabled(false); service.StartCapture(2, 30); service.CancelCapture();
                Assert(!service.GetStatus().ControllerInputEnabled && service.GetControllerButtons() == 0, "cancel reenabled stopped source");
            }
        }));
    }

    private sealed class FakeLegionButtonBus : IDisposable
    {
        private sealed class Packet { internal long Time; internal byte[] Data; }
        private readonly object gate = new object();
        private readonly Queue<Packet> packets = new Queue<Packet>();
        private readonly AutoResetEvent ready = new AutoResetEvent(false);
        internal readonly ManualResetEvent OpenEntered = new ManualResetEvent(false);
        internal readonly ManualResetEvent ReleaseOpen = new ManualResetEvent(false);
        private long time;
        private int opens, active, maximumActive, toggles, failRead, blockOpen;
        internal int OpenCount => Volatile.Read(ref opens);
        internal int Active => Volatile.Read(ref active);
        internal int MaximumActive => Volatile.Read(ref maximumActive);
        internal int Toggles => Volatile.Read(ref toggles);
        internal void SetTime(long value) => Interlocked.Exchange(ref time, value);
        internal LegionShortcutService CreateService(string folder) => new LegionShortcutService(folder,
            () => Interlocked.Read(ref time), diagnostic =>
            {
                Interlocked.Increment(ref opens); int count = Interlocked.Increment(ref active);
                if (count > Volatile.Read(ref maximumActive)) Interlocked.Exchange(ref maximumActive, count);
                if (Interlocked.Exchange(ref blockOpen, 0) != 0) { OpenEntered.Set(); ReleaseOpen.WaitOne(); }
                return new Reader(this);
            }, () => Interlocked.Increment(ref toggles));
        internal void Push(LegionShortcutService service, long timestamp, int buttons, byte extras = 0)
        {
            long before = service.GetStatus().Reports;
            lock (gate) packets.Enqueue(new Packet { Time = timestamp, Data = LegionCandidateReport(true, buttons, extras) });
            ready.Set(); LegionButtonWait(() => service.GetStatus().Reports > before, "fake report not processed");
        }
        internal void FailRead() { Interlocked.Exchange(ref failRead, 1); ready.Set(); }
        internal void BlockNextOpen() { OpenEntered.Reset(); ReleaseOpen.Reset(); Interlocked.Exchange(ref blockOpen, 1); }
        public void Dispose() { ready.Dispose(); OpenEntered.Dispose(); ReleaseOpen.Dispose(); }
        private sealed class Reader : ILegionButtonReader
        {
            private readonly FakeLegionButtonBus bus;
            internal Reader(FakeLegionButtonBus bus) { this.bus = bus; }
            public byte[] Read(WaitHandle stop, WaitHandle changed)
            {
                while (true)
                {
                    if (stop.WaitOne(0) || changed.WaitOne(0)) throw new OperationCanceledException();
                    if (Interlocked.Exchange(ref bus.failRead, 0) != 0) throw new IOException("simulated reader loss");
                    lock (bus.gate)
                        if (bus.packets.Count != 0)
                        {
                            var packet = bus.packets.Dequeue(); bus.SetTime(packet.Time); return packet.Data;
                        }
                    int wake = WaitHandle.WaitAny(new[] { stop, changed, bus.ready }, 25);
                    if (wake < 2) throw new OperationCanceledException();
                    if (wake == WaitHandle.WaitTimeout) return null;
                }
            }
            public void Dispose() => Interlocked.Decrement(ref bus.active);
        }
    }
}
