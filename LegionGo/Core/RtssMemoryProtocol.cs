// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    // Small byte-oriented seam: tests never open RTSS or touch a native mapping.
    public interface IRtssMemoryView : IDisposable
    {
        long Length { get; }
        byte ReadByte(long offset);
        uint ReadUInt32(long offset);
        void WriteByte(long offset, byte value);
        int CompareExchangeInt32(long offset, int value, int comparand);
        int IncrementInt32(long offset);
    }

    // Original implementation of the published RTSS v2 shared-memory ABI.
    // No app entries, hooks, embedded graphics or RTSS configuration are accessed.
    public static class RtssMemoryProtocol
    {
        // The SDK's MSVC multicharacter literal 'RTSS', stored as 53 53 54 52 on x86/x64.
        public const uint Signature = 0x52545353;
        public const string Owner = "LegionTweaks.Z1E";
        public const int MaximumTextLength = 4095;
        private const long MaximumMappingLength = 64L * 1024 * 1024;
        private const uint MinimumVersion = 0x0002000e;
        // SDK v2.21 extends only application records; this publisher never reads them.
        private const uint MaximumVersion = 0x00020015;
        private const int HeaderLength = 40, BusyOffset = 36, FrameOffset = 32;
        private const int OwnerOffset = 256, ExtendedOffset = 512, ExtendedLength = 4096;

        public static RtssPublishResult Publish(IRtssMemoryView memory, string text)
        {
            if (!ValidText(text)) return Result("error", false, false, "El texto del OSD no es ASCII válido o supera su límite.");
            return Execute(memory, text, false);
        }

        public static RtssPublishResult Release(IRtssMemoryView memory)
        {
            return Execute(memory, null, true);
        }

        private static RtssPublishResult Execute(IRtssMemoryView memory, string text, bool release)
        {
            bool locked = false, mayOwn = false;
            RtssPublishResult result = null;
            try
            {
                if (memory == null) return result = Result("missing", false, false, null);
                if (memory.Length < HeaderLength || memory.Length > MaximumMappingLength)
                    return result = Result("incompatible", false, release, "El tamaño de la memoria RTSS no es compatible.");
                Header before = ReadHeader(memory);
                if (before.Values[0] == 0xDEAD) return result = Result("missing", false, false, null);
                if (!ValidHeader(before, memory.Length))
                    return result = Result("incompatible", false, release, "El formato de memoria RTSS no es compatible.");
                if (!TryLock(memory)) return result = Result("busy", false, release, null);
                locked = true;
                Header current = ReadHeader(memory);
                if (!Equal(before, current) || !ValidHeader(current, memory.Length))
                    return result = Result("incompatible", false, release, "La memoria RTSS cambió durante la operación.");

                if (release)
                {
                    for (uint i = 1; i < current.Count; i++)
                    {
                        long entry = current.Offset + (long)i * current.Size;
                        if (!Owned(memory, entry)) continue;
                        mayOwn = true;
                        ClearText(memory, entry);
                        // A C-string owner is released by its first NUL. Leaving the tail
                        // avoids a partially erased owner that a retry cannot recognize.
                        memory.WriteByte(entry + OwnerOffset, 0);
                        memory.IncrementInt32(FrameOffset);
                    }
                    return result = Result("released", false, false, null);
                }

                long slot = -1, empty = -1;
                for (uint i = 1; i < current.Count; i++)
                {
                    long entry = current.Offset + (long)i * current.Size;
                    if (Owned(memory, entry)) { slot = entry; break; }
                    if (empty < 0 && memory.ReadByte(entry + OwnerOffset) == 0) empty = entry;
                }
                if (slot < 0) slot = empty;
                if (slot < 0) return result = Result("full", false, false, null);
                mayOwn = true;
                if (!Owned(memory, slot)) Claim(memory, slot);
                // Only our own fields are written; slot padding and graphics are untouched.
                if (!Owned(memory, slot)) return result = Result("error", false, true, "No se pudo confirmar el propietario del OSD.");
                WriteText(memory, slot, text, 256);
                WriteText(memory, slot + ExtendedOffset, text, ExtendedLength);
                memory.IncrementInt32(FrameOffset);
                return result = Result("published", true, false, null);
            }
            catch (Exception)
            {
                return result = Result("error", false, mayOwn || release, "No se pudo actualizar la memoria compartida RTSS.");
            }
            finally
            {
                if (locked)
                {
                    bool unlocked = false;
                    try { unlocked = Unlock(memory); } catch { }
                    if (!unlocked && result != null)
                    {
                        result.State = "error"; result.Published = false; result.CleanupPending = true;
                        result.Error = "No se confirmó la liberación del bloqueo de memoria RTSS.";
                    }
                }
            }
        }

        private static bool ValidText(string text)
        {
            if (text == null || text.Length > MaximumTextLength) return false;
            foreach (char c in text) if (c != '\n' && (c < ' ' || c > '~')) return false;
            return true;
        }

        private static Header ReadHeader(IRtssMemoryView memory)
        {
            var header = new Header();
            for (int i = 0; i < header.Values.Length; i++) header.Values[i] = memory.ReadUInt32(i * 4);
            return header;
        }

        private static bool ValidHeader(Header h, long length)
        {
            uint version = h.Values[1];
            if (h.Values[0] != Signature || (version >> 16) != 2 || version < MinimumVersion || version > MaximumVersion) return false;
            // Later v2 revisions extended the header before the dynamic arrays.
            // Unknown revisions need an SDK review before accepting their offsets.
            uint headerSize = version >= 0x00020013 ? 96u : version >= 0x00020012 ? 80u :
                version >= 0x00020010 ? 72u : version >= 0x0002000f ? 64u : 40u;
            uint appSize = h.Values[2], appOffset = h.Values[3], appCount = h.Values[4];
            if (h.Count < 2 || h.Count > 256 || h.Size < ExtendedOffset + ExtendedLength || h.Size > 1024 * 1024 ||
                h.Offset < headerSize || (h.Offset & 3) != 0 || (h.Size & 3) != 0) return false;
            ulong osdEnd = (ulong)h.Offset + (ulong)h.Count * h.Size;
            if (osdEnd > (ulong)length) return false;
            if (appCount > 4096 || appSize > 1024 * 1024) return false;
            if (appCount == 0) return appOffset <= length;
            if (appSize < 4 || appOffset < headerSize || (appOffset & 3) != 0 || (appSize & 3) != 0) return false;
            ulong appEnd = (ulong)appOffset + (ulong)appCount * appSize;
            return appEnd <= (ulong)length && ((ulong)appOffset >= osdEnd || appEnd <= h.Offset);
        }

        private static bool Equal(Header a, Header b)
        {
            for (int i = 0; i < a.Values.Length; i++) if (a.Values[i] != b.Values[i]) return false;
            return true;
        }

        private static bool TryLock(IRtssMemoryView memory)
        {
            // No waiting for a busy producer. Bounded retries cover other-bit races only.
            for (int attempt = 0; attempt < 8; attempt++)
            {
                int value = unchecked((int)memory.ReadUInt32(BusyOffset));
                if ((value & 1) != 0) return false;
                if (memory.CompareExchangeInt32(BusyOffset, value | 1, value) == value) return true;
            }
            return false;
        }

        private static bool Unlock(IRtssMemoryView memory)
        {
            // CAS clears only our bit, preserving unrelated flags even if they changed.
            // An abnormal producer continuously changing other bits cannot hang the helper.
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int value = unchecked((int)memory.ReadUInt32(BusyOffset));
                if (memory.CompareExchangeInt32(BusyOffset, value & ~1, value) == value) return true;
            }
            return false;
        }

        private static bool Owned(IRtssMemoryView memory, long entry)
        {
            for (int i = 0; i < Owner.Length; i++)
                if (memory.ReadByte(entry + OwnerOffset + i) != (byte)Owner[i]) return false;
            return memory.ReadByte(entry + OwnerOffset + Owner.Length) == 0;
        }

        private static void Claim(IRtssMemoryView memory, long entry)
        {
            // Commit the first byte last: an interrupted claim remains an empty C-string.
            for (int i = 1; i < Owner.Length; i++) memory.WriteByte(entry + OwnerOffset + i, (byte)Owner[i]);
            memory.WriteByte(entry + OwnerOffset + Owner.Length, 0);
            memory.WriteByte(entry + OwnerOffset, (byte)Owner[0]);
        }

        private static void WriteText(IRtssMemoryView memory, long start, string text, int capacity)
        {
            int count = Math.Min(text.Length, capacity - 1);
            for (int i = 0; i < count; i++) memory.WriteByte(start + i, (byte)text[i]);
            memory.WriteByte(start + count, 0);
        }

        private static void ClearText(IRtssMemoryView memory, long entry)
        {
            // Clearing the initial byte clears each owned C-string without touching buffers.
            memory.WriteByte(entry, 0);
            memory.WriteByte(entry + ExtendedOffset, 0);
        }

        private static RtssPublishResult Result(string state, bool published, bool cleanup, string error)
        {
            return new RtssPublishResult { State = state, Published = published, CleanupPending = cleanup, Error = error };
        }

        private sealed class Header
        {
            internal readonly uint[] Values = new uint[8];
            internal uint Size { get { return Values[5]; } }
            internal uint Offset { get { return Values[6]; } }
            internal uint Count { get { return Values[7]; } }
        }
    }
}
