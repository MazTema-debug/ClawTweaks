// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LegionTweaks;

internal static partial class Program
{
    private static RemapBinding RemapKey(RemapInput input, int key, RemapBehavior behavior = RemapBehavior.Hold) => new RemapBinding {
        Input = input, Behavior = behavior, Action = new RemapAction { Kind = RemapActionKind.Keyboard, Keys = new[] { key } } };
    private static RemapProfile Remap(params RemapBinding[] bindings) => new RemapProfile { Mode = RemapMode.Fps, Bindings = bindings };
    private static RemapCapabilities RemapCaps(RemapMode mode = RemapMode.Fps) => new RemapCapabilities {
        Mode = mode, Inputs = RemapCatalog.Inputs, ReplacedInputs = RemapCatalog.Inputs,
        Keyboard = true, Mouse = true, VirtualPlayers = 2,
        Functions = new[] { RemapFunction.GameBar, RemapFunction.PauseRemap, RemapFunction.ReturnOriginal } };
    private static RemapInputFrame RemapFrame(long sequence, long time, params RemapInput[] down) => new RemapInputFrame {
        Mode = RemapMode.Fps, Sequence = sequence, CapturedAtMilliseconds = time, Values = down.ToDictionary(v => v, v => 1.0) };
    private static void RemapReject(Action operation)
    { bool rejected = false; try { operation(); } catch (Exception ex) when (ex is ArgumentException || ex is InvalidDataException || ex is InvalidOperationException) { rejected = true; } Assert(rejected, "invalid remap accepted"); }
    private static ControllerRemapEngine RemapStarted(RemapProfile profile)
    {
        var engine = new ControllerRemapEngine(); engine.Activate(profile, RemapCaps(profile.Mode));
        engine.Observe(new RemapInputFrame { Mode = profile.Mode, Sequence = 0, CapturedAtMilliseconds = 0 }, 0, true, 1); return engine;
    }
    private sealed class RemapMemoryStorage : IRemapProfileStorage
    {
        internal byte[] Bytes;
        internal bool FailWrite, FailVerify, Race;
        internal int Writes;
        public byte[] Read() => Bytes == null ? null : (byte[])Bytes.Clone();
        public void WriteAtomic(byte[] bytes, byte[] expected)
        {
            if (FailWrite) throw new IOException("simulated storage failure");
            if (Race) Bytes = new RemapProfiles { Profiles = new[] { new RemapProfile { Name = "Otro editor" } } }.Encode();
            if (!(Bytes == null && expected == null) && (Bytes == null || expected == null || !Bytes.SequenceEqual(expected)))
                throw new InvalidOperationException("concurrent edit");
            Writes++; Bytes = FailVerify ? new RemapProfiles().Encode() : (byte[])bytes.Clone();
        }
    }

