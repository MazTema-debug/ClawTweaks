// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;
using LegionTweaks.Widget;

internal static partial class Program
{
    private const string TrialPath = @"\\?\hid#vid_17ef&pid_61ee&mi_02#synthetic#{00000000-0000-0000-0000-000000000001}";
    private static RemapProfile TrialProfile(int key = 0x51) => Remap(RemapKey(RemapInput.Y3, key, RemapBehavior.Tap));
    [DataContract]
    private sealed class LegacyTrialStatus
    {
        [DataMember] public bool Active { get; set; }
        [DataMember] public long PulsesSent { get; set; }
    }
    [DataContract]
    private sealed class LegacyTrialResponse
    {
        [DataMember] public bool Success { get; set; }
        [DataMember] public FpsY3TrialStatus FpsY3Trial { get; set; }
    }
    private sealed class TrialSource : IFpsY3Source
    {
        internal long Now;
        internal bool CanBegin = true, Missing;
        internal int Starts, Stops;
        internal FpsY3Sample Sample = new FpsY3Sample { Product = 0x61ee, Path = TrialPath, Report = FpsReport() };
        public long NowMilliseconds => Now;
        public bool CanBeginY3 => CanBegin && Starts == Stops;
        public void BeginY3Read() { Starts++; }
        public void EndY3Read() { Stops++; }
        public FpsY3Sample ReadY3() => Missing ? null : new FpsY3Sample { Product = Sample.Product, Path = Sample.Path,
            Report = Sample.Report == null ? null : (byte[])Sample.Report.Clone(), Sequence = Sample.Sequence, CapturedAt = Sample.CapturedAt };
        internal void Next(byte buttons = 0, int elapsed = 40)
        { Now += elapsed; Sample.CapturedAt = Now; Sample.Sequence++; Sample.Report = FpsReport(buttons); }
    }
    private sealed class TrialLease : IFpsY3Lease
    {
        internal int Starts, Verifies, Restores;
        internal bool IsPending, IsMuted, FailBegin, FailRestore, FailVerify;
        internal Action OnVerify, OnBegin;
        public long VerifierWarmupMs { get; set; }
        public long LastNativeVerifyMs { get; set; }
        public bool Pending => IsPending;
        public bool Muted => IsMuted;
        public string DevicePath { get; private set; }
        public void Begin(string path) {
            Starts++; DevicePath = path; IsPending = IsMuted = true;
            OnBegin?.Invoke();
            if (FailBegin) throw new InvalidOperationException("native begin failed after possible write");
        }
        public void Verify() { Verifies++; OnVerify?.Invoke(); if (FailVerify) throw new InvalidOperationException("native map changed"); }
        public void Restore() {
            if (!IsPending) return;
            Restores++; if (FailRestore) throw new InvalidOperationException("device absent during return");
            IsPending = IsMuted = false;
        }
    }
    private sealed class TrialOutput : IFpsGuardedKeyOutput
    {
        internal MouseDesktop Desktop = new MouseDesktop { Available = true, Foreground = 1 };
        internal List<int> Keys = new List<int>();
        internal bool Accept = true, Fail, CanRelease = true;
        internal Action OnPulse;
        internal int Releases, PulseCalls;
        public string LastPulseDecision { get; private set; }
        public int LastAcceptedEvents { get; private set; } = -1;
        public MouseDesktop Inspect() => Desktop;
        public bool Pulse(int key, MouseDesktop expected) => Pulse(key, expected, null);
        public bool Pulse(int key, MouseDesktop expected, Func<bool> canSend) {
            PulseCalls++; LastAcceptedEvents = -1; LastPulseDecision = "not_sent"; OnPulse?.Invoke();
            if (Fail) { LastPulseDecision = "simulated_error"; throw new InvalidOperationException("output failed"); }
            if (!expected.Available || !Desktop.Available) { LastPulseDecision = "desktop_or_modifier"; return false; }
            if (Desktop.Foreground != expected.Foreground) { LastPulseDecision = "focus_changed"; return false; }
            if (canSend != null && !canSend()) { LastPulseDecision = "edge_expired"; return false; }
            if (Accept) { Keys.Add(key); LastAcceptedEvents = 2; LastPulseDecision = "pair_accepted"; }
            else LastPulseDecision = "output_rejected";
            return Accept;
        }
        public bool ReleaseOwned() { Releases++; return CanRelease; }
    }
    private sealed class TrialRig : IDisposable
    {
        internal readonly TrialSource Source = new TrialSource();
        internal readonly TrialLease Lease = new TrialLease();
        internal readonly TrialOutput Output = new TrialOutput();
        internal readonly FpsY3TrialService Trial;
        internal TrialRig(bool supported = true) { Trial = new FpsY3TrialService(supported, Source, Lease, Output, false); }
        internal void Start() { Trial.Start(TrialProfile()); Trial.Tick(); }
        internal void Tick(byte bits = 0) { Source.Next(bits); Trial.Tick(); }
        public void Dispose() { Lease.FailRestore = false; Output.CanRelease = true; Trial.Dispose(); }
    }
    private sealed class TrialEditorService : IRemapEditorService
    {
        internal readonly RemapMemoryStorage Storage = new RemapMemoryStorage();
        internal readonly TrialRig Rig = new TrialRig();
        internal readonly RemapEditingService Backend;
        internal readonly List<Request> Requests = new List<Request>();
        internal TrialEditorService(RemapProfile profile) {
            Storage.Bytes = new RemapProfiles { Profiles = new[] { profile } }.Encode();
            Backend = new RemapEditingService(Storage, true, Rig.Trial);
        }
        public Task<Response> Send(Request request) {
            Requests.Add(request);
            try { return Task.FromResult(new Response { Success = true, SupportsRemapEditing = true,
                Remap = Backend.Execute(request.Operation, request.Remap), FpsY3Trial = Backend.TrialStatus }); }
            catch (Exception ex) { return Task.FromResult(new Response { Message = ex.Message, SupportsRemapEditing = true, FpsY3Trial = Backend.TrialStatus }); }
        }
    }
    private static byte[] TrialMap(int selector, byte y3 = 6)
    {
        int count = selector == 3 ? 15 : 16;
        var b = new byte[64]; b[0] = 4; b[2] = 0x6c; b[3] = 0x0b; b[4] = (byte)selector; b[5] = b[6] = 1; b[7] = (byte)count;
        int at = 8;
        for (int i = 0; i < count; i++) {
            byte button = (byte)(i + (selector == 3 ? 1 : 21));
            b[at++] = button; b[at++] = 1; b[at++] = button == 30 ? y3 : button == 34 ? (byte)5 : (byte)0;
        }
        b[1] = (byte)at; b[at] = 1; return b;
    }
    private static byte[] TrialUsb(int selector)
    {
        var b = new byte[64]; b[0] = 4; b[1] = 21; b[2] = 0x79; b[3] = 1; b[4] = (byte)selector;
        b[18] = b[19] = 1; b[20] = 0xa0; b[21] = 1; return b;
    }
    private static FpsY3NativeOriginal TrialOriginal() => new FpsY3NativeOriginal { Path = TrialPath,
        Container = "00000000-0000-0000-0000-000000000002", Usb3 = TrialUsb(3), Usb4 = TrialUsb(4), Map3 = TrialMap(3), Map4 = TrialMap(4) };
    private static FpsY3LeaseRecord TrialRecord(string phase = "Original") {
        long now = DateTime.UtcNow.Ticks;
        return new FpsY3LeaseRecord { Sid = "S-1-5-21-synthetic", Token = Guid.NewGuid().ToString("N"), Phase = phase,
            SourcePath = TrialPath, OwnerPid = 123, OwnerStarted = now - 1, CreatedUtc = now,
            ExpiresUtc = now + TimeSpan.FromMilliseconds(FpsY3TrialRules.DurationMilliseconds).Ticks,
            Original = phase == "Preparing" ? null : TrialOriginal() };
    }
    private static T TrialCopy<T>(T value) => Protocol.Decode<T>(Protocol.Encode(value));

