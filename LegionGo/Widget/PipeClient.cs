// SPDX-License-Identifier: AGPL-3.0-only
// UWP CreateFileW pipe technique adapted from ClawTweaks IPC/NamedPipeClient.cs.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks.Widget
{
    internal static class PipeClient
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        public static async Task<Response> Send(Request request)
        {
            // Both Game Bar windows share the helper's single-client pipe. Never race an apply with a poll.
            await Gate.WaitAsync();
            try { return await SendCore(request); }
            finally { Gate.Release(); }
        }
        private static async Task<Response> SendCore(Request request)
        {
            SafeFileHandle handle = null;
            for (int i = 0; i < 20; i++)
            {
                handle = CreateFileW(@"\\.\pipe\" + Protocol.PipeName, 0xC0000000, 0, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (!handle.IsInvalid) break;
                handle.Dispose(); handle = null;
                await Task.Delay(75);
            }
            if (handle == null)
                throw new InvalidOperationException("Servicio desconectado. Inicia la tarea LegionTweaks-Z1E o vuelve a ejecutar Install-LegionGo.ps1.");
            using (handle)
            using (var stream = new FileStream(handle, FileAccess.ReadWrite, 4096, true))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
            {
                await writer.WriteLineAsync(Protocol.Encode(request));
                var read = reader.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(60000)) != read)
                    throw new TimeoutException("El servicio no respondió. Pulsa Actualizar antes de repetir un cambio.");
                var response = Protocol.Decode<Response>(await read);
                if (response.Version != Protocol.Version) throw new InvalidOperationException("Versión de servicio incompatible.");
                return response;
            }
        }
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
