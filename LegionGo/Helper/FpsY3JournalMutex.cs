// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Threading;

namespace LegionTweaks
{
    internal static class FpsY3AbandonmentRecorder
    {
        internal const string Leaf = "fps-y3-journal-abandoned.flag";
        private static readonly byte[] Content = System.Text.Encoding.ASCII.GetBytes("LegionTweaks.Y3.JournalAbandoned.v1\n");

        // A warning is durable and never overwritten or removed here. Recovery
        // does not consult this flag; only new admission and maintenance do.
        internal static void Record(string directory, Action<bool> requireProtected)
        {
            requireProtected(false); string path = Path.Combine(directory, Leaf);
            try {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) {
                    file.Write(Content, 0, Content.Length); file.Flush(true);
                }
            }
            catch (IOException) {
                // An existing protected marker is already a fail-closed warning.
                File.GetAttributes(path);
            }
            requireProtected(true);
        }
    }

    internal static class FpsY3JournalMutex
    {
        // Recording occurs inside the existing short journal critical section,
        // never for the maintenance lifetime. Even if recording fails, the
        // recovery operation is allowed to continue; the process is marked
        // uncertain so admission fails closed in that process.
        internal static void Execute(string name, Action recordAbandonment, Action markUncertain, Action action)
        {
            using (var gate = new Mutex(false, name)) {
                bool owns = false;
                try {
                    try { owns = gate.WaitOne(2000); }
                    catch (AbandonedMutexException) {
                        owns = true;
                        try { recordAbandonment(); } catch { markUncertain(); }
                    }
                    if (!owns) throw new TimeoutException("El diario Y3 está ocupado.");
                    action();
                }
                finally { if (owns) gate.ReleaseMutex(); }
            }
        }
    }
}
