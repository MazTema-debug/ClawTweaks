// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace LegionTweaks
{
    [DataContract]
    public sealed class PhysicalRecoveryLease
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string Token { get; set; }
        [DataMember] public string Sid { get; set; }
        [DataMember] public string Device { get; set; }
        [DataMember] public int OwnerPid { get; set; }
        [DataMember] public long OwnerStarted { get; set; }
        [DataMember] public long CreatedUtc { get; set; }
        [DataMember] public long ExpiresUtc { get; set; }
        [DataMember] public uint OriginalStatus { get; set; }
        [DataMember] public uint OriginalProblem { get; set; }
        [DataMember] public string Phase { get; set; }
        [DataMember] public int WatchPid { get; set; }
        [DataMember] public long WatchStarted { get; set; }
        [DataMember] public int WorkerPid { get; set; }
        [DataMember] public long WorkerStarted { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool AllowBusyReaders { get; set; }
    }

    public sealed class PhysicalDeviceState
    {
        public string Device { get; set; }
        public string Service { get; set; }
        public uint Status { get; set; }
        public uint Problem { get; set; }
    }

    [DataContract]
    public sealed class PhysicalRecoveryOutcome
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string Token { get; set; }
        [DataMember] public string Sid { get; set; }
        [DataMember] public long CapturedUtc { get; set; }
        [DataMember] public string Phase { get; set; }
        [DataMember] public string FailureStage { get; set; }
        [DataMember] public string Failure { get; set; }
        [DataMember] public int? WorkerExitCode { get; set; }
        [DataMember] public bool Recovered { get; set; }
        [DataMember] public bool Completed { get; set; }
    }

    public interface IPhysicalRecoveryJournal
    {
        PhysicalRecoveryLease Load();
        void Create(PhysicalRecoveryLease lease);
    }
    public interface IPhysicalRecoveryDevice
    {
        PhysicalDeviceState Inspect(string instance);
        void Disable(string instance);
        void Enable(string instance);
    }

    // The platform adapter supplies protected storage and executes these native steps only
    // in killable child processes. No mutex is held while a driver call is in progress.
    public static class PhysicalRecoveryRules
    {
        public const uint Started = 0x00000008;
        public const uint HasProblem = 0x00000400;
        public const uint DisabledProblem = 22;
        // Windows SDK cfgmgr32.h: UI_NOT_OK=4, HARDWARE=2, ABSOLUTE=1, PERSIST=8.
        // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h
        public const uint NonPersistentDisableFlags = 4;
        // ABSOLUTE bypasses the driver's query-remove veto. It is restartable;
        // HARDWARE (2) and PERSIST (8) remain excluded. Never selected implicitly.
        public const uint BusyReaderDisableFlags = 1 | 4;
        public const int LeaseMilliseconds = 30000;
        public static bool IsEndpoint(string instance) => instance != null && instance.Length <= 1024 &&
            Regex.IsMatch(instance, @"\AUSB\\VID_17EF&PID_(6182|61EB)&MI_00\\[^\\\r\n]+\z", RegexOptions.IgnoreCase);
        public static bool IsStarted(PhysicalDeviceState state) => state != null &&
            (state.Status & Started) != 0 && (state.Status & HasProblem) == 0 && state.Problem == 0;
        public static bool Matches(PhysicalRecoveryOutcome result, string sid, string token) => result != null &&
            result.Version == 1 && token != null && result.Token == token && result.Sid == sid && result.CapturedUtc > 0;
        // An event can disappear after a supervisor has already recovered the device.
        // Only the retired operation's completed, fresh result can settle that race.
        // A still-present journal always retains ownership, including a new operation.
        public static bool IsCompletedAfterRetirement(PhysicalRecoveryLease current, PhysicalRecoveryOutcome result,
            string sid, string token, long createdUtc) => current == null && createdUtc > 0
            && !string.IsNullOrEmpty(sid) && Matches(result, sid, token)
            && result.CapturedUtc >= createdUtc && result.Recovered && result.Completed;
        public static string InterruptedMessage(PhysicalRecoveryOutcome result, string sid, string token)
        {
            if (!Matches(result, sid, token)) return "La preparación del jugador 1 se interrumpió; no hay una reconexión activa. Actualiza el estado y vuelve a intentarlo con los juegos cerrados.";
            string reason = string.IsNullOrWhiteSpace(result.Failure) ? "la preparación terminó antes de completar la activación" : result.Failure;
            string stage = string.IsNullOrWhiteSpace(result.FailureStage) ? "" : " Etapa: " + result.FailureStage + ".";
            return result.Recovered ? "Mando físico recuperado tras interrumpirse la preparación: " + reason + stage
                : "La preparación del jugador 1 se interrumpió: " + reason + stage;
        }
        public static PhysicalRecoveryLease RequireCurrent(PhysicalRecoveryLease lease, string sid, string token, PhysicalRecoveryOutcome result)
        {
            // A consumed journal is an operation lifecycle outcome, not corrupt backup data.
            if (lease == null) throw new InvalidOperationException(InterruptedMessage(result, sid, token));
            Validate(lease, sid, token); return lease;
        }
        public static PhysicalRecoveryOutcome RecordOutcome(PhysicalRecoveryOutcome previous, PhysicalRecoveryLease lease,
            string sid, long now, string stage = null, string failure = null, int? exitCode = null, bool recovered = false, bool completed = false)
        {
            Validate(lease, sid);
            var result = Matches(previous, sid, lease.Token) ? Protocol.Decode<PhysicalRecoveryOutcome>(Protocol.Encode(previous))
                : new PhysicalRecoveryOutcome { Token = lease.Token, Sid = sid };
            result.CapturedUtc = now; result.Phase = lease.Phase;
            if (string.IsNullOrEmpty(result.Failure) && !string.IsNullOrEmpty(failure))
            {
                result.Failure = failure.Length <= 2500 ? failure : failure.Substring(0, 2500);
                result.FailureStage = stage == null || stage.Length <= 64 ? stage : stage.Substring(0, 64);
                result.WorkerExitCode = exitCode;
            }
            result.Recovered |= recovered; result.Completed |= completed;
            return result;
        }
        public static void Validate(PhysicalRecoveryLease lease, string sid, string token = null)
        {
            Guid parsed;
            if (lease == null || lease.Version != 1 || !IsEndpoint(lease.Device) ||
                string.IsNullOrEmpty(sid) || lease.Sid != sid || !Regex.IsMatch(sid, @"\AS-1-5-(\d+-)*\d+\z") ||
                lease.Token == null || lease.Token.Length != 32 || !Guid.TryParseExact(lease.Token, "N", out parsed) ||
                (token != null && lease.Token != token) || lease.OwnerPid <= 0 || lease.OwnerStarted <= 0 ||
                lease.CreatedUtc <= 0 || lease.ExpiresUtc <= lease.CreatedUtc ||
                lease.ExpiresUtc - lease.CreatedUtc != TimeSpan.FromMilliseconds(LeaseMilliseconds).Ticks ||
                (lease.OriginalStatus & Started) == 0 || (lease.OriginalStatus & HasProblem) != 0 || lease.OriginalProblem != 0 ||
                (lease.WatchPid == 0) != (lease.WatchStarted == 0) || lease.WatchPid < 0 || lease.WatchStarted < 0 ||
                (lease.WorkerPid == 0) != (lease.WorkerStarted == 0) || lease.WorkerPid < 0 || lease.WorkerStarted < 0 ||
                Array.IndexOf(new[] { "Armed", "Disabling", "Disabled", "Enabling", "Enabled", "Recovering" }, lease.Phase) < 0)
                throw new InvalidDataException("Respaldo de reconexión del mando inválido. Se conserva para revisión.");
        }
        private static void CheckDevice(PhysicalDeviceState state, string instance)
        {
            if (!IsEndpoint(instance) || state == null || !string.Equals(state.Device, instance, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(state.Service, "xusb22", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("La reconexión admite solo la función XInput Lenovo MI_00 con servicio xusb22.");
        }
        public static void Arm(IPhysicalRecoveryJournal journal, IPhysicalRecoveryDevice device, PhysicalRecoveryLease lease, string sid)
        {
            if (journal.Load() != null) throw new InvalidOperationException("Hay una recuperación del mando pendiente.");
            if (lease == null || !IsEndpoint(lease.Device)) throw new InvalidOperationException("Identidad de mando no admitida.");
            var original = device.Inspect(lease.Device); CheckDevice(original, lease.Device);
            if (!IsStarted(original)) throw new InvalidOperationException("El mando físico debe estar iniciado y sin problemas antes de reconectarlo.");
            lease.OriginalStatus = original.Status; lease.OriginalProblem = original.Problem; lease.Phase = "Armed";
            Validate(lease, sid);
            journal.Create(lease); // Durable original and intent precede any disable call.
        }
        public static void Disable(IPhysicalRecoveryJournal journal, IPhysicalRecoveryDevice device, string sid, string token, long now)
        {
            var lease = journal.Load(); Validate(lease, sid, token);
            if (lease.Phase != "Disabling" || now < lease.CreatedUtc || now >= lease.ExpiresUtc)
                throw new InvalidOperationException("La autorización de reconexión ya no está activa.");
            var state = device.Inspect(lease.Device); CheckDevice(state, lease.Device);
            if (!IsStarted(state)) throw new InvalidOperationException("El estado del mando cambió antes de la reconexión.");
            // Re-read authorization immediately before the native mutation.
            var current = journal.Load(); Validate(current, sid, token);
            if (current.Phase != "Disabling") throw new InvalidOperationException("La recuperación del mando ya comenzó.");
            device.Disable(lease.Device);
            state = device.Inspect(lease.Device); CheckDevice(state, lease.Device);
            if ((state.Status & Started) != 0 || (state.Status & HasProblem) == 0 || state.Problem != DisabledProblem)
                throw new InvalidOperationException("Windows no confirmó la desconexión temporal del mando.");
        }
        public static void Enable(IPhysicalRecoveryJournal journal, IPhysicalRecoveryDevice device, string sid, string token)
        {
            var lease = journal.Load(); Validate(lease, sid, token);
            if (lease.Phase != "Enabling" && lease.Phase != "Recovering")
                throw new InvalidOperationException("La rehabilitación del mando no está autorizada.");
            var state = device.Inspect(lease.Device); CheckDevice(state, lease.Device);
            if (!IsStarted(state)) device.Enable(lease.Device);
            state = device.Inspect(lease.Device); CheckDevice(state, lease.Device);
            if (!IsStarted(state)) throw new InvalidOperationException("Windows aún no confirmó el mando físico iniciado y sin problemas; se conserva su respaldo.");
        }
        public static void VerifyEnabled(IPhysicalRecoveryJournal journal, IPhysicalRecoveryDevice device, string sid, string token)
        {
            var lease = journal.Load(); Validate(lease, sid, token);
            if (lease.Phase != "Enabled") throw new InvalidOperationException("La reconexión del mando aún no se ha completado.");
            var state = device.Inspect(lease.Device); CheckDevice(state, lease.Device);
            if (!IsStarted(state)) throw new InvalidOperationException("El mando físico no está iniciado; se conserva su respaldo.");
        }
    }
}
