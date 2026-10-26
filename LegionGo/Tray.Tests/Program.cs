// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Windows.Forms;
using LegionTweaks;
using LegionTweaks.Tray;

// Private, never-shown fixture HWNDs only. No UWP discovery, app activation,
// helper/device access, or modification of a user's windows.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try {
            RoundTrip(false);
            RoundTrip(true);
            AttachPermission();
            RetainAnchorOnIdentityLoss();
            Console.WriteLine("4 native owner fixture tests passed (not UWP/taskbar proof).");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void RoundTrip(bool nonzeroOwner)
    {
        using (var original = new FixtureWindow(IntPtr.Zero))
        using (var target = new FixtureWindow(nonzeroOwner ? original.Handle : IntPtr.Zero)) {
            var anchor = new HiddenOwnerAnchor();
            var port = new NativeWindowOwnerPort(target.Handle, anchor, () => true, () => true);
            var lease = new DesktopOwnerLease(port);
            long owner;
            Assert(port.TryReadOwner(out owner) && owner == (nonzeroOwner ? original.Handle.ToInt64() : 0), "original owner missing");
            Assert(lease.Apply() && lease.Apply() && lease.Pending, "native attach failed");
            Assert(port.TryReadOwner(out owner) && owner == anchor.Handle.ToInt64(), "native attach readback failed");
            Assert(lease.Restore() && !lease.Pending && port.TryReadOwner(out owner)
                && owner == (nonzeroOwner ? original.Handle.ToInt64() : 0), "native restore failed");
            Assert(anchor.TryRelease(lease) && target.Alive, "release destroyed the detached fixture");
        }
    }
    private static void AttachPermission()
    {
        using (var target = new FixtureWindow(IntPtr.Zero)) {
            var anchor = new HiddenOwnerAnchor();
            bool liveCore = false;
            var port = new NativeWindowOwnerPort(target.Handle, anchor, () => true, () => liveCore);
            var lease = new DesktopOwnerLease(port);
            bool applied = lease.Apply();
            Assert(!applied && !lease.Pending, "denied attach: applied=" + applied
                + ", pending=" + lease.Pending + ", error=" + port.LastError + ", stage=" + port.LastStage);
            liveCore = true;
            Assert(lease.Apply(), "attach permission not honored");
            liveCore = false;
            Assert(lease.Restore() && !lease.Pending, "cleanup required a closed CoreWindow");
            Assert(anchor.TryRelease(lease) && target.Alive, "cleanup did not permit safe release");
        }
    }
    private static void RetainAnchorOnIdentityLoss()
    {
        using (var target = new FixtureWindow(IntPtr.Zero)) {
            var anchor = new HiddenOwnerAnchor();
            bool same = true;
            var port = new NativeWindowOwnerPort(target.Handle, anchor, () => same, () => true);
            var lease = new DesktopOwnerLease(port);
            Assert(lease.Apply(), "initial attach failed");
            same = false;
            Assert(!anchor.TryRelease(lease) && anchor.Handle != IntPtr.Zero && target.Alive,
                "anchor destroyed while target was attached");
            target.Dispose();
            Assert(anchor.TryRelease(lease) && anchor.Handle == IntPtr.Zero,
                "missing fixture did not allow anchor release");
        }
    }
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private sealed class FixtureWindow : NativeWindow, IDisposable
    {
        internal FixtureWindow(IntPtr owner)
        {
            CreateHandle(new CreateParams { Parent = owner, Style = unchecked((int)0x80000000),
                ExStyle = 0x08000080, Width = 1, Height = 1 });
        }
        internal bool Alive => Handle != IntPtr.Zero;
        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }
}
