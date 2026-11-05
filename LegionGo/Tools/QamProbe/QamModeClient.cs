// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks.QamProbe
{
    internal sealed class QamModeClient
    {
        private readonly string pipeName, expectedServer;
        internal QamModeClient(string pipeName = null, string expectedServer = null)
        {
            this.pipeName = pipeName ?? QamModeRequest.PipePrefix + WindowsIdentity.GetCurrent().User.Value;
            this.expectedServer = expectedServer ?? QamPipeIdentity.HelperImage;
        }
        internal async Task<QamModeResponse> Send(QamModeRequest request, CancellationToken cancellation)
        {
            if (request?.Valid != true) throw new ArgumentException("Solicitud de modo inválida.");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous)) {
                deadline.CancelAfter(15000);
                using (deadline.Token.Register(() => pipe.Dispose())) {
                    try {
                        await pipe.ConnectAsync(1500, deadline.Token).ConfigureAwait(false);
                        if (!QamPipeIdentity.Matches(pipe.SafePipeHandle, true, WindowsIdentity.GetCurrent().User, expectedServer))
                            throw new InvalidOperationException("No se pudo verificar el helper instalado.");
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                            await writer.WriteLineAsync(Protocol.Encode(request)).ConfigureAwait(false);
                            var response = Protocol.Decode<QamModeResponse>(await ReadOnlyQueries.ReadLine(reader).ConfigureAwait(false));
                            if (response.Version != Protocol.Version || response.RequestId != request.RequestId)
                                throw new InvalidDataException("Respuesta del helper incompatible; vuelve a consultar.");
                            return response;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException) {
                        cancellation.ThrowIfCancellationRequested();
                        throw new InvalidOperationException(request.Operation == "query"
                            ? "El cambio de modo necesita el helper actualizado y el QAM instalado."
                            : "Resultado sin confirmar. Consulta el modo actual antes de volver a aplicar.", ex);
                    }
                }
            }
        }
    }
}
