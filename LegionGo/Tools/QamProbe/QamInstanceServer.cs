// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks.QamProbe
{
    internal sealed class QamInstanceServer : IDisposable
    {
        private readonly SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
        private readonly int session = Process.GetCurrentProcess().SessionId;
        private readonly string ownImage, helperImage, pipeName;
        private readonly Func<QamInstanceRequest, CancellationToken, Task<QamInstanceResponse>> execute;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly Task worker;
        private NamedPipeServerStream active;
        private bool disposed;
        private readonly Dictionary<string, Tuple<string, string>> completed = new Dictionary<string, Tuple<string, string>>();
        private readonly Queue<string> order = new Queue<string>();
        internal QamInstanceServer(Func<QamInstanceRequest, CancellationToken, Task<QamInstanceResponse>> execute,
            string ownImage = null, string helperImage = null, string pipeName = null)
        {
            this.execute = execute;
            this.ownImage = ownImage ?? Process.GetCurrentProcess().MainModule.FileName;
            // A portable diagnostic instance accepts only its own second invocation.
            this.helperImage = helperImage ?? (string.Equals(this.ownImage, QamPipeIdentity.QamImage, StringComparison.OrdinalIgnoreCase) ? QamPipeIdentity.HelperImage : null);
            this.pipeName = pipeName ?? QamInstanceClient.PipeName(user, session);
            worker = Task.Run(Serve);
        }
        private async Task Serve()
        {
            var acl = new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!stop.IsCancellationRequested) {
                try {
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, acl)) {
                        lock (gate) { if (stop.IsCancellationRequested) return; active = pipe; }
                        await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        if (!QamPipeIdentity.Matches(pipe.SafePipeHandle, false, user, ownImage, session)
                            && (helperImage == null || !QamPipeIdentity.Matches(pipe.SafePipeHandle, false, user, helperImage, session))) continue;
                        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                        using (budget.Token.Register(() => pipe.Dispose()))
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                            budget.CancelAfter(3000);
                            string json = await ReadOnlyQueries.ReadLine(reader).ConfigureAwait(false);
                            if (json.Length > 4096) throw new InvalidDataException("Solicitud de instancia demasiado larga.");
                            var request = Protocol.Decode<QamInstanceRequest>(json);
                            string response;
                            if (request?.Valid != true) response = Protocol.Encode(new QamInstanceResponse { SessionId = session, Message = "Orden de instancia no admitida." });
                            else if (request.RequestId != null && completed.TryGetValue(request.RequestId, out var previous)) {
                                response = previous.Item1 == request.Operation ? previous.Item2 : Protocol.Encode(new QamInstanceResponse {
                                    SessionId = session, RequestId = request.RequestId, Message = "El identificador pertenece a otra acción." });
                            }
                            else {
                                var work = execute(request, budget.Token);
                                if (await Task.WhenAny(work, Task.Delay(3000, budget.Token)).ConfigureAwait(false) != work)
                                    throw new IOException("La vista del menú no respondió.");
                                var result = await work.ConfigureAwait(false);
                                result.RequestId = request.RequestId;
                                response = Protocol.Encode(result);
                                if (request.RequestId != null) {
                                    completed.Add(request.RequestId, Tuple.Create(request.Operation, response)); order.Enqueue(request.RequestId);
                                    if (order.Count > 16) completed.Remove(order.Dequeue());
                                }
                            }
                            await writer.WriteLineAsync(response).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ObjectDisposedException || ex is OperationCanceledException
                    || ex is System.Runtime.Serialization.SerializationException || ex is System.Xml.XmlException || ex is DecoderFallbackException || ex is UnauthorizedAccessException) { }
                finally { lock (gate) active = null; }
                if (!stop.IsCancellationRequested) { try { await Task.Delay(20, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { } }
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; stop.Cancel(); active?.Dispose(); }
            // Never block the UI thread on a callback that is queued to that same UI.
            // The canceled pipe closes immediately; the worker owns no setting writes.
            worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
    }
}
