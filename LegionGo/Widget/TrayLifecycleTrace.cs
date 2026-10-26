// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LegionTweaks.Widget
{
    // Bounded private lifecycle evidence. Fixed event names and view-state flags
    // only: no input, titles, paths, handles, exception messages or game identity.
    internal static class TrayLifecycleTrace
    {
        private static readonly object Gate = new object();
        private static readonly Queue<string> Events = new Queue<string>();
        private static Task pending = Task.CompletedTask;
        internal static void Write(string eventName, DesktopTrayState state = null)
        {
            try {
                string line = DateTime.UtcNow.ToString("o") + "\t" + eventName;
                if (state != null) line += "\tready=" + state.Ready + " hiding=" + state.Hiding + " hidden=" + state.Hidden + " exiting=" + state.Exiting;
                lock (Gate) {
                    Events.Enqueue(line); while (Events.Count > 64) Events.Dequeue();
                    string content = string.Join("\r\n", Events.ToArray());
                    pending = pending.ContinueWith(async ignored => {
                        try {
                            // Never truncate the last complete snapshot while the
                            // process may be exiting. The serialized writer owns
                            // this fixed temporary name; publish only after its
                            // complete contents have been written. A final event
                            // may still be lost on exit; this is not a durability
                            // guarantee for interrupted filesystem operations.
                            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("desktop-tray-lifecycle.pending", CreationCollisionOption.ReplaceExisting);
                            await FileIO.WriteTextAsync(file, content);
                            await file.RenameAsync("desktop-tray-lifecycle.txt", NameCollisionOption.ReplaceExisting);
                        } catch (Exception) { /* Evidence failure cannot alter interface/controller lifetime. */ }
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                }
            } catch (Exception) { }
        }
    }
}
