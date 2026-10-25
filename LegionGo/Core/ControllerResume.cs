// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    // Notification callbacks only change this small snapshot. SDL/ViGEm remain
    // owned by the existing controller worker, including during resume.
    internal sealed class ControllerPowerState
    {
        private readonly object gate = new object();
        private ControllerPowerSnapshot value;
        internal ControllerPowerSnapshot Read() { lock (gate) return value; }
        internal bool SetSuspended(bool suspended)
        {
            lock (gate)
            {
                if (value.Suspended == suspended) return false;
                value.Suspended = suspended; value.Revision++;
                return true;
            }
        }
    }
    internal struct ControllerPowerSnapshot { internal bool Suspended; internal long Revision; }

    internal interface IControllerResumePort
    {
        void Pause(); // Neutral target, stop rumble/mouse, close only the source.
        bool TryOpen(); // Same physical identity and same connected virtual target.
        void Waiting(bool suspended, ControllerRecoveryKind kind);
        void Recovered(ControllerRecoveryKind kind);
    }
    internal enum ControllerRecoveryKind { Resume, SamplingGap, Reconnect }

    internal sealed class ControllerResume
    {
        internal const int RetryMilliseconds = 250, TimeoutMilliseconds = 20000, SampleGapMilliseconds = 2000;
        private readonly ControllerPowerState power;
        private readonly IControllerResumePort port;
        private readonly Func<bool> canceled;
        private readonly Func<long> milliseconds;
        private long revision, lastTick, deadline = -1, nextTry;
        private long disconnectDeadline = -1, stableSince = -1;
        private bool paused, waitingForNeutral;
        private ControllerRecoveryKind kind;
        internal bool WaitingForNeutral => waitingForNeutral;
        internal bool PowerChanged => power.Read().Revision != revision;
        internal bool ReadWasInterrupted(long now) => PowerChanged || now < lastTick || now - lastTick >= SampleGapMilliseconds;

        internal ControllerResume(ControllerPowerState power, IControllerResumePort port, long now, Func<bool> canceled = null, Func<long> milliseconds = null, bool waitForNeutral = false)
        {
            this.power = power; this.port = port; revision = power.Read().Revision; lastTick = now;
            waitingForNeutral = waitForNeutral;
            this.canceled = canceled; this.milliseconds = milliseconds;
        }
        internal void SourceDisconnected(long now)
        {
            if (canceled?.Invoke() == true || paused) return;
            // One outage gets one budget, including a source that repeatedly
            // opens but drops again before a full second of fresh samples.
            if (disconnectDeadline < 0) disconnectDeadline = now + TimeoutMilliseconds;
            stableSince = -1; kind = ControllerRecoveryKind.Reconnect;
            port.Pause(); paused = true; waitingForNeutral = true;
            deadline = disconnectDeadline; nextTry = now;
            port.Waiting(false, kind);
        }
        internal void ObserveFreshInput(long now)
        {
            if (disconnectDeadline < 0 || paused || PowerChanged || power.Read().Suspended) return;
            if (stableSince < 0) stableSince = now;
            if (now >= stableSince && now - stableSince >= 1000)
            { disconnectDeadline = stableSince = -1; }
        }
        // False means no source input may be forwarded. No target connect,
        // firmware write, game guard, profile load or power setting lives here.
        internal bool Step(long now)
        {
            if (canceled?.Invoke() == true) return false;
            var state = power.Read();
            bool changed = state.Revision != revision;
            bool gap = now < lastTick || now - lastTick >= SampleGapMilliseconds;
            lastTick = now;
            if (changed || (!paused && gap) || (state.Suspended && !paused))
            {
                revision = state.Revision;
                kind = changed || state.Suspended ? ControllerRecoveryKind.Resume : ControllerRecoveryKind.SamplingGap;
                if (!paused) { port.Pause(); paused = true; }
                stableSince = -1;
                if (changed || state.Suspended) { deadline = disconnectDeadline = stableSince = -1; }
                else if (disconnectDeadline >= 0) deadline = disconnectDeadline;
                waitingForNeutral = true;
                port.Waiting(state.Suspended, kind);
            }
            if (!paused) return true;
            if (state.Suspended) return false;
            if (deadline < 0) { deadline = now + TimeoutMilliseconds; nextTry = now; }
            if (now >= deadline) throw new TimeoutException(kind == ControllerRecoveryKind.Reconnect
                ? ControllerRecoveryMessages.ReconnectTimeout : ControllerRecoveryMessages.ResumeTimeout);
            if (now < nextTry) return false;
            nextTry = now + RetryMilliseconds;
            if (!port.TryOpen()) return false;
            if (canceled?.Invoke() == true || power.Read().Revision != revision)
            {
                port.Pause(); // A second suspend raced with the fresh open.
                deadline = -1;
                return false;
            }
            if ((milliseconds?.Invoke() ?? now) >= deadline)
            { port.Pause(); throw new TimeoutException(ControllerRecoveryMessages.OpenTimeout); }
            paused = false; deadline = -1;
            port.Recovered(kind);
            return true;
        }
        internal bool AcceptInput(GamepadFrame frame, ControllerLayout layout)
        {
            if (canceled?.Invoke() == true || PowerChanged || power.Read().Suspended) return false;
            if (!waitingForNeutral) return true;
            int left = Math.Max(1000, layout.LeftDeadzone * 32768 / 100);
            int right = Math.Max(1000, layout.RightDeadzone * 32768 / 100);
            if (frame.Buttons != 0 || frame.LegionButtons != 0 || frame.LT > 5 || frame.RT > 5
                || Math.Abs((int)frame.LX) > left || Math.Abs((int)frame.LY) > left
                || Math.Abs((int)frame.RX) > right || Math.Abs((int)frame.RY) > right) return false;
            waitingForNeutral = false;
            return true;
        }
    }
}
