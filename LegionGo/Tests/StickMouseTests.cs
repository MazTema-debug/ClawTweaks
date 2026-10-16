// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using LegionTweaks;

internal static partial class Program
{
    private static void StickMouseTests()
    {
        Test("Stick mouse defaults to off and preserves every controller input", () => {
            var mouse = new StickMouse(); var input = MouseFrame(32767, -32768);
            var step = mouse.Process(input, 0, true, 1);
            Assert(!mouse.Enabled && !mouse.Active && !mouse.WaitingForNeutral && step.DeltaX == 0 && step.DeltaY == 0
                && SameMouseFrame(step.ControllerInput, input), "default mode consumed input or moved the cursor");
        });
        Test("Stick mouse requires a neutral right stick after explicit activation", () => {
            var mouse = new StickMouse(); mouse.Configure(true, 8, 15);
            foreach (long now in new long[] { 0, 16, 32 })
            {
                var step = mouse.Process(MouseFrame(32767, 0), now, true, 1);
                Assert(mouse.WaitingForNeutral && step.DeltaX == 0 && step.DeltaY == 0 && step.ControllerInput.RX == 0,
                    "held activation moved the cursor or reached the game");
            }
            mouse.Process(MouseFrame(0, 0), 48, true, 1);
            var moving = mouse.Process(MouseFrame(32767, 0), 64, true, 1);
            Assert(mouse.Active && moving.DeltaX > 0, "neutral observation did not arm movement");
        });
        Test("Stick mouse consumes only the physical right axes before profile stick swapping", () => {
            var mouse = ArmedMouse(); var input = MouseFrame(20000, -20000);
            var consumed = mouse.Process(input, 16, true, 1).ControllerInput;
            Assert(consumed.RX == 0 && consumed.RY == 0 && consumed.Buttons == input.Buttons && consumed.LT == input.LT
                && consumed.RT == input.RT && consumed.LX == input.LX && consumed.LY == input.LY, "mouse modified unrelated input");
            var output = new ControllerLayout { SwapSticks = true, LeftDeadzone = 0, RightDeadzone = 0 }.Transform(consumed);
            Assert(output.LX == 0 && output.LY == 0 && output.RX != 0, "consumed physical stick returned after the layout swap");
        });
        Test("Stick mouse velocity is based on elapsed time rather than controller poll count", () => {
            foreach (int interval in new[] { 4, 10, 20, 50 })
            {
                var mouse = ArmedMouse(); int distance = 0;
                for (int now = interval; now <= 1000; now += interval) distance += mouse.Process(MouseFrame(32767, 0), now, true, 1).DeltaX;
                Assert(Math.Abs(distance - 800) <= 1, "pointer speed changed with polling interval " + interval);
            }
        });
        Test("Stick mouse maps up to negative Windows Y and bounds diagonal speed", () => {
            foreach (short y in new short[] { short.MaxValue, short.MinValue })
            {
                var mouse = ArmedMouse(); var step = mouse.Process(MouseFrame(0, y), 100, true, 1);
                Assert(step.DeltaX == 0 && Math.Sign(step.DeltaY) == -Math.Sign(y) && Math.Abs(step.DeltaY) <= 80,
                    "Y direction or asymmetric short normalization is wrong");
            }
            var diagonal = ArmedMouse().Process(MouseFrame(short.MaxValue, short.MaxValue), 100, true, 1);
            Assert(Math.Sqrt(diagonal.DeltaX * diagonal.DeltaX + diagonal.DeltaY * diagonal.DeltaY) <= 81,
                "diagonal motion exceeded the configured radial speed");
        });
        Test("Stick mouse deadzone removes drift and resets fractional movement at center", () => {
            var mouse = ArmedMouse();
            for (int now = 16; now <= 160; now += 16)
            {
                var step = mouse.Process(MouseFrame(1000, -1000), now, true, 1);
                Assert(step.DeltaX == 0 && step.DeltaY == 0, "drift inside the radial deadzone moved the cursor");
            }
            var small = mouse.Process(MouseFrame(6000, 0), 176, true, 1);
            mouse.Process(MouseFrame(0, 0), 192, true, 1);
            var fresh = mouse.Process(MouseFrame(6000, 0), 208, true, 1);
            Assert(small.DeltaX == fresh.DeltaX, "old fractional movement survived recentering");
        });
        Test("Stick mouse accumulates subpixel motion without losing slow movement", () => {
            var mouse = ArmedMouse(1, 5); int distance = 0;
            for (int now = 4; now <= 1000; now += 4) distance += mouse.Process(MouseFrame(12000, 0), now, true, 1).DeltaX;
            Assert(distance > 0 && distance < 100, "fractional motion vanished or exceeded the maximum speed");
        });
        Test("Stick mouse settings reject invalid values without changing the active mode", () => {
            foreach (var values in new[] { new[] { 0, 15 }, new[] { 21, 15 }, new[] { 8, 4 }, new[] { 8, 31 } })
            {
                var mouse = ArmedMouse(); MustFail(() => mouse.Configure(false, values[0], values[1]));
                Assert(mouse.Enabled && mouse.Active && mouse.Sensitivity == 8 && mouse.Deadzone == 15,
                    "invalid settings changed an existing mode");
            }
        });
        Test("Stick mouse changing settings or reenabling always requires neutral again", () => {
            var mouse = ArmedMouse(); mouse.Configure(true, 20, 30);
            var step = mouse.Process(MouseFrame(32767, 0), 16, true, 1);
            Assert(mouse.WaitingForNeutral && step.DeltaX == 0, "settings update retained an armed held stick");
            mouse.Configure(false, 20, 30);
            var original = MouseFrame(20000, 30000);
            Assert(SameMouseFrame(mouse.Process(original, 32, true, 1).ControllerInput, original), "disabled mode still consumed right axes");
            mouse.Configure(true, 20, 30);
            Assert(mouse.WaitingForNeutral && !mouse.Active, "reenabling retained the previous armed state");
        });
        Test("Stick mouse discards gaps stale clocks and duplicate timestamps without cursor jumps", () => {
            foreach (long now in new long[] { -1, 0, 101, 100000 })
            {
                var mouse = ArmedMouse(); var step = mouse.Process(MouseFrame(32767, 0), now, true, 1);
                Assert(step.DeltaX == 0 && step.DeltaY == 0, "a stale or duplicate sample produced movement");
                if (now != 0) Assert(mouse.WaitingForNeutral, "invalid time retained an armed stick");
            }
            var backwards = ArmedMouse(); backwards.Process(MouseFrame(32767, 0), 50, true, 1);
            var rewind = backwards.Process(MouseFrame(32767, 0), 49, true, 1);
            Assert(rewind.DeltaX == 0 && backwards.WaitingForNeutral, "backward clock emitted a delta");
        });
        Test("Stick mouse stops on desktop loss and requires neutral after desktop recovery", () => {
            var mouse = ArmedMouse();
            var blocked = mouse.Process(MouseFrame(32767, 0), 16, false, 1);
            var recovered = mouse.Process(MouseFrame(32767, 0), 32, true, 1);
            Assert(blocked.DeltaX == 0 && recovered.DeltaX == 0 && mouse.WaitingForNeutral
                && blocked.ControllerInput.RX == 0 && recovered.ControllerInput.RX == 0,
                "secure desktop or recovery emitted movement or game right-stick input");
            mouse.Process(MouseFrame(0, 0), 48, true, 1);
            Assert(mouse.Process(MouseFrame(32767, 0), 64, true, 1).DeltaX > 0, "desktop recovery never rearmed after neutral");
        });
        Test("Stick mouse requires neutral after changing foreground or observing no foreground", () => {
            foreach (long foreground in new long[] { 0, 2 })
            {
                var mouse = ArmedMouse(); var step = mouse.Process(MouseFrame(32767, 0), 16, true, foreground);
                Assert(step.DeltaX == 0 && mouse.WaitingForNeutral, "foreground transition retained motion");
                Assert(mouse.Process(MouseFrame(32767, 0), 32, true, 1).DeltaX == 0, "held stick rearmed after focus returned");
            }
        });
        Test("Stick mouse never inspects Windows or sends input while disabled", () => {
            long now = 0; var output = new MouseOutputFake(); var service = new StickMouseService(output, () => now);
            service.SetAvailable(true);
            foreach (long value in new long[] { 0, 16, 1000 }) { now = value; service.Process(MouseFrame(32767, 0)); }
            Assert(output.Inspections == 0 && output.Attempts == 0 && !service.GetStatus().Enabled && service.GetStatus().Available,
                "disabled mouse performed native polling or enabled itself");
        });
        Test("Stick mouse service requires virtual availability and stores only explicit settings", () => {
            long now = 0; var output = new MouseOutputFake(); var service = new StickMouseService(output, () => now);
            MustFail(() => service.Configure(true, 8, 15));
            service.Configure(false, 12, 20);
            var status = service.GetStatus();
            Assert(status.Supports && !status.Available && !status.Enabled && status.Sensitivity == 12 && status.Deadzone == 20
                && output.Inspections == 0, "offline configuration opened input or incorrectly activated the mouse");
        });
        Test("Stick mouse service limits native work while consuming every virtual right-stick frame", () => {
            long now = 0; var output = new MouseOutputFake(); var service = new StickMouseService(output, () => now);
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
            foreach (long value in new long[] { 4, 8, 12, 16 })
            {
                now = value; var input = service.Process(MouseFrame(32767, 0));
                Assert(input.RX == 0 && input.RY == 0, "unsampled right-stick input reached the virtual gamepad");
            }
            Assert(output.Inspections == 2 && output.Attempts == 1 && output.Moves.Count == 1 && service.GetStatus().Active,
                "mouse output followed the controller's faster polling or never armed");
        });
        Test("Stick mouse service does not reactivate after virtual stop reconnection or explicit disable", () => {
            long now = 0; var output = new MouseOutputFake(); var service = new StickMouseService(output, () => now);
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
            service.SetAvailable(false); service.SetAvailable(true); now = 16;
            var original = MouseFrame(32767, 0);
            Assert(!service.GetStatus().Enabled && SameMouseFrame(service.Process(original), original) && output.Attempts == 0,
                "reconnection implicitly enabled mouse output");
            service.Configure(true, 8, 15); service.Disable();
            Assert(service.GetStatus().Available && !service.GetStatus().Enabled, "explicit mouse disable stopped virtual availability or retained output");
        });
        Test("Stick mouse output failure disables mouse and preserves the virtual input path", () => {
            long now = 0; var output = new MouseOutputFake { ThrowOnMove = true }; var service = new StickMouseService(output, () => now);
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0)); now = 16;
            var original = MouseFrame(32767, 0); var failed = service.Process(original); var status = service.GetStatus();
            Assert(failed.RX == 0 && !status.Enabled && !status.Active && status.Available && status.Error.Contains("envío rechazado"),
                "failed send retained mouse output or stopped the virtual session");
            now = 32;
            Assert(SameMouseFrame(service.Process(original), original) && output.Attempts == 1, "send failure was silently retried or held game input neutral");
        });
        Test("Stick mouse inspection failure disables only mouse without throwing into the controller worker", () => {
            long now = 0; var output = new MouseOutputFake { ThrowOnInspect = true }; var service = new StickMouseService(output, () => now);
            service.SetAvailable(true); service.Configure(true, 8, 15);
            var result = service.Process(MouseFrame(32767, 0)); var status = service.GetStatus();
            Assert(result.RX == 0 && !status.Enabled && status.Available && status.Error.Contains("escritorio no disponible") && output.Attempts == 0,
                "native inspection failure escaped or retained enabled mouse");
        });
        Test("Stick mouse send-time focus race discards motion and waits for a new neutral sample", () => {
            long now = 0; var output = new MouseOutputFake { AcceptMove = false }; var service = new StickMouseService(output, () => now);
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0)); now = 16;
            service.Process(MouseFrame(32767, 0));
            Assert(service.GetStatus().WaitingForNeutral && output.Moves.Count == 0, "focus race moved the cursor or retained armed motion");
            output.AcceptMove = true; now = 32; service.Process(MouseFrame(32767, 0));
            Assert(output.Attempts == 1, "held stick immediately retried a rejected foreground");
            now = 48; service.Process(MouseFrame(0, 0)); now = 64; service.Process(MouseFrame(32767, 0));
            Assert(output.Moves.Count == 1, "focus-race recovery did not rearm after neutral");
        });
        Test("Stick mouse service pauses through locked desktop and drops accumulated time", () => {
            long now = 0; var output = new MouseOutputFake(); var service = new StickMouseService(output, () => now);
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
            now = 16; output.Desktop = new MouseDesktop(); service.Process(MouseFrame(32767, 0));
            now = 10000; output.Desktop = new MouseDesktop { Available = true, Foreground = 1 }; service.Process(MouseFrame(32767, 0));
            Assert(output.Attempts == 0 && service.GetStatus().WaitingForNeutral && service.GetStatus().Enabled,
                "locked-desktop recovery emitted held movement or reused accumulated time");
        });
        Test("Stick mouse service discards an old source frame after a slow or backward desktop inspection", () => {
            foreach (bool backwards in new[] { false, true })
            {
                long now = 0; var output = new MouseOutputFake(); var service = new StickMouseService(output, () => now);
                service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
                now = 16; output.AfterInspect = () => now += backwards ? -1 : 101;
                var consumed = service.Process(MouseFrame(32767, 0));
                Assert(output.Attempts == 0 && consumed.RX == 0 && service.GetStatus().WaitingForNeutral,
                    "a slow or backward inspection emitted the old source frame");
                output.AfterInspect = null; now += 16; service.Process(MouseFrame(32767, 0));
                Assert(output.Attempts == 0 && service.GetStatus().WaitingForNeutral, "held stick immediately rearmed after stale inspection");
                now += 16; service.Process(MouseFrame(0, 0)); now += 16; service.Process(MouseFrame(32767, 0));
                Assert(output.Moves.Count == 1 && service.GetStatus().Active, "mouse did not recover after a fresh neutral sample");
            }
        });
        Test("Stick mouse runtime status and settings survive IPC without implying activation", () => {
            var service = new StickMouseService(new MouseOutputFake(), () => 0);
            service.Configure(false, 20, 30);
            var copy = Protocol.Decode<StickMouseStatus>(Protocol.Encode(service.GetStatus()));
            Assert(copy.Supports && !copy.Enabled && !copy.Active && !copy.Available && copy.Sensitivity == 20 && copy.Deadzone == 30
                && !string.IsNullOrEmpty(copy.Message), "IPC lost capability settings or misrepresented off state");
        });
        Test("Stick mouse INPUT uses Windows x64 mouse offsets and movement flags only", () => {
            Assert(IntPtr.Size == 8 && Marshal.SizeOf(typeof(MouseOutput.NativeInput)) == 40
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "Type").ToInt32() == 0
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "X").ToInt32() == 8
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "Y").ToInt32() == 12
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "MouseData").ToInt32() == 16
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "Flags").ToInt32() == 20
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "Time").ToInt32() == 24
                && Marshal.OffsetOf(typeof(MouseOutput.NativeInput), "Extra").ToInt32() == 32, "Windows x64 INPUT/MOUSEINPUT ABI is wrong");
            var move = MouseOutput.CreateMove(-17, 39);
            Assert(move.Type == 0 && move.X == -17 && move.Y == 39 && move.Flags == 1 && move.MouseData == 0
                && move.Time == 0 && move.Extra == UIntPtr.Zero, "cursor output added buttons wheel or absolute movement");
            var imports = typeof(MouseOutput).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(x => x.GetCustomAttribute<DllImportAttribute>() != null).ToArray();
            Assert(imports.Length == 5 && imports.All(x => x.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?.Paths == DllImportSearchPath.System32),
                "native mouse imports did not restrict lookup to System32");
        });
    }

    private static StickMouse ArmedMouse(int sensitivity = 8, int deadzone = 15)
    {
        var mouse = new StickMouse(); mouse.Configure(true, sensitivity, deadzone);
        mouse.Process(MouseFrame(0, 0), 0, true, 1); return mouse;
    }
    private static GamepadFrame MouseFrame(short x, short y) => new GamepadFrame { RX = x, RY = y, LX = 12345, LY = -23456, LT = 17, RT = 231, Buttons = 4096 };
    private static bool SameMouseFrame(GamepadFrame a, GamepadFrame b) => a.Buttons == b.Buttons && a.LT == b.LT && a.RT == b.RT
        && a.LX == b.LX && a.LY == b.LY && a.RX == b.RX && a.RY == b.RY;
    private sealed class MouseOutputFake : IMouseOutput
    {
        internal MouseDesktop Desktop = new MouseDesktop { Available = true, Foreground = 1 };
        internal bool AcceptMove = true, ThrowOnMove, ThrowOnInspect;
        internal int Inspections, Attempts;
        internal Action AfterInspect;
        internal readonly List<Tuple<int, int>> Moves = new List<Tuple<int, int>>();
        public MouseDesktop Inspect() { Inspections++; if (ThrowOnInspect) throw new InvalidOperationException("escritorio no disponible"); AfterInspect?.Invoke(); return Desktop; }
        public bool Move(int x, int y, MouseDesktop expected)
        {
            Attempts++; if (ThrowOnMove) throw new InvalidOperationException("envío rechazado");
            if (AcceptMove) Moves.Add(Tuple.Create(x, y)); return AcceptMove;
        }
    }
}
