// SPDX-License-Identifier: AGPL-3.0-only
using LegionTweaks;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

internal static partial class Program
{
    private static void DesktopTrayTests()
    {
        Test("Desktop retains ordinary close until a tray connection is ready", () => {
            var state = new DesktopTrayState();
            Assert(!state.InterceptClose && !state.BeginHide() && !state.Hidden, "unrecoverable window could be hidden");
            state.Connected();
            Assert(state.InterceptClose && state.BeginHide(), "ready tray cannot accept close");
            Assert(state.InterceptClose && !state.BeginHide(), "repeated close could destroy an in-flight hidden view");
        });
        Test("Tray failure during consolidation requires restoration before lifetime release", () => {
            var state = new DesktopTrayState(); state.Connected(); state.BeginHide();
            Assert(state.Disconnected() && !state.InterceptClose, "in-flight hidden view lost recovery");
            state.EndHide(true);
            Assert(state.Hidden && !state.Ready, "disconnect was incorrectly confirmed ready");
            state.Restored();
            Assert(!state.Hidden && !state.BeginHide(), "failed tray stranded or hid desktop again");
        });
        Test("Tray hide revalidates a cached hidden desktop without overlapping requests", () => {
            var state = new DesktopTrayState(); state.Connected(); state.BeginHide(); state.EndHide(true);
            Assert(state.BeginHide() && state.Hidden, "cached hidden state prevented native revalidation");
            Assert(!state.BeginHide(), "hidden desktop accepted overlapping hide requests");
            state.EndHide(true);
            state.Restored();
            Assert(state.Ready && state.BeginHide(), "restored window no longer reusable");
            state.EndHide(false);
            Assert(!state.Hidden && state.InterceptClose, "Windows refusal became a hidden success");
        });
        Test("Explicit interface exit bypasses close-to-tray and disconnect restoration", () => {
            var state = new DesktopTrayState(); state.Connected(); state.BeginHide(); state.EndHide(true);
            state.Exit();
            Assert(!state.Disconnected() && !state.InterceptClose && !state.BeginHide(), "explicit exit reopened the interface");
            state.Connected();
            Assert(!state.Ready && state.Exiting, "late ready acknowledgment canceled exit");
        });
        Test("Desktop minimize uses the exact shell frame rather than its internal CoreWindow", () => {
            var frames = new[] {
                new DesktopFrameCandidate { Handle=10, ClassName="Windows.UI.Core.CoreWindow", ApplicationId="own!App", SystemFrameHost=true },
                new DesktopFrameCandidate { Handle=20, ClassName="ApplicationFrameWindow", ApplicationId="other!App", SystemFrameHost=true },
                new DesktopFrameCandidate { Handle=30, ClassName="ApplicationFrameWindow", ApplicationId="own!App", SystemFrameHost=true }
            };
            Assert(DesktopFrameSelection.Select("own!App", frames)==30, "internal or foreign window replaced desktop shell frame");
        });
        Test("Desktop shell selection rejects untrusted hosts and ambiguous matching frames", () => {
            var spoof = new DesktopFrameCandidate { Handle=1, ClassName="ApplicationFrameWindow", ApplicationId="own!App", SystemFrameHost=false };
            Assert(DesktopFrameSelection.Select("own!App",new[]{spoof})==0, "unverified host accepted");
            var first = new DesktopFrameCandidate { Handle=2, ClassName="ApplicationFrameWindow", ApplicationId="own!App", SystemFrameHost=true };
            var second = new DesktopFrameCandidate { Handle=3, ClassName="ApplicationFrameWindow", ApplicationId="own!App", SystemFrameHost=true };
            Assert(DesktopFrameSelection.Select("own!App",new[]{first,second})==0, "ambiguous frames guessed");
            Assert(DesktopFrameSelection.Select("",new[]{first})==0, "missing app identity accepted");
        });
        Test("Desktop hide requires hidden readback and an accepted taskbar removal", () => {
            var port = new DesktopWindowPortFake();
            Assert(DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult(), "verified hide rejected");
            Assert(!port.Visible && port.ShowRequests.Count == 1 && !port.ShowRequests[0]
                && port.TaskbarRequests.Count == 1 && !port.TaskbarRequests[0], "hide did not update both window and shell");
            Assert(DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult(), "already hidden frame could not be revalidated");
            Assert(port.TaskbarRequests.Count == 2 && !port.TaskbarRequests[1], "already hidden frame skipped taskbar removal");
        });
        Test("Desktop taskbar removal failure restores the same window", () => {
            foreach (bool throws in new[] { false, true }) {
                var port = new DesktopWindowPortFake();
                port.TaskbarResult = visible => { if (!visible && throws) throw new InvalidOperationException(); return visible; };
                Assert(!DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult(), "failed taskbar removal became success");
                Assert(port.Visible && !port.Minimized && port.ShowRequests.Count == 2 && port.ShowRequests[1]
                    && port.TaskbarRequests.Count == 2 && port.TaskbarRequests[1], "partial hide was not rolled back");
            }
        });
        Test("Desktop hide refusal and exception both recover a partially hidden frame", () => {
            foreach (bool throws in new[] { false, true }) {
                var port = new DesktopWindowPortFake();
                port.ShowResult = visible => {
                    port.IsVisible = visible;
                    if (!visible && throws) throw new InvalidOperationException();
                    return Task.FromResult(visible);
                };
                Assert(!DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult(), "failed hide became success");
                Assert(port.Visible && port.ShowRequests.Count == 2 && port.ShowRequests[1]
                    && port.TaskbarRequests.Count == 1 && port.TaskbarRequests[0], "failed hide left the frame inaccessible");
            }
        });
        Test("Desktop hide verifies visibility rather than trusting an acknowledgment", () => {
            var port = new DesktopWindowPortFake();
            port.ShowResult = visible => Task.FromResult(true);
            Assert(!DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult(), "visible frame counted as hidden");
            Assert(port.ShowRequests.Count == 2 && port.ShowRequests[1]
                && port.TaskbarRequests.Count == 1 && port.TaskbarRequests[0], "failed visibility readback skipped recovery");
        });
        Test("Desktop visibility rejects invalid ownership without touching a foreign frame", () => {
            var port = new DesktopWindowPortFake { IsValid = false };
            Assert(!DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult()
                && !DesktopWindowVisibility.RestoreAsync(port).GetAwaiter().GetResult(), "foreign frame accepted");
            Assert(port.ShowRequests.Count == 0 && port.TaskbarRequests.Count == 0 && port.ForeignWrites == 0, "invalid frame was mutated");
            Assert(!DesktopWindowVisibility.HideAsync(null).GetAwaiter().GetResult()
                && !DesktopWindowVisibility.RestoreAsync(null).GetAwaiter().GetResult(), "missing frame accepted");
        });
        Test("Desktop visibility stops rollback writes when ownership is lost after hide", () => {
            var port = new DesktopWindowPortFake();
            port.ShowResult = visible => { port.IsVisible = visible; port.IsValid = false; return Task.FromResult(true); };
            Assert(!DesktopWindowVisibility.HideAsync(port).GetAwaiter().GetResult(), "lost ownership counted as success");
            Assert(port.ShowRequests.Count == 1 && !port.ShowRequests[0] && port.TaskbarRequests.Count == 0 && port.ForeignWrites == 0,
                "rollback wrote to a frame after ownership changed");
        });
        Test("Desktop restore still shows the window if adding its taskbar entry fails", () => {
            foreach (bool throws in new[] { false, true }) {
                var port = new DesktopWindowPortFake { IsVisible = false, IsMinimized = true };
                port.TaskbarResult = visible => { if (throws) throw new InvalidOperationException(); return false; };
                Assert(!DesktopWindowVisibility.RestoreAsync(port).GetAwaiter().GetResult(), "failed shell restore became success");
                Assert(port.Visible && !port.Minimized && port.ShowRequests.Count == 1 && port.ShowRequests[0],
                    "shell failure prevented window recovery");
            }
        });
        Test("Desktop restore attempts the shell even when showing the window fails", () => {
            var port = new DesktopWindowPortFake { IsVisible = false };
            port.ShowResult = visible => { throw new InvalidOperationException(); };
            Assert(!DesktopWindowVisibility.RestoreAsync(port).GetAwaiter().GetResult(), "failed show became success");
            Assert(port.TaskbarRequests.Count == 1 && port.TaskbarRequests[0] && port.ShowRequests.Count == 1,
                "show failure omitted independent shell recovery");
        });
        Test("Desktop restore rechecks ownership before showing the window", () => {
            var port = new DesktopWindowPortFake { IsVisible = false };
            port.TaskbarResult = visible => { port.IsValid = false; return true; };
            Assert(!DesktopWindowVisibility.RestoreAsync(port).GetAwaiter().GetResult(), "foreign frame restoration accepted");
            Assert(port.TaskbarRequests.Count == 1 && port.ShowRequests.Count == 0 && port.ForeignWrites == 0, "show wrote after ownership was lost");
        });
        Test("Desktop restore requires a visible frame that is no longer minimized", () => {
            var port = new DesktopWindowPortFake { IsVisible = false, IsMinimized = true };
            port.ShowResult = visible => { port.IsVisible = visible; return Task.FromResult(true); };
            Assert(!DesktopWindowVisibility.RestoreAsync(port).GetAwaiter().GetResult(), "minimized frame counted as restored");
            port.IsMinimized = false;
            Assert(DesktopWindowVisibility.RestoreAsync(port).GetAwaiter().GetResult(), "visible restored frame rejected");
        });
        Test("Desktop activation gesture expires and rejects intervening user input", () => {
            var gesture = new DesktopOpenGesture(1000, 900);
            Assert(gesture.CanActivate(1000, 900) && gesture.CanActivate(3000, 900), "fresh matching gesture rejected");
            Assert(!gesture.CanActivate(3001, 900), "expired gesture retained foreground permission");
            Assert(!gesture.CanActivate(1001, 901), "new user input reused an old open gesture");
        });
        Test("Desktop activation gesture handles unsigned tick rollover", () => {
            var gesture = new DesktopOpenGesture(uint.MaxValue - 500, uint.MaxValue - 600);
            Assert(gesture.CanActivate(1499, uint.MaxValue - 600), "valid rollover gesture expired early");
            Assert(!gesture.CanActivate(1500, uint.MaxValue - 600), "rollover gesture exceeded its time budget");
        });
        Test("Desktop window queue completes restore before a queued hide and its state update", () => {
            var queue = new DesktopWindowQueue();
            var port = new DesktopWindowPortFake { IsVisible = false, IsMinimized = true };
            var state = new DesktopTrayState(); state.Connected(); state.BeginHide(); state.EndHide(true);
            var releaseRestore = new TaskCompletionSource<bool>();
            bool restoreStarted = false, hideStarted = false;
            var restore = queue.RunAsync(async () => {
                restoreStarted = true;
                await releaseRestore.Task;
                bool restored = await DesktopWindowVisibility.RestoreAsync(port);
                if (restored) state.Restored();
                return restored;
            });
            var hide = queue.RunAsync(async () => {
                hideStarted = true;
                Assert(state.BeginHide(), "serialized hide could not start");
                bool hidden = await DesktopWindowVisibility.HideAsync(port);
                state.EndHide(hidden);
                return hidden;
            });
            Assert(restoreStarted && !restore.IsCompleted && !hideStarted && !hide.IsCompleted,
                "hide overlapped an unfinished restore");
            Assert(port.ShowRequests.Count == 0 && port.TaskbarRequests.Count == 0, "queued hide mutated the frame early");
            releaseRestore.SetResult(true);
            Assert(restore.GetAwaiter().GetResult() && hide.GetAwaiter().GetResult(), "serialized window operation failed");
            Assert(hideStarted && state.Hidden && !state.Hiding && !port.Visible,
                "late restore state overwrote the completed hide");
            Assert(port.ShowRequests.Count == 2 && port.ShowRequests[0] && !port.ShowRequests[1],
                "window mutations were not restored then hidden");
        });
        Test("Desktop window queue releases after an exception and rejects a missing action", () => {
            var queue = new DesktopWindowQueue();
            bool failed = false;
            try { queue.RunAsync(() => { throw new InvalidOperationException(); }).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { failed = true; }
            Assert(failed, "queued action exception was hidden");
            var next = queue.RunAsync(() => Task.FromResult(true));
            Assert(next.Wait(1000) && next.GetAwaiter().GetResult(), "failed action stranded the window queue");
            bool missing = false;
            try { queue.RunAsync(null).GetAwaiter().GetResult(); }
            catch (ArgumentNullException) { missing = true; }
            Assert(missing, "missing queued action was accepted");
        });
        Test("Interface exit waits for Game Bar to close every hosted view", () => {
            var first = new TaskCompletionSource<bool>(); var second = new TaskCompletionSource<bool>(); var timeout = new TaskCompletionSource<bool>();
            var exit = HostedWidgetExit.CloseAllAsync(new Func<Task<bool>>[]{()=>first.Task,()=>second.Task},timeout.Task);
            Assert(!exit.IsCompleted, "process exit allowed before widget acknowledgment");
            second.SetResult(true); Assert(!exit.IsCompleted, "one hosted view was abandoned");
            first.SetResult(true); Assert(exit.GetAwaiter().GetResult(), "all close acknowledgments lost");
        });
        Test("A failed or unacknowledged widget close cannot authorize shared-process exit", () => {
            var pending = new TaskCompletionSource<bool>(); var timeout = new TaskCompletionSource<bool>();
            var exit = HostedWidgetExit.CloseAllAsync(new Func<Task<bool>>[]{()=>pending.Task},timeout.Task);
            timeout.SetResult(true); Assert(!exit.GetAwaiter().GetResult(), "timeout authorized process termination");
            Assert(!HostedWidgetExit.CloseAllAsync(new Func<Task<bool>>[]{()=>{throw new InvalidOperationException();}},Task.Delay(1000)).GetAwaiter().GetResult(), "failed SDK call authorized termination");
            Assert(HostedWidgetExit.CloseAllAsync(new Func<Task<bool>>[0],Task.Delay(1000)).GetAwaiter().GetResult(), "desktop-only exit was blocked");
        });
    }

    private sealed class DesktopWindowPortFake : IDesktopWindowPort
    {
        public bool IsValid = true, IsVisible = true, IsMinimized;
        public int ForeignWrites;
        public readonly List<bool> ShowRequests = new List<bool>(), TaskbarRequests = new List<bool>();
        public Func<bool, Task<bool>> ShowResult;
        public Func<bool, bool> TaskbarResult;
        public bool Valid => IsValid;
        public bool Visible => IsVisible;
        public bool Minimized => IsMinimized;
        public Task<bool> ShowAsync(bool visible)
        {
            if (!IsValid) { ForeignWrites++; throw new InvalidOperationException("foreign frame write"); }
            ShowRequests.Add(visible);
            if (ShowResult != null) return ShowResult(visible);
            IsVisible = visible; if (visible) IsMinimized = false;
            return Task.FromResult(true);
        }
        public bool SetTaskbarVisible(bool visible)
        {
            if (!IsValid) { ForeignWrites++; throw new InvalidOperationException("foreign frame write"); }
            TaskbarRequests.Add(visible);
            return TaskbarResult == null || TaskbarResult(visible);
        }
    }
}
