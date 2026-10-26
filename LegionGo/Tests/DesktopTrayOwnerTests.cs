// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using LegionTweaks;
internal static partial class Program
{
    private static void DesktopTrayOwnerTests()
    {
            Test("a null original owner is restored exactly", () => {
                var port = new OwnerFakePort { Owner = 0 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply() && lease.Pending && port.Owner == port.Anchor, "anchor not applied");
                Assert(lease.Restore() && !lease.Pending && port.Owner == 0, "null original owner not restored");
            });
            Test("a nonzero original owner is restored instead of blindly clearing it", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply() && lease.Restore() && port.Owner == 101 && !lease.Pending, "nonzero owner was lost");
                Assert(port.OriginalChecks.Count == 1 && port.OriginalChecks[0] == 101, "original owner identity was not validated");
                Assert(!port.Writes.Contains(0), "nonzero original owner was replaced with null");
            });
            Test("repeated apply retains the first owner rather than backing up the anchor", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply() && lease.Apply() && lease.Restore() && port.Owner == 101, "anchor replaced original backup");
                Assert(lease.Apply() && lease.Restore() && port.Owner == 101, "later cycle lost original owner");
            });
            Test("a false write after mutation is compensated to the original owner", () => {
                var port = new OwnerFakePort { Owner = 101 };
                int writes = 0;
                port.Write = owner => { port.Owner = owner; return ++writes != 1; };
                var lease = new DesktopOwnerLease(port);
                Assert(!lease.Apply() && port.Owner == 101 && !lease.Pending && writes == 2, "partial write was not compensated");
            });
            Test("a throwing write after mutation also restores the original owner", () => {
                var port = new OwnerFakePort { Owner = 101 };
                int writes = 0;
                port.Write = owner => {
                    port.Owner = owner;
                    if (++writes == 1) throw new InvalidOperationException();
                    return true;
                };
                var lease = new DesktopOwnerLease(port);
                Assert(!lease.Apply() && port.Owner == 101 && !lease.Pending && writes == 2, "throwing write stranded ownership");
            });
            Test("apply requires readback instead of trusting a successful write", () => {
                var port = new OwnerFakePort { Owner = 101 };
                int writes = 0;
                port.Write = owner => { if (++writes != 1) port.Owner = owner; return true; };
                var lease = new DesktopOwnerLease(port);
                Assert(!lease.Apply() && port.Owner == 101 && !lease.Pending && writes == 2, "false readback counted as applied");
            });
            Test("failed restoration readback retains recovery state for retry", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                port.Write = owner => true;
                Assert(!lease.Restore() && lease.Pending && port.Owner == port.Anchor, "failed readback cleared pending state");
                port.Write = null;
                Assert(lease.Restore() && !lease.Pending && port.Owner == 101, "retry failed to use original owner");
            });
            Test("an invalid initial target is never read or mutated", () => {
                var port = new OwnerFakePort { IsValid = false };
                var lease = new DesktopOwnerLease(port);
                Assert(!lease.Apply() && !lease.Pending && port.Reads == 0 && port.Writes.Count == 0
                    && port.ForeignAccesses == 0, "foreign target was accessed");
            });
            Test("identity loss after a write prevents foreign rollback and anchor release", () => {
                var port = new OwnerFakePort { Owner = 101 };
                port.Write = owner => { port.Owner = owner; port.IsValid = false; return false; };
                var lease = new DesktopOwnerLease(port);
                Assert(!lease.Apply() && lease.Pending && port.Writes.Count == 1, "lost identity was counted as applied");
                Assert(!lease.Restore() && !lease.CanReleaseAnchor() && lease.Pending
                    && port.Writes.Count == 1 && port.ForeignAccesses == 0, "foreign frame was changed or its anchor released");
            });
            Test("a third party owner is not overwritten by apply or restore", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                port.Owner = 303;
                Assert(!lease.Apply() && !lease.Restore() && !lease.CanReleaseAnchor() && lease.Pending,
                    "conflicting ownership was silently accepted");
                Assert(port.Owner == 303 && port.Writes.Count == 1, "third party owner was overwritten");
            });
            Test("the lease retains its original anchor and rejects a changed port anchor", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Anchor == 202 && lease.Apply(), "anchor was not captured once");
                port.AnchorValue = 404;
                Assert(lease.Anchor == 202 && !lease.Apply() && !lease.Restore() && !lease.CanReleaseAnchor()
                    && lease.Pending && port.Writes.Count == 1 && port.ForeignAccesses == 0,
                    "changed anchor was adopted or allowed foreign recovery writes");
            });
            Test("a deleted original owner is not substituted with null or a reused handle", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                port.OriginalExists = false;
                Assert(!lease.Restore() && !lease.CanReleaseAnchor() && lease.Pending
                    && port.Owner == port.Anchor && port.Writes.Count == 1, "deleted original owner was reused or invented");
            });
            Test("an anchor cannot be released while failed recovery leaves it attached", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                port.Write = owner => false;
                Assert(!lease.CanReleaseAnchor() && lease.Pending && port.Owner == port.Anchor,
                    "attached anchor was released after a failed restore");
                port.Write = null;
                Assert(lease.CanReleaseAnchor() && !lease.Pending && port.Owner == 101,
                    "verified restored owner did not permit release");
            });
            Test("a destroyed target permits anchor release without window writes", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                int writes = port.Writes.Count, reads = port.Reads;
                port.Exists = false; port.IsValid = false;
                Assert(lease.CanReleaseAnchor() && !lease.Pending && port.Reads == reads
                    && port.Writes.Count == writes && port.ForeignAccesses == 0, "destroyed target received recovery writes");
            });
            Test("taskbar removal requires both a hidden frame and matching identity", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                bool hidden = false, same = true;
                int shells = 0;
                var action = new OwnerTaskbarAction(lease, () => same, () => hidden, visible => { shells++; return 0; });
                Assert(!action.SetTaskbarVisible(false), "visible frame was accepted");
                hidden = true; same = false;
                Assert(!action.SetTaskbarVisible(false) && port.Writes.Count == 0 && shells == 0 && !lease.Pending,
                    "unhidden or foreign frame was mutated");
            });
            Test("taskbar removal and addition require their complete owner and Shell transactions", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                var shells = new List<bool>();
                var action = new OwnerTaskbarAction(lease, () => true, () => true,
                    visible => { shells.Add(visible); return 0; });
                Assert(action.SetTaskbarVisible(false) && port.Owner == port.Anchor && lease.Pending,
                    "successful hide transaction failed");
                Assert(action.SetTaskbarVisible(true) && port.Owner == 101 && !lease.Pending,
                    "successful restore transaction failed");
                Assert(shells.Count == 2 && !shells[0] && shells[1], "Shell remove/add sequence was not requested");
            });
            Test("failed or throwing DeleteTab restores the exact original owner", () => {
                foreach (bool throws in new[] { false, true }) {
                    var port = new OwnerFakePort { Owner = 101 };
                    var lease = new DesktopOwnerLease(port);
                    int shells = 0;
                    var action = new OwnerTaskbarAction(lease, () => true, () => true, visible => {
                        Assert(!visible, "failed removal unexpectedly requested AddTab");
                        shells++;
                        if (throws) throw new InvalidOperationException();
                        return -1;
                    });
                    Assert(!action.SetTaskbarVisible(false) && shells == 1 && port.Owner == 101
                        && !lease.Pending && port.Writes.Count == 2, "failed Shell removal stranded the anchor owner");
                }
            });
            Test("failed AddTab reports failure while still restoring the owner", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                int shells = 0;
                var action = new OwnerTaskbarAction(lease, () => true, () => true, visible => {
                    Assert(visible, "restore requested DeleteTab"); shells++; return -1;
                });
                Assert(!action.SetTaskbarVisible(true) && shells == 1 && port.Owner == 101 && !lease.Pending,
                    "Shell failure skipped owner recovery or was reported as success");
            });
            Test("successful AddTab cannot hide a failed owner restore", () => {
                var port = new OwnerFakePort { Owner = 101 };
                var lease = new DesktopOwnerLease(port);
                Assert(lease.Apply(), "apply failed");
                port.OriginalExists = false;
                int shells = 0;
                var action = new OwnerTaskbarAction(lease, () => true, () => true, visible => {
                    Assert(visible, "restore requested DeleteTab"); shells++; return 0;
                });
                Assert(!action.SetTaskbarVisible(true) && shells == 1 && port.Owner == port.Anchor && lease.Pending,
                    "AddTab acknowledgment concealed an unrecovered owner");
            });
    }
    private sealed class OwnerFakePort : IWindowOwnerPort
    {
        public bool IsValid = true, Exists = true, OriginalExists = true;
        public long Owner;
        public long AnchorValue = 202;
        public int Reads, ForeignAccesses;
        public readonly List<long> Writes = new List<long>(), OriginalChecks = new List<long>();
        public Func<long, bool> Write;
        public bool Valid { get { return IsValid; } }
        public bool TargetExists { get { return Exists; } }
        public long Anchor { get { return AnchorValue; } }
        public bool TryReadOwner(out long owner)
        { EnsureValid(); Reads++; owner = Owner; return true; }
        public bool OriginalOwnerValid(long owner)
        { EnsureValid(); OriginalChecks.Add(owner); return owner == 0 || OriginalExists; }
        public bool TryWriteOwner(long owner)
        {
            EnsureValid(); Writes.Add(owner);
            if (Write != null) return Write(owner);
            Owner = owner; return true;
        }
        private void EnsureValid()
        {
            if (!IsValid) { ForeignAccesses++; throw new InvalidOperationException("foreign identity access"); }
        }
    }
}
