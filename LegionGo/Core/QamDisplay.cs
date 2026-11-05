// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class QamDisplayRequest
    {
        public const string PipePrefix = "LegionTweaks.Z1E.QamDisplay.v1.";
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public string Operation { get; set; }
        [DataMember] public int Frequency { get; set; }
        [DataMember] public string RequestId { get; set; }
        public static bool IsFrequency(int value) => value == 60 || value == 144;
        public bool Valid => Version == Protocol.Version && (Operation == "query" ? Frequency == 0 && RequestId == null
            : Operation == "set_refresh" && IsFrequency(Frequency) && Guid.TryParseExact(RequestId, "N", out Guid _));
    }
    [DataContract]
    public sealed class QamDisplayResponse
    {
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public bool Success { get; set; }
        [DataMember] public bool Supported { get; set; }
        [DataMember] public bool CanSetRefreshRate { get; set; }
        [DataMember] public bool Can60 { get; set; }
        [DataMember] public bool Can144 { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public DisplayInfo Info { get; set; }
    }

    // Delegates share the normal helper's display service and lock. No general command dispatcher.
    public sealed class QamDisplayService
    {
        private readonly bool supported;
        private readonly Func<DisplayStatus> inspect;
        private readonly Func<int, DisplayStatus> apply;
        private readonly object gate = new object();
        private readonly Dictionary<string, Tuple<int, string>> completed = new Dictionary<string, Tuple<int, string>>();
        private readonly Queue<string> order = new Queue<string>();
        public QamDisplayService(bool supported, Func<DisplayStatus> inspect, Func<int, DisplayStatus> apply)
        { this.supported = supported; this.inspect = inspect; this.apply = apply; }
        public QamDisplayResponse Execute(QamDisplayRequest request)
        {
            if (request?.Valid != true) return new QamDisplayResponse { Message = "Solo se admiten consulta y 60 o 144 Hz." };
            if (!supported) return new QamDisplayResponse { RequestId = request.RequestId, Message = "Modelo o CPU no compatibles." };
            lock (gate) {
                bool write = request.Operation == "set_refresh";
                if (write && completed.TryGetValue(request.RequestId, out var previous))
                    return previous.Item1 == request.Frequency ? Protocol.Decode<QamDisplayResponse>(previous.Item2)
                        : new QamDisplayResponse { RequestId = request.RequestId, Message = "El identificador ya pertenece a otra frecuencia." };
                QamDisplayResponse response;
                try {
                    var status = write ? apply(request.Frequency) : inspect();
                    bool success = status?.Supported == true && status.Info?.Current != null && string.IsNullOrEmpty(status.Error)
                        && status.Phase != "Failed" && (!write || (!status.Pending && !status.RecoveryPending && status.Info.Current.Frequency == request.Frequency));
                    bool can = success && status.Info.CanChange && !status.Pending && !status.RecoveryPending;
                    var options = DisplayRefreshRate.Options(status?.Info);
                    response = new QamDisplayResponse { Success = success, Supported = status?.Supported == true,
                        RequestId = request.RequestId, Info = status?.Info, CanSetRefreshRate = can,
                        Can60 = can && options.Single(x => x.Frequency == 60).IsAvailable,
                        Can144 = can && options.Single(x => x.Frequency == 144).IsAvailable,
                        Message = !success ? status?.Error ?? status?.Message ?? "Lectura sin confirmar."
                            : status.Pending || status.RecoveryPending ? "Hay una recuperación de pantalla pendiente."
                            : !status.Info.CanChange ? status.Info.Restriction ?? "Windows no permite cambiar esta pantalla."
                            : write ? "Frecuencia verificada: " + status.Info.Current + "."
                            : "Pantalla consultada. Cambiar frecuencia conserva la resolución actual." };
                }
                catch (Exception ex) {
                    // A failed write is never retried here. Report a new reading if it is still available.
                    DisplayInfo actual = null;
                    try { actual = inspect()?.Info; } catch (Exception) { }
                    response = new QamDisplayResponse { RequestId = request.RequestId, Supported = true,
                        Info = actual, Message = "Cambio o lectura sin confirmar. " + ex.Message };
                }
                if (write) {
                    completed.Add(request.RequestId, Tuple.Create(request.Frequency, Protocol.Encode(response)));
                    order.Enqueue(request.RequestId);
                    if (order.Count > 16) completed.Remove(order.Dequeue());
                }
                return response;
            }
        }
    }
}
