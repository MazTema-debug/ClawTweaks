// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace LegionTweaks
{
    // Closed stage vocabulary: callers cannot accidentally log an exception,
    // account identifier, path, device identifier or recovery authorization token.
    public enum ControllerPreparationStage
    {
        CommandEntered, BeforeStop, AfterStop, BeforePreflight, AfterPreflight,
        RunEntered, BeforeReaderCreation, AfterReaderCreation,
        BeforeVigemClient, AfterVigemClient, BeforeTargetCreation, AfterTargetCreation,
        BeforeEnvironmentInspection, AfterEnvironmentInspection,
        BeforeDisable, DisableReturned, DisableFailed,
        BeforePhysicalOpen, PhysicalOpened, BeforeHidHide, AfterHidHide,
        BeforeTargetDispose, AfterTargetDispose, BeforeReaderDispose, AfterReaderDispose,
        BeforeTargetCleanup, AfterTargetCleanup, BeforeVigemDispose, AfterVigemDispose,
        BeforeRecovery, AfterRecovery, OperationFailed, Finished
    }

    [DataContract]
    public sealed class ControllerPreparationModuleFlags
    {
        [DataMember] public bool SnapshotAvailable { get; set; }
        [DataMember] public bool SdlLoaded { get; set; }
        [DataMember] public bool XInputLoaded { get; set; }
        [DataMember] public bool ViGEmLoaded { get; set; }
    }
    [DataContract]
    public sealed class ControllerPreparationTraceEvent
    {
        [DataMember] public int Sequence { get; set; }
        [DataMember] public string Stage { get; set; }
        [DataMember] public string CapturedAtUtc { get; set; }
        [DataMember] public long ElapsedMilliseconds { get; set; }
        [DataMember] public ControllerPreparationModuleFlags Modules { get; set; }
        // Sticky facts about explicit source.Open execution in this operation.
        // Module presence alone is not evidence that any device was opened/read.
        [DataMember] public bool PhysicalOpenRequested { get; set; }
        [DataMember] public bool PhysicalOpened { get; set; }
    }
    [DataContract]
    public sealed class ControllerPreparationTraceReport
    {
        [DataMember] public int Version { get; set; } = 1;
        // A diagnostic correlation ID only; never used to authorize a transition.
        [DataMember] public string RunId { get; set; }
        [DataMember] public int ProcessId { get; set; }
        [DataMember] public string StartedAtUtc { get; set; }
        [DataMember] public bool Truncated { get; set; }
        [DataMember] public ControllerPreparationTraceEvent[] Events { get; set; }
    }

    public static class ControllerPreparationTraceRules
    {
        public const int MaximumEvents = 64, MaximumBytes = 65536, MaximumModules = 512;
        public static bool IsStage(ControllerPreparationStage stage) => Enum.IsDefined(typeof(ControllerPreparationStage), stage);
        public static ControllerPreparationModuleFlags Modules(IEnumerable<string> names)
        {
            var value = new ControllerPreparationModuleFlags();
            if (names == null) return value;
            try
            {
                int count = 0;
                foreach (string name in names)
                {
                    if (++count > MaximumModules) return new ControllerPreparationModuleFlags();
                    // Only base names may participate; raw names are never retained.
                    if (name == null || name.Length > 260 || name.IndexOfAny(new[] { '\\', '/', '\r', '\n' }) >= 0) continue;
                    value.SdlLoaded |= string.Equals(name, "SDL2.dll", StringComparison.OrdinalIgnoreCase);
                    value.ViGEmLoaded |= string.Equals(name, "vigemclient.dll", StringComparison.OrdinalIgnoreCase);
                    foreach (string xinput in new[] { "xinput1_1.dll", "xinput1_2.dll", "xinput1_3.dll", "xinput1_4.dll", "xinput9_1_0.dll", "xinputuap.dll" })
                        value.XInputLoaded |= string.Equals(name, xinput, StringComparison.OrdinalIgnoreCase);
                }
                value.SnapshotAvailable = true; return value;
            }
            catch { return new ControllerPreparationModuleFlags(); }
        }
        internal static ControllerPreparationModuleFlags CopyModules(ControllerPreparationModuleFlags value) => new ControllerPreparationModuleFlags {
            SnapshotAvailable = value?.SnapshotAvailable == true,
            SdlLoaded = value?.SnapshotAvailable == true && value.SdlLoaded,
            XInputLoaded = value?.SnapshotAvailable == true && value.XInputLoaded,
            ViGEmLoaded = value?.SnapshotAvailable == true && value.ViGEmLoaded };
        internal static string Utc(long ticks) => ticks > 0 && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : null;

        public static byte[] Serialize(ControllerPreparationTraceReport value)
        {
            Guid id;
            if (value == null || value.Version != 1 || value.RunId == null || !Guid.TryParseExact(value.RunId, "N", out id)
                || value.ProcessId <= 0 || !ValidUtc(value.StartedAtUtc) || value.Events == null || value.Events.Length > MaximumEvents)
                throw new InvalidDataException("Traza de preparación inválida.");
            var safe = new ControllerPreparationTraceReport { RunId = id.ToString("N"), ProcessId = value.ProcessId,
                StartedAtUtc = value.StartedAtUtc, Truncated = value.Truncated, Events = new ControllerPreparationTraceEvent[value.Events.Length] };
            long previous = 0; bool requested = false, opened = false;
            for (int i = 0; i < value.Events.Length; i++)
            {
                var entry = value.Events[i]; ControllerPreparationStage stage;
                if (entry == null || entry.Sequence != i + 1 || entry.Stage == null || entry.Stage.Length > 40
                    || !Enum.TryParse(entry.Stage, out stage) || !IsStage(stage) || stage.ToString() != entry.Stage
                    || !ValidUtc(entry.CapturedAtUtc) || entry.ElapsedMilliseconds < previous
                    || (requested && !entry.PhysicalOpenRequested) || (opened && !entry.PhysicalOpened)
                    || (entry.PhysicalOpened && !entry.PhysicalOpenRequested))
                    throw new InvalidDataException("Evento de preparación inválido.");
                previous = entry.ElapsedMilliseconds; requested = entry.PhysicalOpenRequested; opened = entry.PhysicalOpened;
                safe.Events[i] = new ControllerPreparationTraceEvent { Sequence = i + 1, Stage = stage.ToString(),
                    CapturedAtUtc = entry.CapturedAtUtc, ElapsedMilliseconds = previous, Modules = CopyModules(entry.Modules),
                    PhysicalOpenRequested = requested, PhysicalOpened = opened };
            }
            byte[] data = Encoding.UTF8.GetBytes(Protocol.Encode(safe));
            if (data.Length > MaximumBytes) throw new InvalidDataException("La traza supera el tamaño permitido.");
            return data;
        }
        private static bool ValidUtc(string value)
        {
            DateTime time;
            return value != null && value.Length <= 40 && DateTime.TryParseExact(value, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out time) && time.Kind == DateTimeKind.Utc;
        }
    }

    // Pure ownership guard for asynchronous writers of the single last-trace file.
    // Starting a newer operation invalidates delayed writes from an older one.
    public sealed class ControllerPreparationTraceGeneration
    {
        private long current;
        public long Begin() => Interlocked.Increment(ref current);
        public bool IsCurrent(long generation) => generation != 0 && Volatile.Read(ref current) == generation;
    }

    // No device or storage APIs. Rejected observations never change the evidence
    // already captured, and failed optional storage never determines operation success.
    public sealed class ControllerPreparationTraceBuffer
    {
        private readonly object gate = new object();
        private readonly ControllerPreparationTraceReport report;
        private readonly List<ControllerPreparationTraceEvent> entries = new List<ControllerPreparationTraceEvent>();
        private long previous;
        private bool requested, opened;
        public ControllerPreparationTraceBuffer(string runId, int processId, long startedUtcTicks)
        {
            Guid id; string utc = ControllerPreparationTraceRules.Utc(startedUtcTicks);
            if (!Guid.TryParseExact(runId, "N", out id) || processId <= 0 || utc == null) throw new ArgumentException("Identidad de traza inválida.");
            report = new ControllerPreparationTraceReport { RunId = id.ToString("N"), ProcessId = processId, StartedAtUtc = utc };
        }
        public bool HasCapacity { get { lock (gate) return entries.Count < ControllerPreparationTraceRules.MaximumEvents; } }
        public bool Record(ControllerPreparationStage stage, long utcTicks, long elapsedMilliseconds,
            ControllerPreparationModuleFlags modules, bool? openRequested = null, bool? physicalOpened = null)
        {
            lock (gate)
            {
                if (!ControllerPreparationTraceRules.IsStage(stage) || elapsedMilliseconds < previous
                    || ControllerPreparationTraceRules.Utc(utcTicks) == null || (physicalOpened == true && !requested && openRequested != true)) return false;
                if (entries.Count == ControllerPreparationTraceRules.MaximumEvents) { report.Truncated = true; return false; }
                requested |= openRequested == true; opened |= physicalOpened == true;
                previous = elapsedMilliseconds;
                entries.Add(new ControllerPreparationTraceEvent { Sequence = entries.Count + 1, Stage = stage.ToString(),
                    CapturedAtUtc = ControllerPreparationTraceRules.Utc(utcTicks), ElapsedMilliseconds = previous,
                    Modules = ControllerPreparationTraceRules.CopyModules(modules), PhysicalOpenRequested = requested, PhysicalOpened = opened });
                return true;
            }
        }
        public ControllerPreparationTraceReport Snapshot()
        {
            lock (gate)
            {
                var events = new ControllerPreparationTraceEvent[entries.Count];
                for (int i = 0; i < events.Length; i++) { var entry = entries[i]; events[i] = new ControllerPreparationTraceEvent {
                    Sequence = entry.Sequence, Stage = entry.Stage, CapturedAtUtc = entry.CapturedAtUtc, ElapsedMilliseconds = entry.ElapsedMilliseconds,
                    Modules = ControllerPreparationTraceRules.CopyModules(entry.Modules), PhysicalOpenRequested = entry.PhysicalOpenRequested, PhysicalOpened = entry.PhysicalOpened }; }
                return new ControllerPreparationTraceReport { RunId = report.RunId, ProcessId = report.ProcessId,
                    StartedAtUtc = report.StartedAtUtc, Truncated = report.Truncated, Events = events };
            }
        }
        public bool TryWrite(Action<byte[]> writer)
        {
            try { if (writer == null) return false; writer(ControllerPreparationTraceRules.Serialize(Snapshot())); return true; }
            catch { return false; }
        }
    }
}
