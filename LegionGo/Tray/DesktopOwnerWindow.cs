// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace LegionTweaks.Tray
{
    // Keep this on the tray STA for the whole desktop session. Do not destroy it
    // until TryRelease has verified detachment. A process crash bypasses cleanup;
    // Windows can then destroy the UWP frame owned by this anchor.
    public sealed class HiddenOwnerAnchor : NativeWindow
    {
        private readonly int threadId = Thread.CurrentThread.ManagedThreadId;
        public HiddenOwnerAnchor()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("An STA tray context is required");
            CreateHandle(new CreateParams {
                Caption = "", Parent = IntPtr.Zero,
                Style = unchecked((int)0x80000000), // WS_POPUP, no WS_VISIBLE/WS_CHILD.
                ExStyle = 0x00000080 | 0x08000000, // TOOLWINDOW | NOACTIVATE.
                Width = 1, Height = 1
            });
        }
        public bool TryRelease(DesktopOwnerLease lease)
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId) return false;
            if (Handle == IntPtr.Zero) return true;
            if (lease == null || lease.Anchor != Handle.ToInt64() || !lease.CanReleaseAnchor()) return false;
            DestroyHandle();
            return Handle == IntPtr.Zero;
        }
        internal void ReleaseUnused()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId)
                throw new InvalidOperationException("The anchor belongs to the tray STA");
            DestroyHandle(); // Only before a lease has ever been constructed/applied.
        }
    }

    // Frame identity survives loss of the CoreWindow only for cleanup. Attaching
    // the anchor additionally requires the original live CoreWindow identity.
    public sealed class NativeWindowOwnerPort : IWindowOwnerPort
    {
        private readonly IntPtr frame, anchor;
        private readonly Func<bool> identityStillMatches, canAttach;
        private readonly uint anchorPid;
        private readonly long anchorProcessStarted;
        private bool originalCaptured;
        private IntPtr original;
        private uint originalPid;
        private long originalProcessStarted;
        public int LastError { get; private set; }
        public string LastStage { get; private set; }
        public NativeWindowOwnerPort(IntPtr frame, HiddenOwnerAnchor anchor,
            Func<bool> identityStillMatches, Func<bool> canAttach)
        {
            if (frame == IntPtr.Zero || anchor == null || anchor.Handle == IntPtr.Zero || frame == anchor.Handle)
                throw new ArgumentException("Two distinct verified windows are required");
            this.frame = frame; this.anchor = anchor.Handle;
            this.identityStillMatches = identityStillMatches ?? throw new ArgumentNullException(nameof(identityStillMatches));
            this.canAttach = canAttach ?? throw new ArgumentNullException(nameof(canAttach));
            GetWindowThreadProcessId(this.anchor, out anchorPid);
            anchorProcessStarted = ProcessStarted(anchorPid);
        }
        public long Anchor => anchor.ToInt64();
        public bool TargetExists => IsWindow(frame);
        public bool Valid {
            get {
                try {
                    uint pid; GetWindowThreadProcessId(anchor, out pid);
                    return IsWindow(frame) && IsWindow(anchor) && pid == anchorPid
                        && ProcessStarted(pid) == anchorProcessStarted && identityStillMatches();
                }
                catch { return false; }
            }
        }
        public bool TryReadOwner(out long owner)
        {
            owner = 0; LastError = 0; LastStage = "read_owner";
            if (!Valid) return false;
            IntPtr value = GetWindow(frame, 4); // GW_OWNER, not GetParent.
            if (!Valid) return false;
            if (!originalCaptured) {
                uint pid = 0; long started = 0;
                if (value != IntPtr.Zero) {
                    if (!IsWindow(value)) return false;
                    GetWindowThreadProcessId(value, out pid);
                    try { started = ProcessStarted(pid); } catch { return false; }
                }
                original = value; originalPid = pid; originalProcessStarted = started;
                originalCaptured = true;
            }
            owner = value.ToInt64(); return true;
        }
        public bool OriginalOwnerValid(long owner)
        {
            if (!originalCaptured || owner != original.ToInt64() || !Valid) return false;
            if (original == IntPtr.Zero) return true;
            uint pid; GetWindowThreadProcessId(original, out pid);
            try { return IsWindow(original) && pid == originalPid && ProcessStarted(pid) == originalProcessStarted; }
            catch { return false; }
        }
        public bool TryWriteOwner(long owner)
        {
            LastError = 0; LastStage = "write_owner";
            if (!Valid || !originalCaptured) return false;
            if (owner == Anchor) { if (!canAttach() || !Valid) return false; }
            else if (!OriginalOwnerValid(owner)) return false;
            // A denied attach can leave the original owner unchanged. Confirm
            // that state instead of issuing a redundant native owner write.
            if (GetWindow(frame, 4).ToInt64() == owner) return Valid;
            // No SetParent, WS_CHILD, WndProc change, or arbitrary owner supplied
            // through IPC. Zero is allowed only if that was the captured owner.
            ClearLastError(0);
            IntPtr previous = SetWindowLongPtrW(frame, -8, new IntPtr(owner));
            int error = Marshal.GetLastWin32Error();
            if (previous == IntPtr.Zero && error != 0) { LastError = error; return false; }
            return Valid && GetWindow(frame, 4).ToInt64() == owner;
        }
        private static long ProcessStarted(uint pid)
        {
            if (pid == 0) throw new InvalidOperationException("Missing window process");
            using (var process = Process.GetProcessById(checked((int)pid))) return process.StartTime.ToUniversalTime().Ticks;
        }
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr window, int index, IntPtr value);
        [DllImport("kernel32.dll", EntryPoint = "SetLastError", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void ClearLastError(uint value);
    }
}
