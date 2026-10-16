// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading;

namespace LegionTweaks
{
    internal interface ILegionButtonReader : IDisposable
    {
        byte[] Read(WaitHandle stop, WaitHandle changed);
    }
    // Adapter only: all consumers share the service's single owning read loop.
    internal sealed class LegionButtonReader : ILegionButtonReader
    {
        private readonly LegionHidReader reader;
        internal LegionButtonReader(Action<string> diagnostic) { reader = LegionHidReader.Open(diagnostic); }
        public byte[] Read(WaitHandle stop, WaitHandle changed) => reader.Read(stop, changed);
        public void Dispose() => reader.Dispose();
    }
}
