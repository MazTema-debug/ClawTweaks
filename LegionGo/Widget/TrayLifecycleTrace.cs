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
                            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("desktop-tray-lifecycle.txt", CreationCollisionOption.ReplaceExisting);
                            await FileIO.WriteTextAsync(file, content);
                        } catch (Exception) { /* Evidence failure cannot alter interface/controller lifetime. */ }
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                }
            } catch (Exception) { }
        }
    }
}
