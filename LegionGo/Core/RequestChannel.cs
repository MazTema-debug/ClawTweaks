// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    // Commands serialize; optional read-only refreshes skip a busy channel.
    // The transport enforces these per-request response budgets.
    public sealed class RequestChannel
    {
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly Func<Request, int, Task<Response>> send;
        public RequestChannel(Func<Request, int, Task<Response>> send)
        { this.send = send ?? throw new ArgumentNullException(nameof(send)); }
        public async Task<Response> Send(Request request)
        {
            // The process-wide channel outlives any one UWP view. Its release
            // must not depend on a dispatcher which a desktop window can close.
            await gate.WaitAsync().ConfigureAwait(false);
            try { return await send(request, 60000).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        public async Task<Response> TryReadStatus()
        {
            if (!await gate.WaitAsync(0).ConfigureAwait(false)) return null;
            try { return await send(new Request { Operation = "recording_status" }, 5000).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }
}
