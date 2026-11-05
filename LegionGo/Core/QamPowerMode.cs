// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class QamModeRequest
    {
        public const string PipePrefix = "LegionTweaks.Z1E.QamMode.v1.";
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public string Operation { get; set; }
        [DataMember] public int Mode { get; set; }
        [DataMember] public string RequestId { get; set; }
        public bool Valid => Version == Protocol.Version && (Operation == "query" ? Mode == 0 && RequestId == null
            : Operation == "apply_mode" && IsPreset(Mode) && Guid.TryParseExact(RequestId, "N", out Guid _));
        public static bool IsPreset(int value) => value >= 1 && value <= 3;
    }
    [DataContract]
    public sealed class QamModeResponse
    {
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public bool Success { get; set; }
        [DataMember] public bool CanApply { get; set; }
        [DataMember] public string RequestId { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public Snapshot State { get; set; }
    }

    // This instance shares the main helper's engine, lock and durable originals journal.
    public sealed class QamModeService
    {
        private readonly ControlEngine engine;
        private readonly object gate = new object();
        private readonly Dictionary<string, Tuple<int, string>> completed = new Dictionary<string, Tuple<int, string>>();
        private readonly Queue<string> order = new Queue<string>();
        public QamModeService(ControlEngine engine) { this.engine = engine; }
        public QamModeResponse Execute(QamModeRequest request)
        {
            if (request?.Valid != true) return new QamModeResponse { Message = "Solo se admiten consulta y los tres modos Lenovo." };
            lock (gate) {
                if (request.Operation == "apply_mode" && completed.TryGetValue(request.RequestId, out var previous))
                    return previous.Item1 == request.Mode ? Protocol.Decode<QamModeResponse>(previous.Item2)
                        : new QamModeResponse { RequestId = request.RequestId, Message = "El identificador ya pertenece a otro modo." };
                // A query never invokes the command dispatcher with caller-provided fields.
                var result = engine.Execute(request.Operation == "query" ? new Request { Operation = "telemetry" }
                    : new Request { Operation = "profile_apply", Profile = new ProfileDefinition {
                        Id = request.RequestId, Name = "QAM · modo Lenovo", Mode = request.Mode, Controller = null } });
                int? actual = CurrentMode(result.State);
                bool verified = result.Success && result.State?.Supported == true && actual.HasValue
                    && (request.Operation == "query" || actual == request.Mode);
                var response = new QamModeResponse { Success = verified, CanApply = verified,
                    RequestId = request.RequestId, State = result.State,
                    Message = verified ? (request.Operation == "query" ? "Modo consultado. Seleccionar no aplica cambios."
                        : "Modo " + TelemetryValue.ModeName(actual) + " aplicado y verificado.")
                        : result.Success ? "Modo sin confirmar por lectura; vuelve a consultar." : result.Message };
                if (request.Operation == "apply_mode") {
                    completed.Add(request.RequestId, Tuple.Create(request.Mode, Protocol.Encode(response)));
                    order.Enqueue(request.RequestId);
                    if (order.Count > 16) completed.Remove(order.Dequeue());
                }
                return response;
            }
        }
        public static int? CurrentMode(Snapshot state)
        {
            var values = state?.Values?.Where(x => x?.Key == "Mode").ToArray();
            var mode = values?.Length == 1 && string.IsNullOrEmpty(values[0].Error) ? values[0].Value : null;
            return mode.HasValue && (QamModeRequest.IsPreset(mode.Value) || mode == 255) ? mode : null;
        }
    }
}
