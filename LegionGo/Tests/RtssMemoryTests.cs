// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LegionTweaks;

internal static partial class Program
{
    private static void RtssMemoryTests()
    {
        Test("RTSS signature and offsets follow the little-endian DWORD ABI", () =>
        {
            var memory = new RtssFakeMemory();
            Assert(memory.Data[0] == 0x53 && memory.Data[1] == 0x53 && memory.Data[2] == 0x54 && memory.Data[3] == 0x52, "signature bytes differ from MSVC 'RTSS'");
            var result = RtssMemoryProtocol.Publish(memory, "LegionTweaks\nGPU 52 C");
            Assert(result.Published && result.State == "published" && !result.CleanupPending, "valid ABI rejected");
            Assert(memory.Text(1, 256) == RtssMemoryProtocol.Owner && memory.Text(1, 0) == "LegionTweaks\nGPU 52 C" && memory.Text(1, 512) == "LegionTweaks\nGPU 52 C", "OSD fields differ");
            Assert(memory.ReadUInt32(32) == 1 && memory.ReadUInt32(36) == 0, "frame or busy offset differs");
        });
        Test("RTSS publishing prefers its existing exact owner over an earlier empty slot", () =>
        {
            var memory = new RtssFakeMemory(); memory.SetText(2, 256, RtssMemoryProtocol.Owner);
            memory.SetText(0, 256, "RTSS"); memory.SetText(0, 512, "reserved");
            var before = memory.Data.Take(memory.Entry(1)).ToArray();
            Assert(RtssMemoryProtocol.Publish(memory, "GPU").Published, "own slot rejected");
            Assert(memory.Text(1, 256) == "" && memory.Text(2, 512) == "GPU", "claimed a second slot");
            Assert(before.Skip(40).SequenceEqual(memory.Data.Skip(40).Take(before.Length - 40)), "slot zero modified");
        });
        Test("RTSS exact owner matching rejects prefixes and unterminated owners", () =>
        {
            foreach (string owner in new[] { RtssMemoryProtocol.Owner + ".other", "OtherApp" })
            {
                var memory = new RtssFakeMemory(2); memory.SetText(1, 256, owner);
                var original = memory.Data.ToArray();
                var result = RtssMemoryProtocol.Publish(memory, "GPU");
                Assert(result.State == "full" && !result.Published && memory.Writes == 0, "foreign owner claimed");
                Assert(original.SequenceEqual(memory.Data), "foreign memory changed");
            }
            var noTerminator = new RtssFakeMemory(2);
            for (int i = 0; i < 256; i++) noTerminator.Data[noTerminator.Entry(1) + 256 + i] = (byte)'X';
            Assert(RtssMemoryProtocol.Publish(noTerminator, "GPU").State == "full" && noTerminator.Writes == 0, "unterminated owner reclaimed");
        });
        Test("RTSS publishes only text and owner fields without erasing graphics or foreign slots", () =>
        {
            var memory = new RtssFakeMemory(3, 8192);
            memory.SetText(2, 256, "Another OSD"); memory.SetText(2, 512, "Keep me");
            for (int i = 4608; i < memory.EntrySize; i++) memory.Data[memory.Entry(1) + i] = 0xA5;
            byte[] foreign = memory.Data.Skip(memory.Entry(2)).Take(memory.EntrySize).ToArray();
            Assert(RtssMemoryProtocol.Publish(memory, new string('A', 4095)).Published, "bounded text rejected");
            Assert(memory.Text(1, 512).Length == 4095 && memory.Text(1, 0).Length == 255, "text bounds changed");
            Assert(memory.Data[memory.Entry(1) + 512 + 4095] == 0 && memory.Data.Skip(memory.Entry(1) + 4608).Take(memory.EntrySize - 4608).All(x => x == 0xA5), "terminator or graphics overwritten");
            Assert(foreign.SequenceEqual(memory.Data.Skip(memory.Entry(2)).Take(memory.EntrySize)), "foreign slot changed");
            Assert(memory.AppReads == 0, "application entries read");
        });
        Test("RTSS release clears only its exact owners including duplicate stale slots", () =>
        {
            var memory = new RtssFakeMemory(4);
            foreach (int slot in new[] { 0, 1, 3 }) { memory.SetText(slot, 256, RtssMemoryProtocol.Owner); memory.SetText(slot, 0, "old"); memory.SetText(slot, 512, "old extended"); }
            memory.SetText(2, 256, "foreign"); memory.SetText(2, 512, "keep");
            var result = RtssMemoryProtocol.Release(memory);
            Assert(result.State == "released" && !result.CleanupPending && !result.Published, "release failed");
            foreach (int slot in new[] { 1, 3 }) Assert(memory.Text(slot, 256) == "" && memory.Text(slot, 0) == "" && memory.Text(slot, 512) == "", "own text retained");
            Assert(memory.Text(0, 256) == RtssMemoryProtocol.Owner && memory.Text(0, 512) == "old extended" && memory.Text(2, 512) == "keep", "reserved or foreign slot cleared");
            Assert(memory.ReadUInt32(32) == 2, "release update count differs");
        });
        Test("RTSS release never claims an empty slot or reports it as published", () =>
        {
            var memory = new RtssFakeMemory();
            Assert(RtssMemoryProtocol.Release(memory).State == "released" && memory.Writes == 0 && memory.ReadUInt32(32) == 0, "release claimed an OSD");
            Assert(RtssMemoryProtocol.Release(null).State == "missing", "missing mapping not tolerated");
        });
        Test("RTSS text rejects Unicode control bytes null and oversized values without touching memory", () =>
        {
            foreach (string text in new[] { null, "G\0PU", "GPU\t52", "GPU\r52", "52 °C", new string('A', 4096) })
            {
                var memory = new RtssFakeMemory(); var result = RtssMemoryProtocol.Publish(memory, text);
                Assert(result.State == "error" && memory.Reads == 0 && memory.CasCalls == 0 && memory.Writes == 0, "invalid text touched shared memory");
            }
        });
        Test("RTSS SDK 2.21 publishes and retires its OSD while preserving application records and foreign slots", () =>
        {
            // ABI dimensions derived from the SDK, not from a user's shared-memory capture.
            // The v2.20 app prefix ends after resolution; v2.21 adds 13 timestamps
            // and 2 DWORDs before 256 three-DWORD performance-counter descriptors.
            const int headerSize = 96, osdSize = 2 * 256 + 4096 + 262144 + 32768;
            const int appSize = 9232 + 13 * 8 + 2 * 4 + 256 * (3 * 4);
            var memory = new RtssFakeMemory(8, osdSize, headerSize, appSize, 256);
            memory.SetUInt32(4, 0x00020015);
            for (int i = 40; i < headerSize; i++) memory.Data[i] = 0xA7;
            memory.SetText(0, 256, "SDK primary OSD"); memory.SetText(0, 512, "reserved text");
            for (int slot = 2; slot < 8; slot++)
            {
                memory.SetText(slot, 256, "Synthetic foreign " + slot);
                memory.SetText(slot, 512, "Keep foreign OSD " + slot);
            }
            for (int i = memory.Entry(1) + 4608; i < memory.Entry(2); i++) memory.Data[i] = 0xC3;
            int appOffset = memory.Entry(8);
            for (int i = appOffset; i < memory.Data.Length; i++) memory.Data[i] = (byte)((i * 17 + 11) & 255);
            byte[] headerExtension = memory.Data.Skip(40).Take(headerSize - 40).ToArray();
            byte[] slotZero = memory.Data.Skip(memory.Entry(0)).Take(osdSize).ToArray();
            byte[] foreignAndApps = memory.Data.Skip(memory.Entry(2)).ToArray();

            var published = RtssMemoryProtocol.Publish(memory, "LEGIONTWEAKS\nGPU 52 C");
            Assert(published.Published && published.State == "published" && !published.CleanupPending, "SDK 2.21 rejected");
            Assert(memory.Text(1, 256) == RtssMemoryProtocol.Owner && memory.Text(1, 512) == "LEGIONTWEAKS\nGPU 52 C", "wrong OSD claimed");
            Assert(slotZero.SequenceEqual(memory.Data.Skip(memory.Entry(0)).Take(osdSize)) && foreignAndApps.SequenceEqual(memory.Data.Skip(memory.Entry(2))), "publication changed reserved, foreign or app data");
            Assert(memory.AppReads == 0 && memory.ReadUInt32(32) == 1 && memory.ReadUInt32(36) == 0, "application records read or update semantics changed");

            var released = RtssMemoryProtocol.Release(memory);
            Assert(released.State == "released" && !released.Published && !released.CleanupPending
                && memory.Text(1, 256) == "" && memory.Text(1, 0) == "" && memory.Text(1, 512) == "", "SDK 2.21 cleanup failed");
            Assert(headerExtension.SequenceEqual(memory.Data.Skip(40).Take(headerSize - 40))
                && slotZero.SequenceEqual(memory.Data.Skip(memory.Entry(0)).Take(osdSize))
                && foreignAndApps.SequenceEqual(memory.Data.Skip(memory.Entry(2))), "cleanup changed unrelated SDK fields");
            Assert(memory.Data.Skip(memory.Entry(1) + 4608).Take(osdSize - 4608).All(x => x == 0xC3), "graphics or extended-2 buffer erased");
            Assert(memory.AppReads == 0 && memory.ReadUInt32(32) == 2 && memory.ReadUInt32(36) == 0, "cleanup read app entries or retained busy bit");
        });
        Test("RTSS ABI rejects old major old lock dead and invalid signatures before any writes", () =>
        {
            foreach (uint version in new uint[] { 0, 0x00010014, 0x0002000d, 0x00020016, 0x00030000 })
            {
                var memory = new RtssFakeMemory(); memory.SetUInt32(4, version);
                Assert(RtssMemoryProtocol.Publish(memory, "GPU").State == "incompatible" && memory.CasCalls == 0 && memory.Writes == 0, "incompatible version locked");
                Assert(RtssMemoryProtocol.Release(memory).State == "incompatible" && memory.CasCalls == 0 && memory.Writes == 0, "cleanup changed an incompatible version");
            }
            foreach (uint signature in new uint[] { 0, 0x53535452, 0xDEAD })
            {
                var memory = new RtssFakeMemory(); memory.SetUInt32(0, signature);
                Assert(RtssMemoryProtocol.Publish(memory, "GPU").State == (signature == 0xDEAD ? "missing" : "incompatible") && memory.CasCalls == 0, "bad signature accepted");
            }
        });
        Test("RTSS dynamic arrays cannot overlap any known version-specific header extension", () =>
        {
            foreach (var pair in new[] { Tuple.Create(0x0002000eu, 40u), Tuple.Create(0x0002000fu, 64u),
                Tuple.Create(0x00020010u, 72u), Tuple.Create(0x00020011u, 72u),
                Tuple.Create(0x00020012u, 80u), Tuple.Create(0x00020013u, 96u), Tuple.Create(0x00020014u, 96u), Tuple.Create(0x00020015u, 96u) })
            {
                var valid = new RtssFakeMemory(); valid.SetUInt32(4, pair.Item1);
                Assert(RtssMemoryProtocol.Publish(valid, "GPU").Published, "known lock-compatible version rejected");
                var invalid = new RtssFakeMemory(); invalid.SetUInt32(4, pair.Item1); invalid.SetUInt32(24, pair.Item2 - 4);
                Assert(RtssMemoryProtocol.Publish(invalid, "GPU").State == "incompatible" && invalid.Writes == 0 && invalid.CasCalls == 0, "OSD overlaps an extended header");
                invalid = new RtssFakeMemory(); invalid.SetUInt32(4, pair.Item1); invalid.SetUInt32(12, pair.Item2 - 4);
                Assert(RtssMemoryProtocol.Publish(invalid, "GPU").State == "incompatible" && invalid.CasCalls == 0, "app array overlaps extended header");
            }
        });
        Test("RTSS ABI rejects overflow bounds counts overlaps and misaligned ranges", () =>
        {
            var changes = new List<Action<RtssFakeMemory>>
            {
                m => m.AdvertisedLength = 39, m => m.AdvertisedLength = 64L * 1024 * 1024 + 1,
                m => m.SetUInt32(24, uint.MaxValue), m => m.SetUInt32(20, uint.MaxValue), m => m.SetUInt32(28, uint.MaxValue),
                m => m.SetUInt32(24, 36), m => m.SetUInt32(24, 65), m => m.SetUInt32(20, 4604),
                m => m.SetUInt32(20, 4609), m => m.SetUInt32(28, 1), m => m.SetUInt32(28, 257),
                m => m.SetUInt32(12, 64), m => m.SetUInt32(12, 32), m => m.SetUInt32(12, uint.MaxValue),
                m => m.SetUInt32(8, uint.MaxValue), m => m.SetUInt32(8, 3), m => m.SetUInt32(16, 4097),
                m => m.SetUInt32(8, 1024 * 1024), m => m.SetUInt32(16, uint.MaxValue),
                m => m.SetUInt32(12, (uint)m.Data.Length - 1)
            };
            foreach (var change in changes)
            {
                var memory = new RtssFakeMemory(); change(memory);
                var result = RtssMemoryProtocol.Publish(memory, "GPU");
                Assert(result.State == "incompatible" && memory.CasCalls == 0 && memory.Writes == 0 && memory.AppReads == 0, "invalid extent reached the writer");
            }
        });
        Test("RTSS ABI accepts empty app tables without reading application entries", () =>
        {
            var memory = new RtssFakeMemory(); memory.SetUInt32(8, 0); memory.SetUInt32(12, 0); memory.SetUInt32(16, 0);
            Assert(RtssMemoryProtocol.Publish(memory, "GPU").Published && memory.AppReads == 0, "empty app table rejected");
        });
        Test("RTSS snapshot is revalidated under the lock before ownership writes", () =>
        {
            foreach (int offset in new[] { 0, 4, 8, 12, 16, 20, 24, 28 })
            {
                var memory = new RtssFakeMemory(); memory.AfterAcquire = m => m.SetUInt32(offset, m.ReadUInt32(offset) + 4);
                var result = RtssMemoryProtocol.Publish(memory, "GPU");
                Assert(result.State == "incompatible" && memory.Writes == 0 && (memory.ReadUInt32(36) & 1) == 0, "mutated layout used");
            }
        });
        Test("RTSS busy producer is not waited for and unrelated flag bits are preserved", () =>
        {
            var busy = new RtssFakeMemory(); busy.SetUInt32(36, 5);
            Assert(RtssMemoryProtocol.Publish(busy, "GPU").State == "busy" && busy.CasCalls == 0 && busy.ReadUInt32(36) == 5 && busy.Writes == 0, "busy lock changed");
            Assert(RtssMemoryProtocol.Release(busy).CleanupPending, "busy release lost pending cleanup");
            var flags = new RtssFakeMemory(); flags.SetUInt32(36, 4); flags.AfterAcquire = m => m.SetUInt32(36, 13);
            Assert(RtssMemoryProtocol.Publish(flags, "GPU").Published && flags.ReadUInt32(36) == 12, "other lock bits clobbered");
        });
        Test("RTSS lock acquisition races have bounded retries and never write OSD data", () =>
        {
            var memory = new RtssFakeMemory { FailAcquire = true };
            Assert(RtssMemoryProtocol.Publish(memory, "GPU").State == "busy" && memory.CasCalls == 8 && memory.Writes == 0, "lock retries are unbounded");
        });
        Test("RTSS lock release races have bounded retries and report pending cleanup explicitly", () =>
        {
            var memory = new RtssFakeMemory { FailRelease = true }; memory.SetUInt32(36, 8);
            var result = RtssMemoryProtocol.Publish(memory, "GPU");
            Assert(result.State == "error" && !result.Published && result.CleanupPending && result.Error != null, "failed unlock reported success");
            Assert(memory.CasCalls == 65 && memory.ReadUInt32(36) == 9, "unlock waited indefinitely or clobbered flags");
        });
        Test("RTSS write failures release bit zero and preserve a recoverable owner", () =>
        {
            var memory = new RtssFakeMemory(); memory.SetUInt32(36, 8); memory.FailWrite = 25;
            var result = RtssMemoryProtocol.Publish(memory, "GPU 52 C");
            Assert(result.State == "error" && result.CleanupPending && memory.ReadUInt32(36) == 8, "failed write retained global lock");
            Assert(memory.Text(1, 256) == RtssMemoryProtocol.Owner, "failed write lost owner");
            memory.FailWrite = 0;
            Assert(RtssMemoryProtocol.Release(memory).State == "released" && memory.Text(1, 256) == "", "failed publish could not be cleaned up");
        });
        Test("RTSS release failure keeps ownership available for a subsequent cleanup", () =>
        {
            var memory = new RtssFakeMemory(); Assert(RtssMemoryProtocol.Publish(memory, "GPU").Published, "setup");
            memory.FailWrite = memory.Writes + 2;
            Assert(RtssMemoryProtocol.Release(memory).CleanupPending && memory.Text(1, 256) == RtssMemoryProtocol.Owner && memory.ReadUInt32(36) == 0, "release failure lost recovery");
            memory.FailWrite = 0; Assert(!RtssMemoryProtocol.Release(memory).CleanupPending && memory.Text(1, 256) == "", "retry cleanup failed");
        });
        Test("RTSS frame counter wraps as a DWORD while remaining under the lock", () =>
        {
            var memory = new RtssFakeMemory(); memory.SetUInt32(32, uint.MaxValue);
            Assert(RtssMemoryProtocol.Publish(memory, "GPU").Published && memory.ReadUInt32(32) == 0 && memory.FrameOutsideLock == 0, "frame semantics differ");
        });
        Test("RTSS transport reopens and disposes its view on every operation and server restart", () =>
        {
            var first = new RtssFakeMemory(); var second = new RtssFakeMemory(); var queue = new Queue<IRtssMemoryView>(new[] { first, second, second });
            using (var transport = new RtssMemoryTransport(() => queue.Dequeue()))
            {
                Assert(transport.Publish("first").Published && first.Disposals == 1, "first mapping persisted");
                first.SetUInt32(0, 0xDEAD);
                Assert(transport.Publish("second").Published && second.Disposals == 1 && first.Text(1, 512) == "first", "restart reused the old view");
                Assert(transport.Release().State == "released" && second.Disposals == 2 && second.Text(1, 256) == "", "explicit cleanup did not target the current server");
            }
            Assert(second.Disposals == 2 && queue.Count == 0, "dispose performed unbudgeted I/O");
        });
        Test("RTSS disposing an OFF transport and later calls never open a mapping", () =>
        {
            int opens = 0; var transport = new RtssMemoryTransport(() => { opens++; return new RtssFakeMemory(); });
            transport.Dispose(); transport.Dispose();
            Assert(transport.Release().State == "error" && transport.Publish("GPU").State == "error" && opens == 0, "disposed transport accessed shared memory");
        });
        Test("RTSS transport distinguishes absent memory from sanitized access failures", () =>
        {
            using (var missing = new RtssMemoryTransport(() => { throw new FileNotFoundException("private path"); }))
                Assert(missing.Publish("GPU").State == "missing" && missing.Release().State == "missing", "missing server became error");
            using (var denied = new RtssMemoryTransport(() => { throw new UnauthorizedAccessException("private path"); }))
            {
                var result = denied.Release();
                Assert(result.State == "error" && result.CleanupPending && !result.Error.Contains("private"), "exception leaked or pending cleanup lost");
            }
        });
        Test("RTSS exceptions disposing an operation view are contained and marked as errors", () =>
        {
            var memory = new RtssFakeMemory { FailDispose = true };
            using (var transport = new RtssMemoryTransport(() => memory))
            {
                var result = transport.Publish("GPU");
                Assert(result.State == "error" && result.CleanupPending && memory.ReadUInt32(36) == 0, "view-disposal exception escaped or ownership lost");
            }
        });
    }

