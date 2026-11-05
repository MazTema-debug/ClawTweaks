// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    internal sealed class QamDisplayPipeServer : IDisposable
    {
        private readonly SecurityIdentifier user;
        private readonly QamDisplayService service;
        private readonly string expectedImage, pipeName;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly Task worker;
        private NamedPipeServerStream active;
        private bool disposed;
        internal static QamDisplayPipeServer TryCreateInstalled(SecurityIdentifier user, QamDisplayService service)
        {
            string directory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(directory, QamPipeIdentity.InstallDirectory, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(QamPipeIdentity.QamImage)) return null;
            foreach (string path in new[] { directory, Path.GetDirectoryName(QamPipeIdentity.QamImage), QamPipeIdentity.QamImage })
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            return new QamDisplayPipeServer(user, service, QamPipeIdentity.QamImage);
        }
        internal QamDisplayPipeServer(SecurityIdentifier user, QamDisplayService service, string expectedImage, string pipeName = null)
        {
            this.user = user; this.service = service; this.expectedImage = expectedImage;
            this.pipeName = pipeName ?? QamDisplayRequest.PipePrefix + user.Value;
            worker = Task.Run(Serve);
        }
        private async Task Serve()
        {
            var acl = new PipeSecurity();
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            while (!stop.IsCancellationRequested) {
                try {
                    using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 4096, 4096, acl)) {
                        lock (gate) { if (stop.IsCancellationRequested) return; active = pipe; }
                        await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                        if (!QamPipeIdentity.Matches(pipe.SafePipeHandle, false, user, expectedImage)) continue;
                        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                        using (budget.Token.Register(() => pipe.Dispose()))
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                            budget.CancelAfter(3000);
                            var request = Protocol.Decode<QamDisplayRequest>(await ReadOnlyQueries.ReadLine(reader).ConfigureAwait(false));
                            budget.CancelAfter(Timeout.Infinite);
                            var response = service.Execute(request);
                            string json = Protocol.Encode(response);
                            if (json.Length > Protocol.MaxMessageLength) json = Protocol.Encode(new QamDisplayResponse { Message = "Respuesta demasiado larga; vuelve a consultar." });
                            budget.CancelAfter(3000);
                            await writer.WriteLineAsync(json).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is OperationCanceledException || ex is ObjectDisposedException
                    || ex is System.Runtime.Serialization.SerializationException || ex is System.Xml.XmlException
                    || ex is DecoderFallbackException || ex is UnauthorizedAccessException) { }
                finally { lock (gate) active = null; }
                if (!stop.IsCancellationRequested) {
                    try { await Task.Delay(50, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; stop.Cancel(); active?.Dispose(); }
            worker.GetAwaiter().GetResult(); stop.Dispose();
        }
    }
}
