// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LegionTweaks
{
    public static class DesktopTrayProtocol
    {
        public const string ServiceName = "LegionTweaks.DesktopTray";
        public const int Version = 1;
    }

    // Per desktop view, called only on its dispatcher. Never attach to a widget.
    public sealed class DesktopTrayState
    {
        public bool Ready { get; private set; }
        public bool Hiding { get; private set; }
        public bool Hidden { get; private set; }
        public bool Exiting { get; private set; }
        public bool InterceptClose => Ready && !Exiting;
        public void Connected() { if (!Exiting) Ready = true; }
        public bool BeginHide()
        {
            if (!Ready || Hiding || Hidden || Exiting) return false;
            Hiding = true; return true;
        }
        public void EndHide(bool success) { Hiding = false; Hidden = success; }
        public void Restored() { Hidden = false; }
        public bool Disconnected()
        {
            Ready = false;
            return (Hidden || Hiding) && !Exiting;
        }
        public void Exit() { Exiting = true; Ready = false; }
    }

    public sealed class DesktopFrameCandidate
    {
        public long Handle;
        public string ClassName, ApplicationId;
        public bool SystemFrameHost;
    }
    public static class DesktopFrameSelection
    {
        // A UWP CoreWindow need not be a child of its shell frame. Accept one
        // exact application frame, never another hosted widget or a guessed HWND.
        public static long Select(string applicationId, IEnumerable<DesktopFrameCandidate> candidates)
        {
            if (string.IsNullOrEmpty(applicationId) || candidates == null) return 0;
            long selected = 0;
            foreach (var candidate in candidates) {
                if (candidate == null || candidate.Handle == 0 || !candidate.SystemFrameHost
                    || candidate.ClassName != "ApplicationFrameWindow" || candidate.ApplicationId != applicationId) continue;
                if (selected != 0 && selected != candidate.Handle) return 0;
                selected = candidate.Handle;
            }
            return selected;
        }
    }
    public static class HostedWidgetExit
    {
        // Do not terminate the shared UI process until Game Bar acknowledges
        // closing each hosted CoreWindow. A timeout/failure preserves the UI.
        public static async Task<bool> CloseAllAsync(IEnumerable<Func<Task<bool>>> closeViews, Task timeout)
        {
            if (closeViews == null || timeout == null) throw new ArgumentNullException();
            var closures = closeViews.Select(CloseSafely).ToArray();
            if (closures.Length == 0) return true;
            var all = Task.WhenAll(closures);
            if (await Task.WhenAny(all, timeout).ConfigureAwait(false) != all) return false;
            return (await all.ConfigureAwait(false)).All(value => value);
        }
        private static async Task<bool> CloseSafely(Func<Task<bool>> close)
        {
            try { return close != null && await close().ConfigureAwait(false); }
            catch (Exception) { return false; }
        }
    }
}