    private sealed class RtssFakeMemory : IRtssMemoryView
    {
        internal readonly byte[] Data;
        internal readonly int EntrySize;
        internal long AdvertisedLength;
        internal int Writes, Reads, CasCalls, AppReads, Disposals, FailWrite, FrameOutsideLock;
        internal bool FailAcquire, FailRelease, FailDispose;
        internal Action<RtssFakeMemory> AfterAcquire;
        private readonly int appOffset, osdOffset;
        internal RtssFakeMemory(int count = 3, int entrySize = 4608, int osdOffset = 128, int appEntrySize = 64, int appCount = 1)
        {
            EntrySize = entrySize; this.osdOffset = osdOffset; appOffset = osdOffset + count * entrySize;
            Data = new byte[appOffset + appEntrySize * appCount]; AdvertisedLength = Data.Length;
            SetUInt32(0, RtssMemoryProtocol.Signature); SetUInt32(4, 0x00020014);
            SetUInt32(8, (uint)appEntrySize); SetUInt32(12, (uint)appOffset); SetUInt32(16, (uint)appCount);
            SetUInt32(20, (uint)entrySize); SetUInt32(24, (uint)osdOffset); SetUInt32(28, (uint)count);
        }
        public long Length { get { return AdvertisedLength; } }
        internal int Entry(int slot) { return osdOffset + slot * EntrySize; }
        internal string Text(int slot, int field)
        {
            int start = Entry(slot) + field, count = 0;
            while (start + count < Data.Length && Data[start + count] != 0) count++;
            return Encoding.ASCII.GetString(Data, start, count);
        }
        internal void SetText(int slot, int field, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text); Buffer.BlockCopy(bytes, 0, Data, Entry(slot) + field, bytes.Length); Data[Entry(slot) + field + bytes.Length] = 0;
        }
        internal void SetUInt32(long offset, uint value)
        {
            for (int i = 0; i < 4; i++) Data[(int)offset + i] = (byte)(value >> (8 * i));
        }
        public byte ReadByte(long offset) { Check(offset, 1); Reads++; return Data[(int)offset]; }
        public uint ReadUInt32(long offset)
        {
            Check(offset, 4); Reads++;
            return (uint)(Data[(int)offset] | Data[(int)offset + 1] << 8 | Data[(int)offset + 2] << 16 | Data[(int)offset + 3] << 24);
        }
        public void WriteByte(long offset, byte value)
        {
            Check(offset, 1); Writes++;
            if (Writes == FailWrite) throw new IOException("simulated private failure");
            Data[(int)offset] = value;
        }
        public int CompareExchangeInt32(long offset, int value, int comparand)
        {
            Check(offset, 4); CasCalls++; int old = unchecked((int)ReadUInt32(offset));
            if (FailAcquire && (value & 1) != 0) return old ^ 4;
            if (FailRelease && (old & 1) != 0 && (value & 1) == 0) return old ^ 4;
            if (old == comparand)
            {
                SetUInt32(offset, unchecked((uint)value));
                if ((old & 1) == 0 && (value & 1) != 0) AfterAcquire?.Invoke(this);
            }
            return old;
        }
        public int IncrementInt32(long offset)
        {
            Check(offset, 4); if ((ReadUInt32(36) & 1) == 0) FrameOutsideLock++;
            uint value = unchecked(ReadUInt32(offset) + 1); SetUInt32(offset, value); return unchecked((int)value);
        }
        private void Check(long offset, int length)
        {
            if (offset < 0 || offset > Data.Length - length) throw new IndexOutOfRangeException();
            if (offset >= appOffset) AppReads++;
        }
        public void Dispose() { Disposals++; if (FailDispose) throw new IOException("private disposal failure"); }
    }
}
