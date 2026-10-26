// SPDX-License-Identifier: AGPL-3.0-only
using LegionTweaks;
using System;
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
        Test("Tray restore preserves a reusable desktop without duplicate hiding", () => {
            var state = new DesktopTrayState(); state.Connected(); state.BeginHide(); state.EndHide(true);
            Assert(!state.BeginHide() && state.Hidden, "already hidden desktop consolidated twice");
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
}