    // Isolated child fixture: no hardware, journal, installation or helper process.
    private static bool TryRunY3VerifierFixture(string[] args)
    {
        if (args == null || args.Length != 2 || args[0] != "--test-y3-verifier") return false;
        string mode = args[1];
        if (mode == "orphan-owner") {
            var child = FpsY3Verifier.Start(Y3FixtureInfo("normal"), "fixture", 5000);
            Console.WriteLine(child.ProcessId); Console.Out.Flush(); Environment.Exit(0);
        }
        if (mode == "delayed-ready") Thread.Sleep(300);
        if (mode == "bad-reply") { Console.WriteLine("READY"); Console.Out.Flush(); Console.ReadLine(); Console.WriteLine("garbage"); return true; }
        var replies = Console.Out;
        if (mode == "quiet") Console.SetOut(TextWriter.Null);
        try {
            Environment.ExitCode = FpsY3VerifyProtocol.Serve(Console.In, replies, () => {
                if (mode == "hang") { Thread.Sleep(5000); return true; }
                if (mode == "exit") Environment.Exit(17);
                if (mode == "quiet") Console.WriteLine("simulated native output");
                return mode != "fail";
            });
        }
        finally { if (mode == "quiet") Console.SetOut(replies); }
        return true;
    }
    private static ProcessStartInfo Y3FixtureInfo(string mode)
    {
        string host = Environment.ProcessPath;
        string argument = "--test-y3-verifier " + mode;
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            argument = "\"" + Assembly.GetEntryAssembly().Location + "\" " + argument;
        return new ProcessStartInfo(host, argument) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
    }

