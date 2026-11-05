// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading;

namespace LegionTweaks
{
    internal interface ILegionButtonReader : IDisposable
    {
        byte[] Read(WaitHandle stop, WaitHandle changed);
    }
    internal interface IFpsButtonReader
    {
        ushort Product { get; }
    }
    internal interface IFpsIdentifiedReader { string DevicePath { get; } }
    // Adapter only: all consumers share the service's single owning read loop.
    internal sealed class LegionButtonReader : ILegionButtonReader, IFpsButtonReader, IFpsIdentifiedReader
    {
        private readonly LegionHidReader reader;
        internal LegionButtonReader(Action<string> diagnostic) { reader = LegionHidReader.Open(diagnostic); }
        public ushort Product => reader.Product;
        public string DevicePath => reader.DevicePath;
        public byte[] Read(WaitHandle stop, WaitHandle changed) => reader.Read(stop, changed);
        public void Dispose() => reader.Dispose();
    }
}
