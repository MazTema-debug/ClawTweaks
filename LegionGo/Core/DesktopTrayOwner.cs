// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    public interface IWindowOwnerPort
    {
        bool Valid { get; }
        bool TargetExists { get; }
        long Anchor { get; }
        bool TryReadOwner(out long owner);
        bool OriginalOwnerValid(long owner);
        bool TryWriteOwner(long owner);
    }

    // This changes the OWNER of a top-level window, not its parent.
    // Keep the same identity-bound port, original-owner cookie and lease for the
    // entire desktop session. Serialize calls on the native owning context.
    // The anchor must remain alive while CanReleaseAnchor returns false.
    // This cannot guarantee survival of the UWP target after a hard tray crash:
    // Windows can destroy owned windows when their owner window is destroyed.
    public sealed class DesktopOwnerLease
    {
        private readonly IWindowOwnerPort port;
        private bool captured;
        private long original;
        public long Anchor { get; }
        public bool Pending { get; private set; }

        public DesktopOwnerLease(IWindowOwnerPort port)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            this.port = port;
            Anchor = port.Anchor;
        }

        public bool Apply()
        {
            long current, anchor;
            if (!Read(out current) || !GetAnchor(out anchor) || !Valid()) return false;
            if (!captured) { original = current; captured = true; }
            if (current != original && current != anchor) {
                Pending = true; // A third party changed ownership. Do not overwrite it.
                return false;
            }
            // A failing native write can nevertheless have changed the owner.
            Pending = true;
            try {
                if (Valid() && port.TryWriteOwner(anchor) && Matches(anchor)) return true;
            }
            catch (Exception) { }
            Restore();
            return false;
        }

        public bool Restore()
        {
            if (!Pending) return true;
            long current, anchor;
            if (!captured || !Read(out current) || !GetAnchor(out anchor)) return false;
            if (current != original && current != anchor) return false;
            try {
                // In particular, do not replace a deleted nonzero original owner
                // with an invented null owner, or reuse its HWND for another owner.
                if (Valid() && port.OriginalOwnerValid(original) && Valid()
                    && port.TryWriteOwner(original) && Matches(original)) {
                    Pending = false;
                    return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        public bool CanReleaseAnchor()
        {
            try {
                // A destroyed target needs no owner write, even if Valid is false.
                if (!port.TargetExists) { Pending = false; return true; }
            }
            catch (Exception) { return false; }
            if (!Restore()) return false;
            long current, anchor;
            if (!Read(out current) || !GetAnchor(out anchor)) return false;
            if (current == anchor) { Pending = true; return false; }
            return true;
        }

        private bool Matches(long expected)
        {
            long current;
            return Read(out current) && current == expected;
        }
        private bool Read(out long owner)
        {
            owner = 0;
            try { return Valid() && port.TryReadOwner(out owner) && Valid(); }
            catch (Exception) { return false; }
        }
        private bool GetAnchor(out long anchor)
        {
            anchor = Anchor;
            return Valid();
        }
        private bool Valid()
        {
            try { return Anchor != 0 && port.Anchor == Anchor && port.Valid; }
            catch (Exception) { return false; }
        }
    }

    // Owner/Shell transaction. The caller owns visibility and must
    // consult lease.CanReleaseAnchor before destroying its matching anchor.
    // No window handle can be substituted through these operation callbacks.
    public sealed class OwnerTaskbarAction
    {
        private readonly DesktopOwnerLease lease;
        private readonly Func<bool> sameFrame, isHidden;
        private readonly Func<bool, int> shellRequest;

        public OwnerTaskbarAction(DesktopOwnerLease lease, Func<bool> sameFrame,
            Func<bool> isHidden, Func<bool, int> shellRequest)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));
            if (sameFrame == null) throw new ArgumentNullException(nameof(sameFrame));
            if (isHidden == null) throw new ArgumentNullException(nameof(isHidden));
            if (shellRequest == null) throw new ArgumentNullException(nameof(shellRequest));
            this.lease = lease; this.sameFrame = sameFrame;
            this.isHidden = isHidden; this.shellRequest = shellRequest;
        }

        public bool SetTaskbarVisible(bool visible)
        {
            if (visible) {
                bool restored = RestoreOwner(), added = false;
                // Even a failed owner restore must not suppress independent Shell
                // recovery, and successful AddTab must not conceal that failure.
                try { if (SameFrame()) added = shellRequest(true) >= 0; }
                catch (Exception) { }
                return restored && added && SameFrame();
            }

            if (!SameFrame()) return false;
            try { if (!isHidden() || !SameFrame()) return false; }
            catch (Exception) { return false; }
            try {
                if (lease.Apply() && SameFrame() && shellRequest(false) >= 0 && SameFrame()) return true;
            }
            catch (Exception) { }
            RestoreOwner();
            return false;
        }

        private bool RestoreOwner()
        {
            try { return SameFrame() && lease.Restore() && SameFrame(); }
            catch (Exception) { return false; }
        }
        private bool SameFrame()
        {
            try { return sameFrame(); }
            catch (Exception) { return false; }
        }
    }
}
