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
    internal sealed class QamReadClient
    {
        private readonly string pipeName;
        internal QamReadClient(string pipeName = null)
        { this.pipeName = pipeName ?? ReadOnlyQueries.PipePrefix + WindowsIdentity.GetCurrent().User.Value; }

        internal static string Operation(QamQuery query)
        {
            switch (query) {
                case QamQuery.Power: return "telemetry";
                case QamQuery.Display: return "display_status";
                case QamQuery.Lossless: return "lossless_status";
                default: throw new ArgumentOutOfRangeException(nameof(query));
            }
        }
        internal async Task<Response> Read(QamQuery query, CancellationToken cancellation)
        {
            var request = new Request { Operation = Operation(query) };
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous)) {
                deadline.CancelAfter(8000);
                using (deadline.Token.Register(() => pipe.Dispose())) {
                    try {
                        await pipe.ConnectAsync(1500, deadline.Token).ConfigureAwait(false);
                        using (var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true))
                        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true }) {
                            await writer.WriteLineAsync(Protocol.Encode(request)).ConfigureAwait(false);
                            var response = Protocol.Decode<Response>(await ReadOnlyQueries.ReadLine(reader).ConfigureAwait(false));
                            if (response.Version != Protocol.Version) throw new InvalidDataException("Versión del helper incompatible.");
                            return response;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException || ex is ObjectDisposedException) {
                        cancellation.ThrowIfCancellationRequested();
                        throw new InvalidOperationException("Helper sin conexión de lectura. Abre Iniciar-Prueba-QAM o usa un helper con soporte QAM y vuelve a consultar.", ex);
                    }
                }
            }
        }
    }
}
