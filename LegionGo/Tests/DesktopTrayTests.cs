// SPDX-License-Identifier: AGPL-3.0-only
using LegionTweaks;

internal static partial class Program
{
    private static void DesktopTrayTests()
    {
        Test("Desktop retains ordinary close until a tray connection is ready", () => {
            var state = new DesktopTrayState();
            Assert(!state.InterceptClose && !state.BeginHide() && !state.Hidden, "unrecoverable window could be hidden");
            state.Connected();
            Assert(state.InterceptClose && state.BeginHide(), "ready tray cannot accept close");
            Assert(!state.InterceptClose && !state.BeginHide(), "programmatic consolidation reentered");
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
    }
}
