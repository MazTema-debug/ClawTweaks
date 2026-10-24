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
            await gate.WaitAsync();
            try { return await send(request, 60000); }
            finally { gate.Release(); }
        }
        public async Task<Response> TryReadStatus()
        {
            if (!await gate.WaitAsync(0)) return null;
            try { return await send(new Request { Operation = "recording_status" }, 5000); }
            finally { gate.Release(); }
        }
    }
}
