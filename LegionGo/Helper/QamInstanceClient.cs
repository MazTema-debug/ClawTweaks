// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks
{
    internal sealed class QamInstanceClient
    {
        private readonly string pipeName, image;
        private readonly int session;
        internal static string PipeName(SecurityIdentifier user, int session) => QamInstanceRequest.PipePrefix + user.Value + "." + session;
        internal QamInstanceClient(string expectedImage = null, string pipeName = null, int? session = null)
        {
            this.session = session ?? Process.GetCurrentProcess().SessionId;
            image = expectedImage ?? QamPipeIdentity.QamImage;
            this.pipeName = pipeName ?? PipeName(WindowsIdentity.GetCurrent().User, this.session);
        }
        internal async Task<QamInstanceResponse> Send(QamInstanceRequest request, CancellationToken cancellation, int timeout = 2500)
        {
            if (request?.Valid != true) throw new ArgumentException("Solicitud de instancia QAM inválida.");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous)) {
                deadline.CancelAfter(timeout);
                using (deadline.Token.Register(() => pipe.Dispose())) {
                    try {
                        await pipe.ConnectAsync(Math.Min(800, timeout), deadline.Token).ConfigureAwait(false);
                        if (!QamPipeIdentity.Matches(pipe.SafePipeHandle, true, WindowsIdentity.GetCurrent().User, image, session))
                            throw new UnauthorizedAccessException("No se pudo verificar el QAM de esta sesión.");
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                            await writer.WriteLineAsync(Protocol.Encode(request)).ConfigureAwait(false);
                            var result = Protocol.Decode<QamInstanceResponse>(await ReadOnlyQueries.ReadLine(reader).ConfigureAwait(false));
                            if (result.Version != Protocol.Version || result.SessionId != session || result.RequestId != request.RequestId)
                                throw new InvalidDataException("Respuesta de instancia QAM incompatible.");
                            return result;
                        }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException || ex is ObjectDisposedException) {
                        cancellation.ThrowIfCancellationRequested();
                        throw new TimeoutException("La instancia del menú rápido no respondió; no se repitió la orden.", ex);
                    }
                }
            }
        }
    }
}
