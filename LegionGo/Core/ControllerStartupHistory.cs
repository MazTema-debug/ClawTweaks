// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class ControllerStartupDecision
    {
        [DataMember(IsRequired = true)] public string Id { get; set; }
        [DataMember(IsRequired = true)] public long StartedUtc { get; set; }
        [DataMember(IsRequired = true)] public long UpdatedUtc { get; set; }
        [DataMember(IsRequired = true)] public bool EnabledAtStartup { get; set; }
        [DataMember(IsRequired = true)] public string State { get; set; }
        [DataMember(IsRequired = true)] public string Message { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Error { get; set; }
        public void Validate()
        {
            Guid id;
            if (Id == null || Id.Length != 32 || !Guid.TryParseExact(Id, "N", out id)
                || StartedUtc <= 0 || StartedUtc > DateTime.MaxValue.Ticks || UpdatedUtc <= 0 || UpdatedUtc > DateTime.MaxValue.Ticks
                || (State != "disabled" && State != "starting" && State != "active" && State != "failed"
                    && State != "canceled" && State != "blocked" && State != "skipped")
                || string.IsNullOrEmpty(Message) || Message.Length > 512 || (Error != null && Error.Length > 512))
                throw new InvalidOperationException("Historial de inicio incompatible; se conserva el archivo.");
        }
    }

    [DataContract]
    public sealed class ControllerStartupHistory
    {
        public const int Capacity = 8;
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public ControllerStartupDecision[] Decisions { get; set; } = new ControllerStartupDecision[0];
        public void Validate()
        {
            if (Version != 1 || Decisions == null || Decisions.Length > Capacity)
                throw new InvalidOperationException("Historial de inicio incompatible; se conserva el archivo.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var decision in Decisions) {
                if (decision == null || !ids.Add(decision.Id ?? "")) throw new InvalidOperationException("Historial de inicio incompleto.");
                decision.Validate();
            }
        }
        public ControllerStartupHistory With(ControllerStartupDecision decision)
        {
            Validate(); decision.Validate();
            var items = new List<ControllerStartupDecision>();
            foreach (var previous in Decisions) if (previous.Id != decision.Id) items.Add(previous);
            items.Add(decision);
            while (items.Count > Capacity) items.RemoveAt(0);
            return Protocol.Decode<ControllerStartupHistory>(Protocol.Encode(new ControllerStartupHistory { Decisions = items.ToArray() }));
        }
    }
    public interface IControllerStartupHistoryStorage
    {
        ControllerStartupHistory LoadHistory();
        void RecordDecision(ControllerStartupDecision decision);
    }
    public interface IControllerStartupHistoryControl
    {
        ControllerStartupHistory GetHistory();
    }
}
