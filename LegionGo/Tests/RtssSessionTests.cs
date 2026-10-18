// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void RtssSessionTests()
    {
        Test("RTSS session validates commands before enabling presentation and never writes power", () => WithRecordingDirectory(folder => {
            var h = new Fake(); var journal = new Journal(); var output = new SessionRtss();
            using (var session = new HelperSession(new ControlEngine(h, journal), folder, false, rtss: output))
            {
                var idle = session.Execute(new Request { Operation = "recording_status" });
                Assert(idle.Success && idle.Rtss.Supports && !idle.Rtss.Enabled && output.Sets == 0 && h.Reads.Count == 0, "idle RTSS sampled or enabled");
                foreach (int[] value in new[] { null, new int[0], new[] { -1 }, new[] { 2 }, new[] { 1, 0 } })
                    Assert(!session.Execute(new Request { Operation = "rtss_set", Values = value }).Success, "invalid state accepted");
                Assert(!session.Execute(new Request { Operation = "rtss_set", Version = 99, Values = new[] { 1 } }).Success && output.Sets == 0, "protocol validation bypassed");
                var on = session.Execute(new Request { Operation = "rtss_set", Values = new[] { 1 } });
                Assert(on.Success && on.Rtss.Enabled && output.Sets == 1 && on.State == null, "presentation request lost or sampled hardware");
                Assert(session.Execute(new Request { Operation = "rtss_status" }).Rtss.Enabled && output.Sets == 1, "status modified RTSS");
                Assert(session.Execute(new Request { Operation = "rtss_set", Values = new[] { 0 } }).Success && !output.Enabled, "OFF failed");
                Assert(h.Reads.Count == 0 && h.Writes.Count == 0 && journal.Saves == 0, "RTSS commands changed hardware or originals");
            }
        }));
        Test("Missing RTSS capability and enable failures stay explicit", () => WithRecordingDirectory(folder => {
            using (var session = new HelperSession(new ControlEngine(new Fake(), new Journal()), folder, false))
            {
                Assert(!session.Execute(new Request { Operation = "rtss_set", Values = new[] { 1 } }).Success, "old helper enabled RTSS");
                Assert(!session.Execute(new Request { Operation = "rtss_status" }).Success, "missing capability claimed status");
            }
            var h = new Fake(); var output = new SessionRtss { FailEnable = true };
            using (var session = new HelperSession(new ControlEngine(h, new Journal()), folder, false, rtss: output))
                Assert(!session.Execute(new Request { Operation = "rtss_set", Values = new[] { 1 } }).Success && !output.Enabled
                    && h.Writes.Count == 0, "failed enable became success");
        }));
        Test("RTSS reuses command and recorder snapshots without another telemetry read", () => WithRecordingDirectory(folder => {
            var h = new Fake(); var output = new SessionRtss();
            using (var session = new HelperSession(new ControlEngine(h, new Journal()), folder, false, rtss: output))
            {
                var response = session.Execute(new Request { Operation = "status" });
                Assert(response.Success && output.Observed == 1 && output.LastCapture == response.State.CapturedAtUtc, "status snapshot not forwarded");
                int reads = h.Reads.Count;
                Assert(session.Execute(new Request { Operation = "recording_status" }).Success && h.Reads.Count == reads && output.Observed == 1, "cached status added a hardware read");
                Assert(session.Execute(new Request { Operation = "recording_start" }).Success && output.Observed == 2, "recorder did not share its snapshot");
                session.Recorder.Tick();
                Assert(output.Observed == 3, "recorder tick snapshot not reused");
                session.Execute(new Request { Operation = "recording_stop" });
                Assert(h.Writes.Count == 0, "observation changed power");
            }
        }));
        Test("Optional RTSS failures cannot hide command outcomes or prevent recording cleanup", () => WithRecordingDirectory(folder => {
            var h = new Fake(); var output = new SessionRtss();
            var session = new HelperSession(new ControlEngine(h, new Journal()), folder, false, rtss: output);
            try
            {
                session.Execute(new Request { Operation = "recording_start" });
                output.FailObserve = output.FailStatus = output.FailShutdown = true;
                var changed = session.Execute(Tdp(10, 10, 10));
                Assert(changed.Success && changed.Rtss == null && h.Writes.Count > 0, "presentation error replaced power result");
                session.Suspend();
                Assert(output.Suspends == 1 && session.Execute(new Request { Operation = "controller_stop" }).Success, "presentation failure blocked recovery path");
            }
            finally { session.Dispose(); }
            Assert(output.Disposes == 1 && !session.Recorder.GetStatus().Active, "RTSS disposal error kept CSV active");
        }));
    }

    private sealed class SessionRtss : IRtssOverlay
    {
        internal bool Enabled, FailEnable, FailObserve, FailStatus, FailShutdown;
        internal int Sets, Observed, Suspends, Disposes;
        internal string LastCapture;
        public void SetEnabled(bool value) { if (FailEnable) throw new InvalidOperationException("Sin soporte"); Sets++; Enabled = value; }
        public RtssOverlayStatus GetStatus()
        {
            if (FailStatus) throw new InvalidOperationException("cache failed");
            return new RtssOverlayStatus { Enabled = Enabled, State = Enabled ? "missing" : "off" };
        }
        public void Observe(Response value)
        {
            if (FailObserve) throw new InvalidOperationException("optional publish failed");
            if (value?.State == null) return;
            Observed++; LastCapture = value.State.CapturedAtUtc;
        }
        public void Suspend() { Suspends++; Enabled = false; if (FailShutdown) throw new InvalidOperationException("release failed"); }
        public void Dispose() { Disposes++; Enabled = false; if (FailShutdown) throw new InvalidOperationException("release failed"); }
    }
}