    private static void ControllerRemapTests()
    {
        Test("Remap schema round trips all destinations and never persists active state", () => {
            var profile = Remap(RemapKey(RemapInput.Y3, 0x2d),
                new RemapBinding { Input = RemapInput.M3, Action = new RemapAction { Kind = RemapActionKind.MouseButton, MouseButton = RemapMouseButton.Back } },
                new RemapBinding { Input = RemapInput.A, Action = new RemapAction { Kind = RemapActionKind.GamepadAxis, Axis = RemapAxis.RX, Direction = -1, Player = 1 } },
                new RemapBinding { Input = RemapInput.B, Behavior = RemapBehavior.Tap, Action = new RemapAction { Kind = RemapActionKind.Function, Function = RemapFunction.GameBar } });
            var bytes = new RemapProfiles { Profiles = new[] { profile } }.Encode();
            var decoded = RemapProfiles.Decode(bytes);
            Assert(decoded.Profiles[0].Bindings.Length == 4 && decoded.Profiles[0].Bindings[2].Action.Axis == RemapAxis.RX, "destination lost");
            Assert(!System.Text.Encoding.UTF8.GetString(bytes).Contains("Active"), "activation serialized");
        });
        Test("Remap rejects unknown versions duplicate lanes and invalid enums", () => {
            var p = Remap(RemapKey(RemapInput.Y3, 65)); p.Version = 2; RemapReject(p.Validate);
            p.Version = 1; p.Bindings = new[] { RemapKey(RemapInput.Y3, 65), RemapKey(RemapInput.Y3, 66) }; RemapReject(p.Validate);
            p.Bindings = new[] { RemapKey((RemapInput)999, 65) }; RemapReject(p.Validate);
            p.Bindings[0].Input = RemapInput.Y3; p.Bindings[0].Action.Kind = (RemapActionKind)999; RemapReject(p.Validate);
            RemapReject(() => RemapProfiles.Decode(new byte[RemapProfiles.MaximumBytes + 1]));
            RemapReject(() => RemapProfiles.Decode(new byte[] { 0xff }));
            RemapReject(() => RemapProfiles.Decode(System.Text.Encoding.UTF8.GetBytes("{}")));
        });
        Test("Remap validates chords function behavior axis direction and players", () => {
            foreach (int[] keys in new[] { null, new int[0], new[] { 65, 65 }, new[] { 1 }, new[] { 0xc3 }, new[] { 0x88 } })
                RemapReject(() => Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.Keyboard, Keys = keys } }).Validate());
            foreach (int key in new[] { 0xa2, 0x70, 0x87, 0xba, 0xad, 0x2d, 0x1b }) Remap(RemapKey(RemapInput.Y3, key)).Validate();
            var p = Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.Function, Function = RemapFunction.GameBar } });
            RemapReject(p.Validate); p.Bindings[0].Behavior = RemapBehavior.Tap; p.Validate();
            p.Bindings[0].Action = new RemapAction { Kind = RemapActionKind.GamepadAxis, Axis = RemapAxis.LT, Direction = -1, Player = 1 }; RemapReject(p.Validate);
            p.Bindings[0].Action.Direction = 1; p.Bindings[0].Action.Player = 2; RemapReject(p.Validate);
            p.Mode = RemapMode.Split; p.Validate();
        });
        Test("Remap rejects cross destination payload and invalid thresholds", () => {
            var p = Remap(RemapKey(RemapInput.Y3, 65)); p.Bindings[0].Action.Player = 1; RemapReject(p.Validate);
            p.Bindings[0].Action.Player = 0; p.Bindings[0].Threshold = 0; RemapReject(p.Validate);
            p.Bindings[0].Threshold = 50; p.Bindings[0].Amount = 101; RemapReject(p.Validate);
            p = Remap(RemapKey(RemapInput.SensorUp, 65, RemapBehavior.Toggle)); RemapReject(p.Validate);
            p.Bindings[0].Behavior = RemapBehavior.Tap; p.Mode = RemapMode.Gamepad; RemapReject(p.Validate);
        });
        Test("Remap activation requires verified native substitution even for no action", () => {
            var p = Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.None } });
            var caps = RemapCaps(); caps.ReplacedInputs = new RemapInput[0]; var e = new ControllerRemapEngine();
            RemapReject(() => e.Activate(p, caps)); Assert(!e.Active, "unverified route activated");
            caps = RemapCaps(); caps.Keyboard = false; RemapReject(() => e.Activate(Remap(RemapKey(RemapInput.Y3, 65)), caps));
            caps = RemapCaps(); caps.Inputs = new RemapInput[0]; RemapReject(() => e.Activate(p, caps));
        });
        Test("Remap invalid replacement preserves active snapshot", () => {
            var p = Remap(RemapKey(RemapInput.Y3, 65)); var e = RemapStarted(p);
            p.Bindings[0].Action.Keys[0] = 66;
            var copy = e.ActiveProfile; copy.Bindings[0].Action.Keys[0] = 67;
            RemapReject(() => e.Activate(Remap(RemapKey(RemapInput.Y3, 1)), RemapCaps()));
            Assert(e.Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1).Keys.SequenceEqual(new[] { 65 }), "active snapshot mutated");
        });
        Test("Remap requires neutral activation and owns held release state", () => {
            var e = new ControllerRemapEngine(); e.Activate(Remap(RemapKey(RemapInput.Y3, 65)), RemapCaps());
            Assert(e.Observe(RemapFrame(0, 0, RemapInput.Y3), 0, true, 1).Keys.Length == 0 && e.WaitingForNeutral, "held enable emitted");
            e.Observe(RemapFrame(1, 1), 1, true, 1);
            Assert(e.Observe(RemapFrame(2, 2, RemapInput.Y3), 2, true, 1).Keys.SequenceEqual(new[] { 65 }), "press lost");
            Assert(e.Observe(RemapFrame(3, 3, RemapInput.Y3), 3, true, 1).Keys.Length == 1, "hold lost");
            Assert(e.Observe(RemapFrame(4, 4), 4, true, 1).Keys.Length == 0, "release retained");
        });
        Test("Remap shared chords preserve modifiers until the final owner releases", () => {
            var a = RemapKey(RemapInput.Y3, 65); a.Action.Keys = new[] { 65, 0xa2 };
            var b = RemapKey(RemapInput.M3, 66); b.Action.Keys = new[] { 0xa2, 66 };
            var e = RemapStarted(Remap(a, b));
            Assert(e.Observe(RemapFrame(1, 1, RemapInput.Y3, RemapInput.M3), 1, true, 1).Keys.SequenceEqual(new[] { 0xa2, 65, 66 }), "chord order or union");
            Assert(e.Observe(RemapFrame(2, 2, RemapInput.M3), 2, true, 1).Keys.SequenceEqual(new[] { 0xa2, 66 }), "shared modifier released early");
            Assert(e.Observe(RemapFrame(3, 3), 3, true, 1).Keys.Length == 0, "owned modifier retained");
        });
        Test("Remap tap expires without repeat and toggle survives release until next press", () => {
            var e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65, RemapBehavior.Tap), RemapKey(RemapInput.M3, 66, RemapBehavior.Toggle)));
            Assert(e.Observe(RemapFrame(1, 1, RemapInput.Y3, RemapInput.M3), 1, true, 1).Keys.Length == 2, "press missing");
            Assert(e.Observe(RemapFrame(2, 32, RemapInput.Y3), 32, true, 1).Keys.SequenceEqual(new[] { 66 }), "tap repeated or toggle released");
            Assert(e.Observe(RemapFrame(3, 33, RemapInput.M3), 33, true, 1).Keys.Length == 0, "toggle second press failed");
        });
        Test("Remap generic and explicit left modifiers share a single ownership identity", () => {
            var e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 0x11), RemapKey(RemapInput.M3, 0xa2)));
            Assert(e.Observe(RemapFrame(1, 1, RemapInput.Y3, RemapInput.M3), 1, true, 1).Keys.SequenceEqual(new[] { 0xa2 }), "modifier aliases duplicated");
            Assert(e.Observe(RemapFrame(2, 2, RemapInput.M3), 2, true, 1).Keys.Single() == 0xa2, "alias released shared modifier");
            var binding = RemapKey(RemapInput.Y3, 65); binding.Action.Keys = new[] { 0x11, 0xa2 };
            RemapReject(() => Remap(binding).Validate());
        });
        Test("Remap focus freshness clock loss and invalid samples release and rearm", () => {
            for (int reason = 0; reason < 7; reason++) {
                var e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65, RemapBehavior.Toggle)));
                e.Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1);
                var frame = reason == 0 ? null : RemapFrame(2, reason == 1 ? 1 : 2, RemapInput.Y3);
                if (reason == 5) frame.Values[RemapInput.Y3] = double.NaN;
                if (reason == 6) frame.CapturedAtMilliseconds = 100;
                long time = reason == 1 ? 252 : reason == 4 ? 0 : 2, focus = reason == 2 ? 2 : 1;
                Assert(e.Observe(frame, time, reason != 3, focus).Keys.Length == 0 && e.WaitingForNeutral, "interruption retained output");
                Assert(e.Observe(RemapFrame(3, 253, RemapInput.Y3), 253, true, focus).Keys.Length == 0, "held recovery escaped");
                e.Observe(RemapFrame(4, 254), 254, true, focus);
                Assert(e.Observe(RemapFrame(5, 255, RemapInput.Y3), 255, true, focus).Keys.Length == 1, "release did not rearm");
            }
        });
        Test("Remap repeated sequences do not replay effects or renew stale input", () => {
            var binding = new RemapBinding { Input = RemapInput.WheelUp, Behavior = RemapBehavior.Tap, Action = new RemapAction { Kind = RemapActionKind.MouseWheel, Y = 1 } };
            var e = RemapStarted(Remap(binding)); var f = RemapFrame(1, 1, RemapInput.WheelUp);
            Assert(e.Observe(f, 1, true, 1).Wheel == 1 && e.Observe(f, 2, true, 1).Wheel == 0, "duplicate effect");
            f.CapturedAtMilliseconds = 2;
            Assert(e.Observe(f, 2, true, 1).Wheel == 0 && e.WaitingForNeutral, "duplicate renewed capture");
            e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65))); f = RemapFrame(1, 1, RemapInput.Y3);
            e.Observe(f, 1, true, 1); Assert(e.Observe(f, 252, true, 1).Keys.Length == 0, "stale key held");
        });
        Test("Remap analog threshold and directional axis cancellation preserve both players", () => {
            var axis = new RemapBinding { Input = RemapInput.LeftRight, Action = new RemapAction { Kind = RemapActionKind.GamepadAxis, Axis = RemapAxis.LX, Direction = 1, Player = 2 } };
            var opposite = new RemapBinding { Input = RemapInput.LeftLeft, Action = new RemapAction { Kind = RemapActionKind.GamepadAxis, Axis = RemapAxis.LX, Direction = -1, Player = 2 } };
            var p = Remap(RemapKey(RemapInput.LT, 65), axis, opposite); p.Mode = RemapMode.Split;
            var e = RemapStarted(p); var f = RemapFrame(1, 1); f.Mode = RemapMode.Split;
            f.Values[RemapInput.LT] = 0.49; f.Values[RemapInput.LeftRight] = 0.75;
            var o = e.Observe(f, 1, true, 1); Assert(o.Keys.Length == 0 && o.PlayerOne.LX == 0 && o.PlayerTwo.LX == 24575, "analog routing");
            f.Sequence++; f.CapturedAtMilliseconds++; f.Values[RemapInput.LT] = 0.5; f.Values[RemapInput.LeftLeft] = 0.75;
            o = e.Observe(f, 2, true, 1); Assert(o.Keys.Single() == 65 && o.PlayerTwo.LX == 0, "threshold/cancellation");
        });
        Test("Remap button destinations include virtual axes and all five mouse buttons", () => {
            foreach (RemapMouseButton mouse in Enum.GetValues(typeof(RemapMouseButton))) {
                if (mouse == 0) continue;
                var e = RemapStarted(Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.MouseButton, MouseButton = mouse } }));
                Assert(e.Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1).MouseButtons.Single() == mouse, "mouse destination missing");
            }
            var axis = new RemapBinding { Input = RemapInput.Y3, Amount = 50, Action = new RemapAction { Kind = RemapActionKind.GamepadAxis, Axis = RemapAxis.RX, Direction = -1, Player = 1 } };
            Assert(RemapStarted(Remap(axis)).Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1).PlayerOne.RX == -16384, "button to axis missing");
        });
        Test("Remap relative motion preserves displacement and ignores duplicate samples", () => {
            var binding = new RemapBinding { Input = RemapInput.SensorRight, Amount = 50, Action = new RemapAction { Kind = RemapActionKind.MouseMove, X = 1 } };
            var e = RemapStarted(Remap(binding)); var f = RemapFrame(1, 10); f.Values[RemapInput.SensorRight] = 7;
            Assert(e.Observe(f, 10, true, 1).MouseX == 3.5, "relative displacement became velocity");
            Assert(e.Observe(f, 11, true, 1).MouseX == 0, "relative replay");
            f.Sequence++; f.CapturedAtMilliseconds = 12;
            Assert(e.Observe(f, 12, true, 1).MouseX == 3.5, "next relative displacement lost");
        });
        Test("Remap velocity depends on elapsed time not poll count", () => {
            var binding = new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.MouseMove, X = 1 } };
            double[] totals = new double[2];
            for (int pass = 0; pass < 2; pass++) {
                var e = RemapStarted(Remap(binding)); int step = pass == 0 ? 10 : 20;
                for (int t = step; t <= 100; t += step) totals[pass] += e.Observe(RemapFrame(t, t, RemapInput.Y3), t, true, 1).MouseX;
            }
            Assert(totals[0] == 100 && totals[1] == 100, "poll-rate dependent velocity");
        });
        Test("Remap function edges pause return and mode changes release all owned state", () => {
            foreach (var function in new[] { RemapFunction.GameBar, RemapFunction.PauseRemap, RemapFunction.ReturnOriginal }) {
                var e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65), new RemapBinding { Input = RemapInput.M3, Behavior = RemapBehavior.Tap,
                    Action = new RemapAction { Kind = RemapActionKind.Function, Function = function } }));
                e.Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1);
                var o = e.Observe(RemapFrame(2, 2, RemapInput.Y3, RemapInput.M3), 2, true, 1);
                Assert(o.Functions.Single() == function, "function missing");
                if (function == RemapFunction.GameBar) Assert(o.Keys.Length == 1 && e.Observe(RemapFrame(3, 3, RemapInput.M3), 3, true, 1).Functions.Length == 0, "held function repeated");
                else Assert(o.Keys.Length == 0 && (function == RemapFunction.PauseRemap ? e.Active && e.Paused : !e.Active), "terminal function retained output");
            }
            var engine = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65))); var changed = RemapFrame(1, 1); changed.Mode = RemapMode.Gamepad;
            Assert(engine.Observe(changed, 1, true, 1).Keys.Length == 0 && !engine.Active, "mode change stayed active");
        });
        Test("Remap repeated reads preserve velocity and expire taps without replay", () => {
            var e = RemapStarted(Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.MouseMove, X = 1 } },
                RemapKey(RemapInput.M3, 65, RemapBehavior.Tap)));
            var first = RemapFrame(1, 10, RemapInput.Y3, RemapInput.M3);
            Assert(e.Observe(first, 10, true, 1).MouseX == 10, "initial velocity");
            Assert(e.Observe(first, 15, true, 1).MouseX == 0, "duplicate velocity replay");
            var second = RemapFrame(2, 20, RemapInput.Y3, RemapInput.M3);
            Assert(e.Observe(second, 20, true, 1).MouseX == 10, "duplicate read reduced elapsed motion");
            Assert(e.Observe(second, 41, true, 1).Keys.Length == 0, "duplicate read retained expired tap");
        });
        Test("Remap every virtual button supports P2 and releases independently", () => {
            foreach (int button in ControllerLayout.Buttons) {
                var p = Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.GamepadButton, Button = button, Player = 2 } }); p.Mode = RemapMode.Split;
                var e = RemapStarted(p); var f = RemapFrame(1, 1, RemapInput.Y3); f.Mode = RemapMode.Split;
                var output = e.Observe(f, 1, true, 1); Assert(output.PlayerOne.Buttons == 0 && output.PlayerTwo.Buttons == button, "P2 button missing");
                f.Sequence++; f.CapturedAtMilliseconds++; f.Values.Clear();
                Assert(e.Observe(f, 2, true, 1).PlayerTwo.Buttons == 0, "P2 release missing");
            }
            var e2 = RemapStarted(Remap(new RemapBinding { Input = RemapInput.Y3, Action = new RemapAction { Kind = RemapActionKind.GamepadButton, Button = 1, Player = 1 } },
                new RemapBinding { Input = RemapInput.M3, Action = new RemapAction { Kind = RemapActionKind.GamepadButton, Button = 2, Player = 1 } }));
            Assert(e2.Observe(RemapFrame(1, 1, RemapInput.Y3, RemapInput.M3), 1, true, 1).PlayerOne.Buttons == 0, "opposed D-pad did not cancel");
        });
        Test("Remap pause resume and stop clear toggles and require neutral again", () => {
            var e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65, RemapBehavior.Toggle)));
            e.Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1);
            Assert(e.Pause().Keys.Length == 0 && e.Paused, "pause kept output");
            Assert(e.Observe(RemapFrame(2, 2), 2, true, 1).Keys.Length == 0, "paused output");
            e.Resume(); Assert(e.Observe(RemapFrame(3, 3, RemapInput.Y3), 3, true, 1).Keys.Length == 0, "resume held input");
            e.Observe(RemapFrame(4, 4), 4, true, 1);
            Assert(e.Observe(RemapFrame(5, 5, RemapInput.Y3), 5, true, 1).Keys.Single() == 65, "resume retained old toggle");
            Assert(e.Stop().Keys.Length == 0 && !e.Active && e.Observe(RemapFrame(6, 6, RemapInput.Y3), 6, true, 1).Keys.Length == 0, "stop reactivated");
        });
        Test("Remap malformed normalized input releases output instead of clamping silently", () => {
            foreach (double value in new[] { -1.0, 0.5, 2.0, double.PositiveInfinity }) {
                var e = RemapStarted(Remap(RemapKey(RemapInput.Y3, 65)));
                e.Observe(RemapFrame(1, 1, RemapInput.Y3), 1, true, 1); var f = RemapFrame(2, 2); f.Values[RemapInput.Y3] = value;
                Assert(e.Observe(f, 2, true, 1).Keys.Length == 0 && e.WaitingForNeutral, "invalid digital sample emitted");
            }
        });
        Test("Remap catalog rejects duplicate identities names and damaged loads without writes", () => {
            var a = Remap(); var b = Remap(); b.Name = "Otra distribución"; b.Id = a.Id;
            RemapReject(() => new RemapProfiles { Profiles = new[] { a, b } }.Encode());
            b.Id = Guid.NewGuid().ToString("D"); b.Name = a.Name.ToUpperInvariant();
            RemapReject(() => new RemapProfiles { Profiles = new[] { a, b } }.Encode());
            var storage = new RemapMemoryStorage { Bytes = new byte[] { 0xff } };
            var e = new ControllerRemapEngine(); var w = new ControllerRemapWorkspace(storage, e);
            RemapReject(w.Load); Assert(storage.Writes == 0 && storage.Bytes.Single() == 0xff && !e.Active, "load repaired or activated implicitly");
        });
        Test("Remap workspace load select save and external mutations never activate", () => {
            var storage = new RemapMemoryStorage(); var e = new ControllerRemapEngine(); var w = new ControllerRemapWorkspace(storage, e);
            w.Load(); w.SetDraft(Remap(RemapKey(RemapInput.Y3, 65))); Assert(w.Dirty, "new draft not dirty");
            w.SaveDraft(); var id = w.Draft.Id; w.Select(id);
            Assert(storage.Writes == 1 && !e.Active && !w.Dirty, "save/select activated");
            w.ActivateSaved(id, RemapCaps()); var draft = w.Draft; draft.Bindings[0].Action.Keys[0] = 66; w.SetDraft(draft); w.SaveDraft();
            Assert(e.ActiveProfile.Bindings[0].Action.Keys[0] == 65 && w.Saved.Profiles[0].Bindings[0].Action.Keys[0] == 66, "saving changed active");
            var external = w.Saved; external.Profiles[0].Bindings[0].Action.Keys[0] = 67;
            Assert(w.Saved.Profiles[0].Bindings[0].Action.Keys[0] == 66, "saved alias escaped");
            w.Load(); Assert(e.Active && w.Draft == null, "load changed live session");
            RemapReject(() => w.Delete(id)); e.Stop(); w.Delete(id); Assert(w.Saved.Profiles.Length == 0, "delete failed");
        });
        Test("Remap workspace preserves catalog on write verify invalid and concurrent failures", () => {
            for (int reason = 0; reason < 5; reason++) {
                var storage = new RemapMemoryStorage(); var w = new ControllerRemapWorkspace(storage, new ControllerRemapEngine());
                w.Load(); w.SetDraft(Remap(RemapKey(RemapInput.Y3, 65))); w.SaveDraft();
                var id = w.Draft.Id; var d = w.Draft; d.Name = "Editado"; w.SetDraft(d);
                if (reason == 0) storage.FailWrite = true;
                if (reason == 1) storage.FailVerify = true;
                if (reason == 2) storage.Bytes = new byte[] { 0xff };
                if (reason == 3) storage.Bytes = new RemapProfiles().Encode();
                if (reason == 4) storage.Race = true;
                bool failed = false; try { w.SaveDraft(); } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is InvalidOperationException) { failed = true; }
                Assert(failed && w.Saved.Profiles.Single().Id == id && w.Saved.Profiles.Single().Name != "Editado" && w.Dirty, "failed save replaced memory");
            }
        });
        Test("Remap editing status is read only and reports unavailable native activation", () => {
            var storage = new RemapMemoryStorage(); var service = new RemapEditingService(storage, true);
            var status = service.Execute("remap_status", null);
            Assert(status.EditingAvailable && !status.ActivationAvailable && status.Revision.Length == 64
                && status.Catalog.Profiles.Length == 0 && storage.Writes == 0 && storage.Bytes == null, "query mutated or advertised native route");
            RemapReject(() => service.Execute("remap_status", new RemapEditingRequest()));
            RemapReject(() => new RemapEditingService(storage, false).Execute("remap_status", null));
        });
        Test("Remap editing save delete and revisions round trip through IPC contracts", () => {
            var storage = new RemapMemoryStorage(); var service = new RemapEditingService(storage, true);
            var status = service.Execute("remap_status", null); string revision = status.Revision;
            var profile = Remap(RemapKey(RemapInput.Y3, 0x87));
            var request = Protocol.Decode<Request>(Protocol.Encode(new Request { Operation = "remap_save", Remap = new RemapEditingRequest { Revision = revision, Profile = profile } }));
            status = service.Execute(request.Operation, request.Remap);
            Assert(storage.Writes == 1 && status.Revision != revision && !status.ActivationAvailable && status.Catalog.Profiles.Single().Bindings[0].Action.Keys.Single() == 0x87, "save contract lost");
            profile.Bindings[0].Action.Keys[0] = 65;
            Assert(service.Execute("remap_status", null).Catalog.Profiles[0].Bindings[0].Action.Keys[0] == 0x87, "caller alias changed disk");
            var response = Protocol.Decode<Response>(Protocol.Encode(new Response { SupportsRemapEditing = true, Remap = status }));
            Assert(response.SupportsRemapEditing && response.Remap.Revision == status.Revision && !response.Remap.ActivationAvailable, "response contract lost");
            RemapReject(() => service.Execute("remap_delete", new RemapEditingRequest { Revision = revision, Id = profile.Id }));
            status = service.Execute("remap_delete", new RemapEditingRequest { Revision = status.Revision, Id = profile.Id });
            Assert(status.Catalog.Profiles.Length == 0 && storage.Writes == 2, "explicit delete failed");
        });
        Test("Remap stale editors and unavailable activation never overwrite or apply", () => {
            var storage = new RemapMemoryStorage(); var first = new RemapEditingService(storage, true); var second = new RemapEditingService(storage, true);
            string revision = first.Execute("remap_status", null).Revision; second.Execute("remap_status", null);
            var profile = Remap(); var saved = first.Execute("remap_save", new RemapEditingRequest { Revision = revision, Profile = profile });
            RemapReject(() => second.Execute("remap_save", new RemapEditingRequest { Revision = revision, Profile = profile }));
            RemapReject(() => first.Execute("remap_activate", new RemapEditingRequest { Revision = saved.Revision, Id = profile.Id }));
            Assert(storage.Writes == 1 && first.Execute("remap_status", null).Revision == saved.Revision, "stale/activation mutated storage");
        });
        Test("Remap editing rejects malformed payloads before writing", () => {
            var storage = new RemapMemoryStorage(); var service = new RemapEditingService(storage, true); string revision = service.Execute("remap_status", null).Revision;
            foreach (var request in new[] { null, new RemapEditingRequest(), new RemapEditingRequest { Revision = revision },
                new RemapEditingRequest { Revision = revision, Profile = Remap(), Id = Guid.NewGuid().ToString("D") },
                new RemapEditingRequest { Revision = new string('0',64), Profile = Remap() } })
                RemapReject(() => service.Execute("remap_save", request));
            RemapReject(() => service.Execute("remap_delete", new RemapEditingRequest { Revision = revision, Profile = Remap() }));
            RemapReject(() => service.Execute("remap_unknown", null));
            Assert(storage.Writes == 0, "malformed mutation written");
        });
        Test("Remap editing preserves invalid files instead of silently replacing them", () => {
            var storage = new RemapMemoryStorage { Bytes = new byte[] { 0xff } }; var service = new RemapEditingService(storage, true);
            RemapReject(() => service.Execute("remap_status", null));
            RemapReject(() => service.Execute("remap_save", new RemapEditingRequest { Revision = new string('0', 64), Profile = Remap() }));
            Assert(storage.Bytes.Single() == 0xff && storage.Writes == 0, "invalid catalog repaired implicitly");
        });
        Test("Remap session separates catalog IPC from controller power and recording", () => WithRecordingDirectory(folder => {
            var hardware = new Fake(); var journal = new Journal(); var controller = new EnginePreparationController();
            var storage = new RemapMemoryStorage(); var editor = new RemapEditingService(storage, true);
            using (var session = new HelperSession(new ControlEngine(hardware, journal, controller), folder, false, controller, remap: editor)) {
                var status = session.Execute(new Request { Operation = "remap_status" });
                Assert(status.Success && status.SupportsRemapEditing && !status.Remap.ActivationAvailable && storage.Writes == 0, "IPC status");
                foreach (var request in new[] {
                    new Request { Operation = "remap_status", Ac = true }, new Request { Operation = "remap_status", Key = "Spl" },
                    new Request { Operation = "remap_status", Values = new[] { 1 } }, new Request { Operation = "remap_status", Profile = FullProfile() },
                    new Request { Operation = "remap_status", Display = new DisplayRequest() },
                    new Request { Operation = "remap_status", GameBarInput = new GameBarInputContext() },
                    new Request { Operation = "remap_status", RtssAppearance = new RtssAppearance() },
                    new Request { Operation = "set", Key = "Spl", Values = new[] { 12 }, Remap = new RemapEditingRequest() } })
                    Assert(!session.Execute(request).Success, "mixed IPC accepted");
                var profile = Remap(RemapKey(RemapInput.Y3, 65));
                var saved = session.Execute(new Request { Operation = "remap_save", Remap = new RemapEditingRequest { Revision = status.Remap.Revision, Profile = profile } });
                Assert(saved.Success && storage.Writes == 1, "IPC save failed");
                Assert(!session.Execute(new Request { Operation = "remap_activate", Remap = new RemapEditingRequest { Revision = saved.Remap.Revision, Id = profile.Id } }).Success, "unverified native activated");
                Assert(hardware.Writes.Count == 0 && hardware.Reads.Count == 0 && journal.Saves == 0 && controller.Applies == 0 && !session.Recorder.GetStatus().Active, "editor touched hardware/session");
            }
        }));
        Test("Remap older helper responses keep editing and activation unavailable", () => WithRecordingDirectory(folder => {
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
                Assert(!session.Execute(new Request { Operation = "remap_status" }).Success && !session.Execute(new Request { Operation = "recording_status" }).SupportsRemapEditing, "old helper advertised editor");
            var old = Protocol.Decode<Response>("{}"); Assert(!old.SupportsRemapEditing && old.Remap == null, "old response became capable");
        }));
    }
}