    private static void FpsY3TrialTests()
    {
        Test("Y3 verifier protocol permits only repeated verification and fails closed", () => {
            int calls = 0; var output = new StringWriter();
            Assert(FpsY3VerifyProtocol.Serve(new StringReader("VERIFY\nVERIFY\n"), output, () => { calls++; return true; }) == 0
                && calls == 2 && output.ToString().Split('\n').Count(x => x.StartsWith("OK ")) == 2, "worker did not verify every request");
            calls = 0; output = new StringWriter();
            Assert(FpsY3VerifyProtocol.Serve(new StringReader("MUTE\n"), output, () => { calls++; return true; }) == 2 && calls == 0, "worker accepted a write command");
            output = new StringWriter();
            Assert(FpsY3VerifyProtocol.Serve(new StringReader("VERIFY\nVERIFY\n"), output, () => { calls++; return false; }) == 1 && calls == 1, "worker continued after failure");
        });
        Test("Y3 prepared verifier reuses one isolated process for two full requests", () => {
            var startup = Stopwatch.StartNew();
            using (var verifier = FpsY3Verifier.Start(Y3FixtureInfo("delayed-ready"), "fixture", 5000)) {
                long prepared = startup.ElapsedMilliseconds; int pid = verifier.ProcessId;
                var check = Stopwatch.StartNew(); Assert(verifier.Verify(5000), "first verification failed"); long first = check.ElapsedMilliseconds;
                check.Restart(); Assert(verifier.Verify(5000) && verifier.ProcessId == pid, "verification started a second process"); long second = check.ElapsedMilliseconds;
                Assert(prepared >= 250 && verifier.LastNativeVerifyMs >= 0, "startup simulation not separated from verification");
                Console.WriteLine("METRIC Y3 simulated prepared-process startup=" + prepared + " ms, checks=" + first + "/" + second + " ms (not hardware)");
            }
        });
        Test("Y3 verifier protocol stays intact when native console output is suppressed", () => {
            using (var verifier = FpsY3Verifier.Start(Y3FixtureInfo("quiet"), "fixture", 5000))
                Assert(verifier.Verify(5000) && verifier.Verify(5000), "native logs corrupted verifier replies");
        });
        Test("Y3 prepared verifier rejects failure bad reply unexpected exit and hung isolated child", () => {
            foreach (string mode in new[] { "fail", "bad-reply", "exit", "hang" }) {
                using (var verifier = FpsY3Verifier.Start(Y3FixtureInfo(mode), "fixture", 5000)) {
                    int pid = verifier.ProcessId; bool rejected = false;
                    try { rejected = !verifier.Verify(mode == "hang" ? 200 : 5000); } catch (Exception) { rejected = true; }
                    Assert(rejected, "failed verifier was accepted: " + mode);
                    try { using (var child = Process.GetProcessById(pid)) Assert(child.HasExited || child.WaitForExit(2000), "failed child survived: " + mode); }
                    catch (ArgumentException) { }
                }
            }
        });
        Test("Y3 prepared verifier exits when its isolated owner exits", () => {
            var info = Y3FixtureInfo("orphan-owner"); info.RedirectStandardInput = true;
            using (var owner = Process.Start(info)) {
                var line = owner.StandardOutput.ReadLineAsync(); Assert(line.Wait(8000), "fixture owner did not start child");
                Assert(int.TryParse(line.Result, out int pid) && pid > 0, "fixture child PID missing");
                Assert(owner.WaitForExit(5000) && owner.ExitCode == 0, "fixture owner did not exit");
                try { using (var child = Process.GetProcessById(pid)) Assert(child.HasExited || child.WaitForExit(5000), "orphaned verifier stayed alive"); }
                catch (ArgumentException) { }
            }
        });
        Test("Y3 trial accepts exactly one saved FPS keyboard pulse and preserves Original bindings", () => {
            foreach (int key in new[] { 0x1b, 0x2d, 0x30, 0x39, 0x41, 0x5a, 0x70, 0x87 })
                Assert(FpsY3TrialRules.Require(TrialProfile(key)) == key, "supported key rejected");
            var p = TrialProfile(); p.Bindings = p.Bindings.Concat(new[] { new RemapBinding { Input = RemapInput.M3 } }).ToArray();
            Assert(FpsY3TrialRules.Require(p) == 0x51, "Original affected eligibility");
            var caps = FpsY3TrialRules.Capabilities();
            Assert(caps.ReplacedInputs.SequenceEqual(new[] { RemapInput.Y3 }) && caps.VirtualPlayers == 0 && !caps.Mouse, "scope broadened");
        });
        Test("Y3 trial rejects modes chords modifiers holds toggles and other changed controls before reading", () => {
            var bad = new List<RemapProfile> { null, TrialProfile(0x11), TrialProfile(0x20), TrialProfile() };
            bad[3].Mode = RemapMode.Gamepad;
            var p = TrialProfile(); p.Bindings[0].Action.Keys = new[] { 0x41, 0x42 }; bad.Add(p);
            foreach (var behavior in new[] { RemapBehavior.Hold, RemapBehavior.Toggle }) { p = TrialProfile(); p.Bindings[0].Behavior = behavior; bad.Add(p); }
            bad.Add(Remap(RemapKey(RemapInput.M3, 0x51, RemapBehavior.Tap)));
            bad.Add(Remap(RemapKey(RemapInput.Y3, 0x51, RemapBehavior.Tap), RemapKey(RemapInput.A, 0x45)));
            using (var r = new TrialRig()) foreach (var profile in bad) {
                RemapReject(() => r.Trial.Start(profile)); Assert(r.Source.Starts == 0 && r.Lease.Starts == 0, "invalid trial touched source");
            }
        });
        Test("Y3 trial status and unsupported or busy starts perform no native writes", () => {
            using (var r = new TrialRig(false)) { r.Trial.GetStatus(); RemapReject(() => r.Trial.Start(TrialProfile())); Assert(r.Source.Starts == 0, "unsupported reader opened"); }
            using (var r = new TrialRig()) {
                r.Source.CanBegin = false; Assert(!r.Trial.GetStatus().Available, "busy advertised"); RemapReject(() => r.Trial.Start(TrialProfile()));
                Assert(r.Lease.Starts == 0 && r.Output.Keys.Count == 0, "busy output");
            }
        });
        Test("Y3 shared passive reader publishes copied identified samples without legacy output or a second reader", () => WithLegionButtonDirectory(folder => {
            using (var bus = new FpsTestBus()) using (var source = bus.Create(folder)) {
                Assert(source.CanBeginY3 && source.ReadY3() == null && bus.Opens == 0, "status opened input");
                source.BeginY3Read(); LegionButtonWait(() => bus.Active == 1, "shared reader missing");
                bus.Push(source, 0x20); var sample = source.ReadY3();
                Assert(sample != null && sample.Product == 0x61ee && sample.Path == TrialPath && sample.CapturedAt == source.NowMilliseconds, "sample identity/time lost");
                sample.Report[20] = 0; Assert(source.ReadY3().Report[20] == 0x20, "caller changed cached physical input");
                bus.Push(source, 0x04); Assert(source.ReadY3().Sequence > sample.Sequence && bus.Output.Sent.Count == 0 && bus.MaximumActive == 1, "source replay or duplicate/fixed output");
                source.EndY3Read(); LegionButtonWait(() => bus.Active == 0, "reader retained after end"); Assert(source.ReadY3() == null, "old sample survived end");
            }
        }));
        Test("Y3 passive interest blocks capture preparation fixed keys and virtual input until returned", () => WithLegionButtonDirectory(folder => {
            using (var bus = new FpsTestBus()) using (var source = bus.Create(folder)) {
                source.BeginY3Read();
                RemapReject(() => source.SetFpsKeys(true)); RemapReject(() => source.SetControllerInputEnabled(true));
                RemapReject(() => source.SetGamepadInputEnabled(true)); RemapReject(() => source.StartCapture(4, 1));
                RemapReject(() => source.SetPreparationPaused(true)); source.EndY3Read();
                source.SetGamepadInputEnabled(true); Assert(!source.CanBeginY3, "virtual interest allowed trial");
            }
        }));
        Test("Y3 shared source clears physical samples on suspension and never reuses an old frame", () => WithLegionButtonDirectory(folder => {
            using (var bus = new FpsTestBus()) using (var source = bus.Create(folder)) {
                source.BeginY3Read(); LegionButtonWait(() => bus.Active == 1, "reader missing"); bus.Push(source, 0x20);
                source.Suspend(true); Assert(source.ReadY3() == null && !source.CanBeginY3, "suspend retained input");
                source.EndY3Read(); source.Suspend(false); Assert(source.ReadY3() == null && source.CanBeginY3, "resume replayed sample");
            }
        }));
        Test("Y3 trial arms from release pulses once and never repeats a held button", () => {
            using (var r = new TrialRig()) {
                var p = TrialProfile(); r.Trial.Start(p); p.Bindings[0].Action.Keys[0] = 0x45;
                r.Trial.Tick(); r.Tick(0x20); r.Tick(0x20); r.Tick(0x20); r.Tick(); r.Tick(0x20);
                Assert(r.Output.Keys.SequenceEqual(new[] { 0x51, 0x51 }) && r.Trial.GetStatus().PulsesSent == 2, "pulse/snapshot ownership wrong");
                Assert(r.Lease.Verifies == 2, "output without native recheck"); r.Trial.Stop();
                Assert(r.Lease.Restores == 1 && r.Source.Stops == 1 && !r.Trial.GetStatus().Active, "stop did not return original");
            }
        });
        Test("Y3 trial waits for release after activation even when the button becomes held during native setup", () => {
            using (var r = new TrialRig()) {
                r.Trial.Start(TrialProfile()); r.Tick(0x20); r.Tick(0x20);
                Assert(r.Output.Keys.Count == 0, "held activation escaped"); r.Tick(); r.Tick(0x20); Assert(r.Output.Keys.Count == 1, "release failed to arm");
            }
        });
        Test("Y3 focus or blocked desktop requires a fresh release before output resumes", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Output.Desktop.Available = false; r.Tick(0x20);
                r.Output.Desktop.Available = true; r.Tick(0x20); Assert(r.Output.Keys.Count == 0, "blocked hold escaped");
                r.Tick(); r.Output.Desktop.Foreground = 2; r.Tick(0x20); Assert(r.Output.Keys.Count == 0, "focus switch emitted");
                r.Tick(); r.Tick(0x20); Assert(r.Output.Keys.Count == 1, "fresh focus did not rearm");
            }
        });
        Test("Y3 stale future absent wrong-device and wrong-format input stop and restore instead of emitting", () => {
            for (int reason = 0; reason < 7; reason++) using (var r = new TrialRig()) {
                r.Start(); r.Source.Next(0x20);
                if (reason == 0) r.Source.Now += 251;
                if (reason == 1) r.Source.Sample.CapturedAt++;
                if (reason == 2) r.Source.Missing = true;
                if (reason == 3) r.Source.Sample.Path += "different";
                if (reason == 4) r.Source.Sample.Product = 0x61eb;
                if (reason == 5) r.Source.Sample.Report[2]++;
                if (reason == 6) r.Source.Sample.Report = new byte[63];
                r.Trial.Tick(); Assert(!r.Trial.GetStatus().Active && r.Lease.Restores == 1 && r.Output.Keys.Count == 0, "bad physical source emitted");
            }
        });
        Test("Y3 trial rejects duplicate native acquisition and releases a partial failed begin", () => {
            using (var r = new TrialRig()) { r.Start(); RemapReject(() => r.Trial.Start(TrialProfile())); Assert(r.Lease.Starts == 1 && r.Trial.GetStatus().Active, "duplicate damaged active trial"); }
            using (var r = new TrialRig()) { r.Lease.FailBegin = true; RemapReject(() => r.Trial.Start(TrialProfile())); Assert(r.Lease.Restores == 1 && r.Source.Stops == 1, "partial native write not recovered"); }
        });
        Test("Y3 native verification failure or source loss during verification produces no key and returns original", () => {
            for (int reason = 0; reason < 3; reason++) using (var r = new TrialRig()) {
                r.Start(); r.Lease.FailVerify = reason == 0;
                if (reason == 1) r.Lease.OnVerify = () => r.Source.Missing = true;
                if (reason == 2) r.Lease.OnVerify = () => r.Source.Now += 251;
                r.Tick(0x20); Assert(r.Output.Keys.Count == 0, "output survived stale verification");
                Assert(!r.Trial.GetStatus().Active && r.Lease.Restores == 1, "lost verification not recovered");
            }
        });
        Test("Y3 accepts a press up to one second old only with a separate fresh report", () => {
            foreach (int delay in new[] { 251, 1000 }) using (var r = new TrialRig()) {
                r.Start(); r.Lease.LastNativeVerifyMs = delay - 10; r.Source.Next(0x20);
                r.Lease.OnVerify = () => r.Source.Next(0x20, delay);
                r.Trial.Tick(); var d = r.Trial.GetDiagnostics().Attempts.Single();
                Assert(r.Trial.GetStatus().PulsesSent == 1 && r.Output.Keys.SequenceEqual(new[] { 0x51 }), "allowed edge rejected");
                Assert(d.Sequence < d.SubsequentSequence && d.InitialAgeMs == 0 && d.VerifyMs == delay && d.NativeVerifyMs == delay - 10
                    && d.SubsequentAgeMs == 0 && d.SubsequentAgeBeforeSendMs == 0
                    && d.OriginalAgeAfterVerifyMs == delay && d.OriginalAgeBeforeSendMs == delay
                    && d.AcceptedEvents == 2 && d.Decision == "pair_accepted", "diagnostics renewed the original edge");
            }
        });
        Test("Y3 accepts a short press released during verification once within the experimental window", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Source.Next(0x20); r.Lease.OnVerify = () => r.Source.Next(0, 700); r.Trial.Tick();
                var d = r.Trial.GetDiagnostics().Attempts.Single();
                Assert(r.Output.Keys.SequenceEqual(new[] { 0x51 }) && !d.SubsequentDown
                    && d.OriginalAgeBeforeSendMs == 700 && d.SubsequentAgeBeforeSendMs == 0, "released press lost or renewed");
                r.Lease.OnVerify = null; r.Tick(); Assert(r.Output.Keys.Count == 1, "release duplicated the pulse");
                r.Tick(0x20); Assert(r.Output.Keys.Count == 2, "next press did not rearm");
            }
        });
        Test("Y3 rejects a 1001 ms original even with a separate fresh sample and rearms only after release", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Source.Next(0x20); r.Lease.OnVerify = () => r.Source.Next(0x20, 1001); r.Trial.Tick();
                var d = r.Trial.GetDiagnostics().Attempts.Single(); var state = r.Trial.GetStatus();
                Assert(state.Active && state.PulsesSent == 0 && r.Output.PulseCalls == 0 && r.Lease.Restores == 0, "expired edge emitted or stopped the session");
                Assert(d.Sequence < d.SubsequentSequence && d.SubsequentAgeMs == 0 && d.OriginalAgeAfterVerifyMs == 1001
                    && d.SubsequentAgeBeforeSendMs == null
                    && d.Decision == "original_edge_expired" && d.VerifyResult == "confirmed", "new report disguised stale edge");
                Assert(state.Error.Contains("muestra de Y3 venció"), "message attributed delay to one unmeasured stage");
                r.Lease.OnVerify = null; r.Trial.Tick(); r.Tick(0x20); Assert(r.Output.PulseCalls == 0, "held edge replayed");
                r.Tick(); r.Tick(0x20); Assert(r.Output.Keys.SequenceEqual(new[] { 0x51 }) && r.Trial.GetStatus().PulsesSent == 1, "new release/press did not rearm");
                Assert(r.Trial.GetStatus().Error == null && r.Trial.GetDiagnostics().Attempts.First().Decision == "original_edge_expired", "current status confused with historical rejection");
            }
        });
        Test("Y3 expired while waiting for keyboard output never reaches SendInput even if the second sample was fresh", () => {
            for (int delay = 200; delay <= 201; delay++) using (var r = new TrialRig()) {
                r.Start(); r.Lease.OnVerify = () => r.Source.Next(0x20, 800);
                r.Output.OnPulse = () => r.Source.Now += delay; r.Tick(0x20);
                var d = r.Trial.GetDiagnostics().Attempts.Single();
                Assert(d.OriginalAgeAfterVerifyMs == 800 && d.OriginalAgeBeforeSendMs == 800 + delay
                    && d.SubsequentAgeBeforeSendMs == delay && r.Output.PulseCalls == 1, "send-time age not measured");
                Assert((r.Trial.GetStatus().PulsesSent == 1) == (delay == 200), "send-time boundary broken");
                Assert(d.Decision == (delay == 200 ? "pair_accepted" : "edge_expired"), "send-time decision lost");
            }
        });
        Test("Y3 still requires its last checked physical report to be at most 250 ms old at send", () => {
            for (int delay = 250; delay <= 251; delay++) using (var r = new TrialRig()) {
                r.Start(); r.Lease.OnVerify = () => r.Source.Next(0x20, 250);
                r.Output.OnPulse = () => r.Source.Now += delay; r.Tick(0x20);
                var d = r.Trial.GetDiagnostics().Attempts.Single(); var state = r.Trial.GetStatus();
                Assert(d.OriginalAgeBeforeSendMs == 250 + delay && d.SubsequentAgeBeforeSendMs == delay,
                    "source and press ages were mixed");
                Assert(state.Active && state.PulsesSent == (delay == 250 ? 1 : 0), "stale physical report emitted or stopped trial");
                Assert(d.Decision == (delay == 250 ? "pair_accepted" : "subsequent_source_expired"), "source freshness decision lost");
                if (delay == 251) {
                    Assert(state.Error.Contains("última lectura FPS venció"), "source freshness message lost");
                    r.Lease.OnVerify = null; r.Output.OnPulse = null; r.Tick(0x20);
                    Assert(r.Output.Keys.Count == 0, "stale report replayed while held");
                    r.Tick(); r.Tick(0x20); Assert(r.Output.Keys.Count == 1, "fresh release/press did not rearm");
                }
            }
        });
        Test("Y3 second report must remain fresh identified and monotonic after verification", () => {
            for (int reason = 0; reason < 8; reason++) using (var r = new TrialRig()) {
                r.Start(); r.Source.Next(0x20);
                r.Lease.OnVerify = () => {
                    if (reason == 0) r.Source.Missing = true;
                    if (reason == 1) r.Source.Sample.Report = null;
                    if (reason == 2) r.Source.Sample.Product = 0x61eb;
                    if (reason == 3) r.Source.Sample.Path += "other";
                    if (reason == 4) r.Source.Now += 251;
                    if (reason == 5) r.Source.Sample.Sequence--;
                    if (reason == 6) { r.Source.Sample.Sequence++; r.Source.Sample.CapturedAt--; }
                    if (reason == 7) r.Source.Sample.Report = FpsReport();
                };
                r.Trial.Tick(); var d = r.Trial.GetDiagnostics().Attempts.Single();
                Assert(!r.Trial.GetStatus().Active && r.Lease.Restores == 1 && r.Output.PulseCalls == 0, "invalid later sample emitted");
                Assert(d.VerifyResult == "confirmed" && d.Decision == "subsequent_source_error", "failed post-read misreported");
            }
        });
        Test("Y3 focus change during verification rejects the pulse and requires a new neutral sample", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Lease.OnVerify = () => r.Output.Desktop.Foreground = 2; r.Tick(0x20);
                Assert(r.Output.Keys.Count == 0 && r.Trial.GetDiagnostics().Attempts.Single().Decision == "focus_changed", "foreground change sent a key");
                r.Lease.OnVerify = null; r.Tick(0x20); Assert(r.Output.Keys.Count == 0, "held button replayed after focus change");
                r.Tick(); r.Tick(0x20); Assert(r.Output.Keys.Count == 1, "focus change did not rearm after release");
            }
        });
        Test("Y3 deadline reached inside output gate restores without sending a late pulse", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Source.Now = FpsY3TrialRules.DurationMilliseconds - 50; r.Source.Next(0, 0); r.Trial.Tick();
                r.Output.OnPulse = () => r.Source.Now += 50; r.Tick(0x20);
                Assert(!r.Trial.GetStatus().Active && r.Output.Keys.Count == 0 && r.Lease.Restores == 1, "expired lease emitted or failed to restore");
                Assert(r.Trial.GetDiagnostics().Attempts.Last().Decision == "lease_expired", "deadline confused with stale sample");
            }
        });
        Test("Y3 diagnostics are bounded copied private and exported only on an explicit read request", () => WithRecordingDirectory(folder => {
            var p = TrialProfile(); var storage = new RemapMemoryStorage { Bytes = new RemapProfiles { Profiles = new[] { p } }.Encode() };
            using (var r = new TrialRig()) {
                var backend = new RemapEditingService(storage, true, r.Trial);
                using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false, remap: backend)) {
                    r.Start(); for (int i = 0; i < 40; i++) { r.Tick(0x20); r.Tick(); }
                    var ordinary = session.Execute(new Request { Operation = "remap_status" });
                    Assert(ordinary.Success && ordinary.FpsY3Diagnostics == null, "ordinary status exported trace");
                    Assert(!session.Execute(new Request { Operation = "remap_y3_diagnostics", Remap = new RemapEditingRequest() }).Success, "diagnostics accepted a profile");
                    var reply = session.Execute(new Request { Operation = "remap_y3_diagnostics" }); var d = reply.FpsY3Diagnostics;
                    Assert(reply.Success && d.Capacity == 32 && d.ActiveAtSnapshot && !d.RecoveryPendingAtSnapshot
                        && d.SessionOrdinal == ordinary.FpsY3Trial.SessionOrdinal && d.Attempts.Length == 32 && d.Dropped == 8
                        && d.Attempts[0].Number == 9 && d.Attempts[31].Number == 40, "ring lost bounded history");
                    Assert(reply.FpsY3Trial == null && reply.Controller == null && reply.Recording == null && reply.Qam == null, "diagnostic response attached unrelated private status");
                    string json = Protocol.Encode(d);
                    Assert(!json.Contains(TrialPath) && !json.Contains("Report") && !json.Contains("Foreground") && !json.Contains("Token") && !json.Contains("Key"), "private input leaked");
                    Assert(Protocol.Encode(reply).Length < Protocol.MaxMessageLength, "diagnostic export exceeds IPC limit");
                    var olderAttempt = Protocol.Decode<FpsY3AttemptDiagnostic>("{\"Sequence\":7,\"Decision\":\"pair_accepted\"}");
                    Assert(olderAttempt.SubsequentAgeBeforeSendMs == null, "old trace invented a send-time source age");
                    d.Attempts[0].Decision = "mutated";
                    Assert(r.Trial.GetDiagnostics().Attempts[0].Decision != "mutated", "caller mutated internal trace");
                    Assert(Protocol.Decode<Response>("{\"Version\":1,\"Success\":true}").FpsY3Diagnostics == null, "old response lost compatibility");
                    var legacy = Protocol.Decode<LegacyTrialResponse>(Protocol.Encode(ordinary));
                    var legacyStatus = Protocol.Decode<LegacyTrialStatus>(Protocol.Encode(ordinary.FpsY3Trial));
                    Assert(legacy.Success && legacy.FpsY3Trial.Active && legacyStatus.Active && legacyStatus.PulsesSent == 40, "new optional fields broke old JSON readers");
                }
            }
        }));
        Test("Y3 partial Windows acceptance is recorded without counting a full pair and restores the native lease", () => {
            var source = new TrialSource(); var lease = new TrialLease(); var desktop = new MouseDesktop { Available = true, Foreground = 1 };
            int sends = 0; var output = new FpsKeyOutput(() => desktop, key => false, inputs => { sends++; return 1; }, FpsY3TrialRules.IsKey);
            using (var trial = new FpsY3TrialService(true, source, lease, output, false)) {
                trial.Start(TrialProfile()); trial.Tick(); source.Next(0x20); trial.Tick();
                var d = trial.GetDiagnostics().Attempts.Single();
                Assert(!trial.GetStatus().Active && trial.GetStatus().PulsesSent == 0 && lease.Restores == 1, "partial send counted or route retained");
                Assert(sends == 2 && d.AcceptedEvents == 1 && d.Decision == "partial_send", "partial acceptance or owned release hidden");
            }
        });
        Test("Y3 guarded output checks freshness after foreground and modifiers before the actual send", () => {
            int sends = 0, checks = 0; var desktop = new MouseDesktop { Available = true, Foreground = 1 };
            var output = new FpsKeyOutput(() => desktop, key => false, inputs => { sends++; return (uint)inputs.Length; }, FpsY3TrialRules.IsKey);
            Assert(!output.Pulse(0x51, desktop, () => { checks++; return false; }) && sends == 0 && checks == 1
                && output.LastPulseDecision == "edge_expired" && output.LastAcceptedEvents == -1, "guard allowed an expired edge into SendInput");
            Assert(output.Pulse(0x51, desktop, () => { checks++; return true; }) && sends == 1 && output.LastAcceptedEvents == 2, "fresh edge not sent as a pair");
            var previous = desktop; desktop.Foreground = 2;
            Assert(!output.Pulse(0x51, previous, () => { checks++; return true; }) && sends == 1 && checks == 2
                && output.LastPulseDecision == "focus_changed", "focus check did not precede guard/send");
        });
        Test("Y3 partial output retains only its own key for retry if release initially fails", () => {
            var source = new TrialSource(); var lease = new TrialLease(); var desktop = new MouseDesktop { Available = true, Foreground = 1 };
            bool allowRelease = false; var sent = new List<GameBarShortcut.Input[]>();
            var output = new FpsKeyOutput(() => desktop, key => false, inputs => {
                sent.Add(inputs); return inputs.Length == 2 || allowRelease ? 1u : 0u;
            }, FpsY3TrialRules.IsKey);
            using (var trial = new FpsY3TrialService(true, source, lease, output, false)) {
                trial.Start(TrialProfile()); trial.Tick(); source.Next(0x20); trial.Tick();
                Assert(!trial.GetStatus().Active && trial.GetStatus().RecoveryPending && lease.Restores == 1 && source.Stops == 0, "failed release lost recovery state");
                Assert(trial.GetDiagnostics().Attempts.Single().AcceptedEvents == 1 && sent.Skip(1).All(batch => batch.Length == 1
                    && batch[0].Key == 0x51 && batch[0].Flags == 2), "cleanup touched another key");
                allowRelease = true; trial.Tick();
                Assert(!trial.GetStatus().RecoveryPending && source.Stops == 1, "owned key cleanup did not retry");
            }
        });
        Test("Y3 status and stop wait for the same in-flight verification without claiming early restoration", () => {
            using (var r = new TrialRig()) using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            using (var statusEntered = new ManualResetEventSlim()) {
                r.Start(); r.Source.Next(0x20);
                r.Lease.OnVerify = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("simulated verification timeout"); };
                var ticking = Task.Factory.StartNew(() => r.Trial.Tick(), TaskCreationOptions.LongRunning);
                Assert(entered.Wait(5000), "verification did not begin");
                var status = Task.Factory.StartNew(() => { var clock = Stopwatch.StartNew(); statusEntered.Set(); r.Trial.GetStatus(); return clock.ElapsedMilliseconds; }, TaskCreationOptions.LongRunning);
                var backend = new RemapEditingService(new RemapMemoryStorage(), true, r.Trial);
                var stop = Task.Factory.StartNew(() => { var clock = Stopwatch.StartNew(); backend.StopTrial(); return clock.ElapsedMilliseconds; }, TaskCreationOptions.LongRunning);
                try { Assert(statusEntered.Wait(1000) && SpinWait.SpinUntil(() => r.Trial.StopRequested, 1000) && !status.Wait(75) && !stop.Wait(75)
                    && r.Lease.Restores == 0, "status/stop claimed an early return"); }
                finally { release.Set(); }
                Assert(Task.WaitAll(new Task[] { ticking, status, stop }, 5000), "simulated tasks stalled");
                Assert(!r.Trial.GetStatus().Active && r.Lease.Restores == 1 && !r.Lease.Pending && r.Output.PulseCalls == 0, "stop sent a late key or did not return");
                Assert(r.Trial.GetDiagnostics().Attempts.Single().Decision == "stop_requested", "stop decision not captured");
                Console.WriteLine("METRIC Y3 simulated blocked verification: status waited=" + status.Result + " ms, stop waited=" + stop.Result + " ms (not hardware)");
            }
        });
        Test("Y3 stop requested during native setup restores before start can report success", () => {
            using (var r = new TrialRig()) using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) {
                r.Lease.OnBegin = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("simulated setup timeout"); };
                var starting = Task.Factory.StartNew(() => { try { r.Trial.Start(TrialProfile()); return false; } catch (InvalidOperationException) { return true; } }, TaskCreationOptions.LongRunning);
                Assert(entered.Wait(5000), "setup did not begin");
                var stop = Task.Factory.StartNew(() => r.Trial.Stop(), TaskCreationOptions.LongRunning);
                try { Assert(SpinWait.SpinUntil(() => r.Trial.StopRequested, 1000) && !stop.Wait(75) && r.Lease.Restores == 0, "early stop was lost"); }
                finally { release.Set(); }
                Assert(Task.WaitAll(new Task[] { starting, stop }, 5000) && starting.Result, "setup reported success after stop");
                Assert(!r.Trial.GetStatus().Active && r.Lease.Restores == 1 && r.Source.Stops == 1 && r.Output.PulseCalls == 0, "interrupted setup did not restore");
            }
        });
        Test("Y3 stop requested inside the output gate prevents the key and then restores", () => {
            using (var r = new TrialRig()) {
                r.Start(); Task stop = null;
                r.Output.OnPulse = () => {
                    stop = Task.Factory.StartNew(() => r.Trial.Stop(), TaskCreationOptions.LongRunning);
                    if (!SpinWait.SpinUntil(() => r.Trial.StopRequested, 1000)) throw new TimeoutException("stop did not reach gate");
                };
                r.Tick(0x20);
                Assert(stop != null && stop.Wait(5000) && r.Output.Keys.Count == 0 && r.Lease.Restores == 1, "stop race emitted or failed to restore");
                Assert(r.Trial.GetDiagnostics().Attempts.Single().Decision == "stop_requested", "stop race not diagnosed");
            }
        });
        Test("Y3 rejected output is not counted and requires release before retry", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Output.Accept = false; r.Tick(0x20); r.Output.Accept = true; r.Tick(0x20);
                Assert(r.Trial.GetStatus().PulsesSent == 0, "rejected output counted"); r.Tick(); r.Tick(0x20);
                Assert(r.Trial.GetStatus().PulsesSent == 1, "valid retry lost");
            }
        });
        Test("Y3 output error stops trial and invokes owned-key cleanup and native restoration", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Output.Fail = true; r.Tick(0x20);
                Assert(!r.Trial.GetStatus().Active && r.Output.Releases > 0 && r.Lease.Restores == 1 && r.Trial.GetStatus().Error != null, "output failure hidden");
            }
        });
        Test("Y3 failed restoration retains recovery and exclusive reader interest until independently restored", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Lease.FailRestore = true; RemapReject(r.Trial.Stop);
                Assert(r.Trial.GetStatus().RecoveryPending && r.Source.Stops == 0, "pending return released route");
                RemapReject(() => r.Trial.Start(TrialProfile())); r.Lease.IsPending = r.Lease.IsMuted = false; r.Trial.Tick();
                Assert(!r.Trial.GetStatus().RecoveryPending && r.Source.Stops == 1, "watcher recovery not recognized");
            }
        });
        Test("Y3 owned-key release failure blocks a new trial and retries only owned cleanup", () => {
            using (var r = new TrialRig()) {
                r.Start(); r.Output.CanRelease = false; RemapReject(r.Trial.Stop);
                Assert(r.Trial.GetStatus().RecoveryPending && r.Source.Stops == 0 && r.Lease.Restores == 1, "key release hid pending cleanup or prevented native return");
                RemapReject(() => r.Trial.Start(TrialProfile())); r.Output.CanRelease = true; r.Trial.Tick();
                Assert(!r.Trial.GetStatus().RecoveryPending && r.Source.Stops == 1, "owned cleanup never retried");
            }
        });
        Test("Y3 deadline or lost supervisor lease restores without automatic restart", () => {
            for (int reason = 0; reason < 2; reason++) using (var r = new TrialRig()) {
                r.Start(); if (reason == 0) r.Source.Now += FpsY3TrialRules.DurationMilliseconds; else r.Lease.IsMuted = false;
                r.Trial.Tick(); r.Tick(); Assert(r.Lease.Restores == 1 && r.Lease.Starts == 1 && !r.Trial.GetStatus().Active, "expired route restarted");
            }
        });
        Test("Y3 catalog reads and saves stay passive and activation requires the exact saved revision", () => {
            var p = TrialProfile(); var service = new TrialEditorService(p);
            using (service.Rig) {
                var state = service.Backend.Execute("remap_status", null);
                state = service.Backend.Execute("remap_save", new RemapEditingRequest { Revision = state.Revision, Profile = p.Copy() });
                Assert(service.Rig.Lease.Starts == 0 && !state.ActivationAvailable, "catalog activated hardware");
                RemapReject(() => service.Backend.Execute("remap_y3_test", new RemapEditingRequest { Revision = new string('0', 64), Id = p.Id }));
                RemapReject(() => service.Backend.Execute("remap_y3_test", new RemapEditingRequest { Revision = state.Revision, Id = Guid.NewGuid().ToString("D") }));
                RemapReject(() => service.Backend.Execute("remap_y3_test", new RemapEditingRequest { Revision = state.Revision, Profile = p.Copy(), Id = p.Id }));
                Assert(service.Rig.Lease.Starts == 0, "unconfirmed saved profile reached hardware");
                service.Backend.Execute("remap_y3_test", new RemapEditingRequest { Revision = state.Revision, Id = p.Id });
                RemapReject(() => service.Backend.Execute("remap_delete", new RemapEditingRequest { Revision = state.Revision, Id = p.Id }));
                Assert(service.Rig.Trial.GetStatus().Active, "active saved profile deleted");
            }
        });
        Test("Y3 return does not depend on a readable profile catalog", () => {
            var service = new TrialEditorService(TrialProfile()); using (service.Rig) {
                service.Rig.Start(); service.Storage.Bytes = new byte[] { 0xff };
                var stopped = service.Backend.Execute("remap_y3_stop", null);
                Assert(service.Rig.Lease.Restores == 1 && !service.Rig.Trial.GetStatus().Active, "corrupt catalog prevented return");
                Assert(!stopped.EditingAvailable && stopped.Catalog == null && !stopped.Y3Trial.Active, "corrupt catalog was reported as confirmed");
            }
        });
        Test("Y3 editor confirms native return with corrupt catalog while retaining its unconfirmed local draft", () => {
            var p = TrialProfile(); var service = new TrialEditorService(p); using (service.Rig) {
                var editor = new RemapEditor(service); editor.Refresh().GetAwaiter().GetResult(); editor.Select(p.Id);
                Assert(editor.TestY3().GetAwaiter().GetResult(), "trial not started"); editor.Current.Touch(); var draft = editor.Current;
                service.Storage.Bytes = new byte[] { 0xff };
                Assert(editor.StopY3().GetAwaiter().GetResult() && !editor.CanSave && editor.ReloadRequired
                    && ReferenceEquals(draft, editor.Current) && draft.Dirty, "return discarded draft or claimed corrupt catalog confirmed");
            }
        });
        Test("Y3 helper IPC advertises status passively rejects mixed requests and returns on suspension", () => WithRecordingDirectory(folder => {
            var p = TrialProfile(); var storage = new RemapMemoryStorage { Bytes = new RemapProfiles { Profiles = new[] { p } }.Encode() };
            using (var rig = new TrialRig()) {
                var hardware = new Fake(); var journal = new Journal(); var backend = new RemapEditingService(storage, true, rig.Trial);
                using (var session = new HelperSession(new ControlEngine(hardware, journal), folder, false, remap: backend)) {
                    var response = session.Execute(new Request { Operation = "remap_status" });
                    Assert(response.Success && response.FpsY3Trial.Available && rig.Lease.Starts == 0, "status started native route");
                    var request = new RemapEditingRequest { Revision = response.Remap.Revision, Id = p.Id };
                    Assert(!session.Execute(new Request { Operation = "remap_y3_test", Remap = request, Profile = FullProfile() }).Success, "trial applied power profile");
                    Assert(!session.Execute(new Request { Operation = "remap_y3_test", Remap = request, Values = new[] { 1 } }).Success, "trial accepted generic values");
                    response = session.Execute(new Request { Operation = "remap_y3_test", Remap = request });
                    Assert(response.Success && response.FpsY3Trial.Active && rig.Lease.Starts == 1, "explicit IPC not wired");
                    session.Suspend(); Assert(!rig.Trial.GetStatus().Active && rig.Lease.Restores == 1, "suspend retained native trial");
                    Assert(hardware.Writes.Count == 0 && journal.Saves == 0 && !session.Recorder.GetStatus().Active, "trial changed power or recording");
                }
            }
        }));
        Test("Y3 editor starts only a clean eligible saved profile and sends no draft or unrelated setting", () => {
            var p = TrialProfile(); var service = new TrialEditorService(p); using (service.Rig) {
                var editor = new RemapEditor(service); editor.Refresh().GetAwaiter().GetResult(); editor.Select(p.Id);
                Assert(editor.CanTestY3, "eligible profile disabled"); editor.Current.Touch();
                Assert(!editor.CanTestY3 && !editor.TestY3().GetAwaiter().GetResult(), "dirty trial activated"); editor.RestoreSaved();
                Assert(editor.TestY3().GetAwaiter().GetResult() && editor.CanStopY3, "explicit saved trial unavailable");
                var request = service.Requests.Last(); Assert(request.Operation == "remap_y3_test" && request.Remap.Id == p.Id
                    && request.Remap.Profile == null && request.Profile == null && request.Key == null && request.Values == null, "trial carried draft or energy");
                editor.Invalidate(); Assert(editor.CanStopY3, "view invalidation hid return");
                Assert(editor.StopY3().GetAwaiter().GetResult() && service.Requests.Last().Remap == null, "return required new profile");
            }
        });
        Test("Y3 editor keeps legacy helper activation disabled and honors current trial capability", () => {
            var p = TrialProfile(); var editor = ReadyEditor(new EditorService(p)); editor.Select(p.Id);
            Assert(!editor.CanTestY3, "legacy catalog enabled trial");
            editor.ObserveY3(new FpsY3TrialStatus { Available = true }); Assert(editor.CanTestY3, "new capability ignored");
            editor.ObserveY3(new FpsY3TrialStatus { RecoveryPending = true }); Assert(!editor.CanTestY3 && editor.CanStopY3, "recovery state unsafe");
        });
        Test("Y3 explicit key allowlist retains paired pulses and does not broaden legacy fixed buttons", () => {
            var sent = new List<GameBarShortcut.Input[]>(); var d = new MouseDesktop { Available = true, Foreground = 1 };
            var output = new FpsKeyOutput(() => d, key => false, inputs => { sent.Add(inputs); return (uint)inputs.Length; }, FpsY3TrialRules.IsKey);
            Assert(output.Pulse(0x51, d) && sent.Single().Length == 2 && sent[0][0].Key == 0x51 && sent[0][0].Flags == 0 && sent[0][1].Flags == 2, "key pulse incorrect");
            RemapReject(() => new FpsKeyOutput(() => d, key => false, inputs => 2).Pulse(0x51, d));
            RemapReject(() => output.Pulse(0x11, d));
        });
        Test("Y3 key sender refuses preexisting key modifiers or a changed foreground", () => {
            var d = new MouseDesktop { Available = true, Foreground = 1 }; int sends = 0;
            foreach (int blocked in new[] { 0x51, 0x11, 0x10, 0x12, 0x5b, 0x5c }) {
                var output = new FpsKeyOutput(() => d, key => key == blocked, inputs => { sends++; return 2; }, FpsY3TrialRules.IsKey);
                Assert(!output.Pulse(0x51, d), "preexisting key affected");
            }
            var changed = new FpsKeyOutput(() => new MouseDesktop { Available = true, Foreground = 2 }, key => false,
                inputs => { sends++; return 2; }, FpsY3TrialRules.IsKey);
            Assert(!changed.Pulse(0x51, d) && sends == 0, "wrong foreground output");
        });
        Test("Y3 partial SendInput cleans only its own selected key", () => {
            var d = new MouseDesktop { Available = true, Foreground = 1 }; var sent = new List<GameBarShortcut.Input[]>();
            var output = new FpsKeyOutput(() => d, key => false, inputs => { sent.Add(inputs); return 1; }, FpsY3TrialRules.IsKey);
            bool failed = false; try { output.Pulse(0x51, d); } catch (System.ComponentModel.Win32Exception) { failed = true; }
            Assert(failed && sent.Count == 2 && sent[1].Length == 1 && sent[1][0].Key == 0x51 && sent[1][0].Flags == 2 && output.ReleaseOwned(), "partial cleanup released foreign input");
        });
        Test("Y3 native allowlist ABI and index decoding reject every mutated output byte", FpsY3NativeTransport.CheckProtocol);
        Test("Y3 native ACK accepts observed result one and rejects failure selector counts or padding", () => {
            var ack = new byte[64]; ack[0] = 4; ack[1] = 8; ack[2] = 0x6c; ack[3] = 0x0a; ack[4] = 4; ack[5] = ack[6] = ack[7] = ack[8] = 1;
            FpsY3NativeTransport.ValidateY3Ack(ack);
            foreach (int offset in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 63 }) { var bad = (byte[])ack.Clone(); bad[offset] ^= 0x40; RemapReject(() => FpsY3NativeTransport.ValidateY3Ack(bad)); }
        });
        Test("Y3 map decoder rejects wrong selectors indexes counts duplicate rows and truncation", () => {
            Assert(FpsY3NativeTransport.DecodeMap(TrialMap(3), 3).Count == 15 && FpsY3NativeTransport.DecodeMap(TrialMap(4), 4).Count == 16, "valid map rejected");
            foreach (int offset in new[] { 1, 4, 5, 6, 7, 9, 56, 63 }) { var bad = TrialMap(4); bad[offset] ^= 0x80; RemapReject(() => FpsY3NativeTransport.DecodeMap(bad, 4)); }
            var duplicate = TrialMap(4); duplicate[11] = duplicate[8]; RemapReject(() => FpsY3NativeTransport.DecodeMap(duplicate, 4));
            RemapReject(() => FpsY3NativeTransport.DecodeMap(TrialMap(4), 1));
        });
        Test("Y3 full readback permits only right Y3 to differ and detects unrelated map drift", () => {
            FpsY3NativeTransport.Match(TrialMap(4), TrialMap(4, 0), 4, 0);
            var drift = TrialMap(4, 0); drift[10] = 1; RemapReject(() => FpsY3NativeTransport.Match(TrialMap(4), drift, 4, 0));
            RemapReject(() => FpsY3NativeTransport.Match(TrialMap(4), TrialMap(4), 4, 0));
            var left = TrialMap(3); left[8] = 30; left[10] = 9; FpsY3NativeTransport.Match(left, (byte[])left.Clone(), 3, 0);
        });
        Test("Y3 original backup requires exact target protocol native assignments and physical identity", () => {
            TrialOriginal().Validate();
            var mutations = new Action<FpsY3NativeOriginal>[] { x => x.Path = "other", x => x.Container = Guid.Empty.ToString("D"),
                x => x.Usb3[20]++, x => x.Usb4[4] = 3, x => x.Map4 = TrialMap(4, 27), x => x.Map4[49] = 27, x => x.Map3[5] = 2 };
            foreach (var mutate in mutations) { var original = TrialOriginal(); mutate(original); RemapReject(original.Validate); }
            var trailing = TrialOriginal(); trailing.Usb3[22] = 0x80; trailing.Validate();
        });
        Test("Y3 software suite cannot open native hardware through the net8 transport", () => {
            bool blocked = false; try { FpsY3NativeTransport.RequireOriginalGo(); } catch (PlatformNotSupportedException) { blocked = true; }
            Assert(blocked, "net8 reached native device enumeration");
        });
        Test("Y3 recovery record rejects missing backups malformed identities or widened deadlines", () => {
            var valid = TrialRecord(); valid.Validate(valid.Sid);
            var mutations = new Action<FpsY3LeaseRecord>[] { x => x.Original = null, x => x.Token = "bad", x => x.ExpiresUtc++, x => x.WatchPid = 10,
                x => x.OwnerPid = 0, x => x.SourcePath = "other", x => x.Phase = "anything" };
            foreach (var mutate in mutations) { var bad = TrialCopy(valid); mutate(bad); RemapReject(() => bad.Validate(valid.Sid)); }
            RemapReject(() => valid.Validate("another-sid"));
        });
        Test("Y3 journal permits capture once forward phases and recovery from every unfinished phase", () => {
            var r = TrialRecord("Preparing"); var next = TrialCopy(r); next.Original = TrialOriginal(); next.Phase = "Original"; r.RequireSuccessor(next); r = next;
            foreach (string phase in new[] { "Applying", "Muted", "Restoring", "Restored" }) { next = TrialCopy(r); next.Phase = phase; r.RequireSuccessor(next); r = next; }
            foreach (string phase in new[] { "Preparing", "Original", "Applying", "Muted", "Restoring" }) { r = TrialRecord(phase); next = TrialCopy(r); next.Phase = "Restoring"; r.RequireSuccessor(next); }
        });
        Test("Y3 journal cannot replace the captured original change ownership or resurrect a restored lease", () => {
            var r = TrialRecord();
            var mutations = new Action<FpsY3LeaseRecord>[] { x => x.Original.Map3[10]++, x => x.OwnerPid++, x => x.Token = Guid.NewGuid().ToString("N"),
                x => x.SourcePath += "other", x => x.CreatedUtc++, x => x.Phase = "Muted" };
            foreach (var mutate in mutations) { var next = TrialCopy(r); mutate(next); RemapReject(() => r.RequireSuccessor(next)); }
            r = TrialRecord("Restored"); var retry = TrialCopy(r); retry.Phase = "Restoring"; RemapReject(() => r.RequireSuccessor(retry));
            r = TrialRecord("Preparing"); retry = TrialCopy(r); retry.Original = TrialOriginal(); RemapReject(() => r.RequireSuccessor(retry));
        });
    }
}
