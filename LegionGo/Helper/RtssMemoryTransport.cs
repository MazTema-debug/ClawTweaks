// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace LegionTweaks
{
    internal sealed class RtssMemoryTransport : IRtssOsdOutput
    {
        private readonly object gate = new object();
        private readonly Func<IRtssMemoryView> open;
        private bool disposed, mayOwn;

        internal RtssMemoryTransport() : this(OpenExisting) { }
        internal RtssMemoryTransport(Func<IRtssMemoryView> open)
        {
            this.open = open ?? throw new ArgumentNullException(nameof(open));
        }

        public RtssPublishResult Publish(string text)
        {
            lock (gate)
            {
                if (disposed) return Error(mayOwn);
                return Execute(text, false);
            }
        }

        public RtssPublishResult Release()
        {
            lock (gate) { return disposed ? Error(mayOwn) : Execute(null, true); }
        }

        private RtssPublishResult Execute(string text, bool release)
        {
            try
            {
                // Open a fresh handle on every operation: no stale view survives RTSS restart.
                using (IRtssMemoryView memory = open())
                {
                    RtssPublishResult result = release ? RtssMemoryProtocol.Release(memory) : RtssMemoryProtocol.Publish(memory, text);
                    if (result.Published || result.CleanupPending) mayOwn = true;
                    if (result.State == "released" || result.State == "missing") mayOwn = false;
                    return result;
                }
            }
            catch (FileNotFoundException)
            {
                mayOwn = false;
                return new RtssPublishResult { State = "missing" };
            }
            catch (Exception) { return Error(release || mayOwn); }
        }

        private static RtssPublishResult Error(bool cleanup)
        {
            return new RtssPublishResult { State = "error", CleanupPending = cleanup, Error = "No se pudo abrir o actualizar la memoria RTSS." };
        }

        private static IRtssMemoryView OpenExisting()
        {
            MemoryMappedFile mapping = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.ReadWrite);
            try { return new NativeView(mapping); }
            catch { mapping.Dispose(); throw; }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                // The overlay service owns the bounded cleanup policy. Disposing an
                // unused/OFF transport must never open or write a shared mapping.
                disposed = true;
            }
        }

        private sealed unsafe class NativeView : IRtssMemoryView
        {
            private readonly MemoryMappedFile mapping;
            private readonly MemoryMappedViewAccessor accessor;
            private byte* pointer;
            private bool acquired;
            internal NativeView(MemoryMappedFile mapping)
            {
                this.mapping = mapping;
                accessor = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
                try
                {
                    byte* start = null;
                    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref start);
                    acquired = true;
                    pointer = start + accessor.PointerOffset;
                }
                catch { accessor.Dispose(); throw; }
            }
            public long Length { get { return accessor.Capacity; } }
            public byte ReadByte(long offset) { Check(offset, 1, false); return pointer[offset]; }
            public uint ReadUInt32(long offset) { Check(offset, 4, true); return unchecked((uint)Volatile.Read(ref *(int*)(pointer + offset))); }
            public void WriteByte(long offset, byte value) { Check(offset, 1, false); pointer[offset] = value; }
            public int CompareExchangeInt32(long offset, int value, int comparand)
            {
                Check(offset, 4, true);
                return Interlocked.CompareExchange(ref *(int*)(pointer + offset), value, comparand);
            }
            public int IncrementInt32(long offset)
            {
                Check(offset, 4, true);
                return Interlocked.Increment(ref *(int*)(pointer + offset));
            }
            private void Check(long offset, int bytes, bool aligned)
            {
                if (!acquired || offset < 0 || offset > Length - bytes || (aligned && (offset & 3) != 0))
                    throw new InvalidDataException("Fuera de los límites de la memoria RTSS.");
            }
            public void Dispose()
            {
                if (acquired) { accessor.SafeMemoryMappedViewHandle.ReleasePointer(); acquired = false; pointer = null; }
                accessor.Dispose();
                mapping.Dispose();
            }
        }
    }
}
