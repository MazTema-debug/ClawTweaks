// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    internal sealed class ReadOnlyPipeServer : IDisposable
    {
        private readonly SecurityIdentifier user;
        private readonly ReadOnlyStatusService service;
        private readonly string pipeName;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly Task worker;
        private NamedPipeServerStream active;
        private bool disposed;

        internal ReadOnlyPipeServer(SecurityIdentifier user, ReadOnlyStatusService service, string pipeName = null)
        {
            this.user = user; this.service = service;
            this.pipeName = pipeName ?? ReadOnlyQueries.PipePrefix + user.Value;
            worker = Task.Run(Serve);
        }
        private async Task Serve()
        {
            var acl = new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!stop.IsCancellationRequested) {
                try {
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, acl)) {
                        lock (gate) {
                            if (stop.IsCancellationRequested) return;
                            active = pipe;
                        }
                        await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        if (!IsOwner(pipe)) continue;
                        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                        using (budget.Token.Register(() => pipe.Dispose()))
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                            budget.CancelAfter(3000);
                            var request = Protocol.Decode<Request>(await ReadOnlyQueries.ReadLine(reader).ConfigureAwait(false));
                            budget.CancelAfter(Timeout.Infinite);
                            var response = service.Execute(request);
                            string json = Protocol.Encode(response);
                            if (json.Length > Protocol.MaxMessageLength)
                                json = Protocol.Encode(new Response { Success = false, Message = "La lectura excede el límite IPC." });
                            budget.CancelAfter(3000);
                            await writer.WriteLineAsync(json).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is OperationCanceledException || ex is ObjectDisposedException
                    || ex is System.Runtime.Serialization.SerializationException || ex is System.Xml.XmlException
                    || ex is DecoderFallbackException || ex is UnauthorizedAccessException) { }
                finally { lock (gate) active = null; }
                // Avoid a retry spin if another helper instance owns the endpoint.
                if (!stop.IsCancellationRequested) {
                    try { await Task.Delay(50, stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
        }
        private bool IsOwner(NamedPipeServerStream pipe)
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid)) return false;
            using (var process = OpenProcess(0x1000, false, pid)) {
                if (process.IsInvalid || !OpenProcessToken(process, 8, out IntPtr token)) return false;
                try { using (var identity = new WindowsIdentity(token)) return user.Equals(identity.User); }
                finally { CloseHandle(token); }
            }
        }
        public void Dispose()
        {
            lock (gate) {
                if (disposed) return;
                disposed = true;
                stop.Cancel();
                active?.Dispose();
            }
            worker.GetAwaiter().GetResult();
            stop.Dispose();
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
