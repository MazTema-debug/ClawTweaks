// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    public static class DesktopTrayProtocol
    {
        public const string ServiceName = "LegionTweaks.DesktopTray";
        public const int Version = 1;
    }

    public interface IDesktopWindowPort
    {
        bool Valid { get; }
        bool Visible { get; }
        bool Minimized { get; }
        Task<bool> ShowAsync(bool visible);
        bool SetTaskbarVisible(bool visible);
    }

    public static class DesktopWindowVisibility
    {
        // The caller serializes requests and supplies the window/shell context.
        // Preserve that context across awaits for the native shell adapter.
        public static async Task<bool> HideAsync(IDesktopWindowPort port)
        {
            if (!IsValid(port)) return false;
            bool attempted = false;
            try {
                attempted = true;
                bool hidden = await port.ShowAsync(false);
                if (hidden && IsValid(port) && !port.Visible && IsValid(port)) {
                    bool removed = port.SetTaskbarVisible(false);
                    if (removed && IsValid(port) && !port.Visible && IsValid(port)) return true;
                }
            }
            catch (Exception) { }
            // Hiding can partially succeed even when its acknowledgment fails.
            // Keep the same verified identity for rollback; never select a new frame.
            if (attempted) await RestoreAsync(port);
            return false;
        }

        public static async Task<bool> RestoreAsync(IDesktopWindowPort port)
        {
            if (!IsValid(port)) return false;
            bool taskbar = false, shown = false;
            try { if (IsValid(port)) taskbar = port.SetTaskbarVisible(true); }
            catch (Exception) { }
            // A shell failure must not prevent recovering the actual window.
            try { if (IsValid(port)) shown = await port.ShowAsync(true); }
            catch (Exception) { }
            try { return taskbar && shown && IsValid(port) && port.Visible && !port.Minimized && IsValid(port); }
            catch (Exception) { return false; }
        }

        private static bool IsValid(IDesktopWindowPort port)
        {
            try { return port != null && port.Valid; }
            catch (Exception) { return false; }
        }
    }

    public sealed class DesktopOpenGesture
    {
        private readonly uint observedTick, lastInputTick;
        public DesktopOpenGesture(uint observedTick, uint lastInputTick)
        { this.observedTick = observedTick; this.lastInputTick = lastInputTick; }
        public bool CanActivate(uint nowTick, uint currentLastInputTick) => currentLastInputTick == lastInputTick
            && unchecked(nowTick - observedTick) <= 2000u;
    }

    public sealed class DesktopWindowQueue
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public async Task<bool> RunAsync(Func<Task<bool>> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            await gate.WaitAsync();
            try { return await action(); }
            finally { gate.Release(); }
        }
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
            if (!Ready || Hiding || Exiting) return false;
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
