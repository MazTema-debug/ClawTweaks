// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace LegionTweaks
{
    // Protocol facts only. No transport, background query or firmware initialization.
    // This first transition prototype is limited to the reviewed 2025 PID family.
    public static class ControllerModeProtocol
    {
        public static bool TryReadMode(byte[] report, out int mode)
        {
            mode = 0;
            if (report == null || report.Length != 64 || report[0] != 4 || report[1] != 0
                || report[2] != 3 || report[3] != 0x0e || report[4] != 3
                || (report[5] != 1 && report[5] != 2)) return false;
            mode = report[5]; return true;
        }

        public static bool IsOriginalReceiver(string root) => root != null
            && Regex.IsMatch(root, @"\AUSB\\VID_17EF&PID_61EB\\[A-Za-z0-9&_-]{1,128}\z", RegexOptions.IgnoreCase);

        public static bool IsTemporaryReceiver(string root) => root != null
            && Regex.IsMatch(root, @"\AUSB\\VID_17EF&PID_61EC\\[A-Za-z0-9&_-]{1,128}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsLocationPath(string path) => path != null && path.Length <= 1024
            && Regex.IsMatch(path, @"\A[A-Za-z0-9_#().-]+#USB\([0-9]+\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static string SelectLocation(string[] paths)
        {
            if (paths == null || paths.Length == 0 || paths.Length > 8 || paths.Any(p => p == null || p.Length > 1024))
                throw new InvalidDataException("Lista de ubicaciones USB inválida.");
            // Windows can also return an ACPI route ending in PRTn rather than USB(n).
            // Keep the explicit USB-port route and select deterministically regardless
            // of the ordering of those alternative descriptions.
            string selected = paths.Where(IsLocationPath).OrderBy(p => p.StartsWith("PCIROOT(", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (selected == null) throw new InvalidDataException("No se confirmó una ruta física de puerto USB.");
            return selected;
        }
    }

    [DataContract]
    public sealed class ControllerModeLease
    {
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public string Token { get; set; }
        [DataMember(IsRequired = true)] public string Sid { get; set; }
        [DataMember(IsRequired = true)] public string OriginalReceiver { get; set; }
        [DataMember(IsRequired = true)] public string OriginalLocation { get; set; }
        // No default original: arming must record a fresh, coherent mode response.
        [DataMember(IsRequired = true)] public int OriginalMode { get; set; }
        [DataMember(IsRequired = true)] public int OwnerPid { get; set; }
        [DataMember(IsRequired = true)] public long OwnerStarted { get; set; }
        [DataMember(IsRequired = true)] public long CreatedUtc { get; set; }
        [DataMember(IsRequired = true)] public long ExpiresUtc { get; set; }
        [DataMember(IsRequired = true)] public string Phase { get; set; }
        [DataMember(IsRequired = true)] public int WatchPid { get; set; }
        [DataMember(IsRequired = true)] public long WatchStarted { get; set; }
        [DataMember(IsRequired = true)] public int WorkerPid { get; set; }
        [DataMember(IsRequired = true)] public long WorkerStarted { get; set; }
    }

    public sealed class ControllerModeState
    {
        public string Receiver { get; set; }
        public string Location { get; set; }
        public string Service { get; set; }
        public bool Present { get; set; }
        public uint Problem { get; set; }
        // Zero means unknown or no valid query response, never XInput.
        public int Mode { get; set; }
    }

    [DataContract]
    public sealed class ControllerModeOutcome
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

    public interface IControllerModeJournal
    {
        ControllerModeLease Load();
        // The platform must persist atomically in protected storage before returning.
        // It must refuse replacement of any existing journal, including an invalid one.
        void Create(ControllerModeLease lease);
    }

    public interface IControllerModeDevice
    {
        // Enumerate the same unique receiver and obtain a fresh protocol response.
        // No cached/default response is allowed. PID changes can also change the
        // instance ID, so the original location must bind the temporary receiver.
        ControllerModeState Inspect(string originalReceiver, string location);
        // Exact receiver only, one command, no initialization/reset/fallback target.
        // Wait for re-enumeration before returning from a successful command.
        void ChangeMode(string originalReceiver, string location, int mode);
    }

    // Transaction policy for the separately supervised mode-cycle diagnostic.
    // Platform calls belong in bounded child processes; no mutex spans driver I/O.
    public static class ControllerModeRecoveryRules
    {
        public const int LeaseMilliseconds = 30000;
        private static readonly long LeaseTicks = TimeSpan.FromMilliseconds(LeaseMilliseconds).Ticks;

        public static void Validate(ControllerModeLease lease, string sid, string token = null)
        {
            Guid parsed;
            if (lease == null || lease.Version != 1 || !ControllerModeProtocol.IsOriginalReceiver(lease.OriginalReceiver)
                || !ControllerModeProtocol.IsLocationPath(lease.OriginalLocation)
                || lease.OriginalMode != 1 || string.IsNullOrEmpty(sid) || lease.Sid != sid
                || !Regex.IsMatch(sid, @"\AS-1-5-([0-9]+-)*[0-9]+\z")
                || lease.Token == null || lease.Token.Length != 32 || !Guid.TryParseExact(lease.Token, "N", out parsed)
                || (token != null && token != lease.Token) || lease.OwnerPid <= 0 || lease.OwnerStarted <= 0
                || lease.OwnerStarted > DateTime.MaxValue.Ticks || lease.CreatedUtc <= 0
                || lease.CreatedUtc > DateTime.MaxValue.Ticks - LeaseTicks || lease.ExpiresUtc != lease.CreatedUtc + LeaseTicks
                || lease.WatchPid < 0 || lease.WatchStarted < 0 || lease.WatchStarted > DateTime.MaxValue.Ticks
                || (lease.WatchPid == 0) != (lease.WatchStarted == 0)
                || lease.WorkerPid < 0 || lease.WorkerStarted < 0 || lease.WorkerStarted > DateTime.MaxValue.Ticks
                || (lease.WorkerPid == 0) != (lease.WorkerStarted == 0)
                || lease.WatchPid == lease.OwnerPid || lease.WorkerPid == lease.OwnerPid
                || (lease.WatchPid > 0 && lease.WatchPid == lease.WorkerPid)
                || Array.IndexOf(new[] { "Armed", "Switching", "Temporary", "Recovering", "Restored" }, lease.Phase) < 0
                || ((lease.Phase == "Switching" || lease.Phase == "Temporary") && lease.WatchPid == 0)
                || (lease.Phase == "Switching" && lease.WorkerPid == 0))
                throw new InvalidDataException("Respaldo del modo del mando inválido. Se conserva para revisión.");
        }

        public static bool IsCompletedAfterRetirement(ControllerModeLease current, ControllerModeOutcome outcome,
            string sid, string token, long createdUtc) => current == null && createdUtc > 0
            && !string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(token) && outcome != null && outcome.Version == 1
            && outcome.Sid == sid && outcome.Token == token && outcome.CapturedUtc >= createdUtc
            && outcome.Recovered && outcome.Completed;

        public static ControllerModeLease RequireCurrent(ControllerModeLease lease, string sid, string token, ControllerModeOutcome outcome)
        {
            if (lease == null) throw new InvalidOperationException(InterruptedMessage(outcome, sid, token));
            Validate(lease, sid, token); return lease;
        }

        public static string InterruptedMessage(ControllerModeOutcome outcome, string sid, string token)
        {
            if (outcome == null || outcome.Version != 1 || outcome.Sid != sid || outcome.Token != token || outcome.CapturedUtc <= 0)
                return "La operación de modo terminó; consulta su recuperación antes de repetirla.";
            return (outcome.Recovered ? "Modo físico recuperado. " : "Recuperación de modo pendiente. ")
                + (outcome.Failure ?? "La transición de modo no se completó.");
        }

        public static ControllerModeOutcome RecordOutcome(ControllerModeOutcome previous, ControllerModeLease lease,
            string sid, long now, string stage = null, string failure = null, int? exitCode = null, bool recovered = false, bool completed = false)
        {
            Validate(lease, sid);
            var result = previous != null && previous.Version == 1 && previous.Sid == sid && previous.Token == lease.Token
                ? Protocol.Decode<ControllerModeOutcome>(Protocol.Encode(previous)) : new ControllerModeOutcome { Token = lease.Token, Sid = sid };
            result.CapturedUtc = now; result.Phase = lease.Phase;
            if (result.Failure == null && failure != null) {
                result.Failure = failure.Length > 2500 ? failure.Substring(0, 2500) : failure;
                result.FailureStage = stage != null && stage.Length > 64 ? stage.Substring(0, 64) : stage;
                result.WorkerExitCode = exitCode;
            }
            result.Recovered |= recovered; result.Completed |= completed;
            return result;
        }

        public static void RequireSameOriginal(ControllerModeLease original, ControllerModeLease current)
        {
            if (original.Version != current.Version || original.Token != current.Token || original.Sid != current.Sid
                || original.OriginalReceiver != current.OriginalReceiver || original.OriginalLocation != current.OriginalLocation || original.OriginalMode != current.OriginalMode
                || original.OwnerPid != current.OwnerPid || original.OwnerStarted != current.OwnerStarted
                || original.CreatedUtc != current.CreatedUtc || original.ExpiresUtc != current.ExpiresUtc)
                throw new InvalidDataException("Cambió la identidad del respaldo de modo; se conserva para revisión.");
        }

        public static bool CanTransitionPhase(string from, string to)
        {
            switch (from) {
                case "Armed": return to == "Armed" || to == "Switching" || to == "Recovering";
                case "Switching": return to == "Switching" || to == "Temporary" || to == "Recovering";
                case "Temporary": return to == "Temporary" || to == "Recovering";
                case "Recovering": return to == "Recovering" || to == "Restored";
                case "Restored": return to == "Restored" || to == "Recovering";
                default: return false;
            }
        }

        public static void Arm(IControllerModeJournal journal, IControllerModeDevice device,
            ControllerModeLease intent, string sid, DeviceIdentity machine)
        {
            RequireMachine(machine);
            if (journal.Load() != null) throw new InvalidOperationException("Hay una recuperación de modo pendiente.");
            if (intent == null || !ControllerModeProtocol.IsOriginalReceiver(intent.OriginalReceiver) || !ControllerModeProtocol.IsLocationPath(intent.OriginalLocation)
                || intent.Phase != "Armed" || intent.WatchPid != 0 || intent.WorkerPid != 0)
                throw new InvalidDataException("La preparación del respaldo de modo no es válida.");
            var original = device.Inspect(intent.OriginalReceiver, intent.OriginalLocation);
            RequireState(original, intent.OriginalReceiver, intent.OriginalLocation, 1);
            var lease = Copy(intent);
            lease.OriginalMode = original.Mode;
            Validate(lease, sid);
            journal.Create(lease);
        }

        public static void SwitchToDirectInput(IControllerModeJournal journal, IControllerModeDevice device,
            string sid, string token, DeviceIdentity machine, Func<long> utcNow)
        {
            RequireMachine(machine);
            if (utcNow == null) throw new ArgumentNullException(nameof(utcNow));
            var lease = RequirePhase(journal, sid, token, "Switching");
            long before = utcNow(); RequireTime(lease, before);
            RequireState(device.Inspect(lease.OriginalReceiver, lease.OriginalLocation), lease.OriginalReceiver, lease.OriginalLocation, 1);
            // A mode query can block or race a supervisor. Re-read both authorization
            // and time after it returns, immediately before the only mutation.
            RequireSame(lease, RequirePhase(journal, sid, token, "Switching"));
            long after = utcNow(); RequireTime(lease, after);
            if (after < before) throw new InvalidOperationException("El reloj retrocedió antes del cambio de modo.");
            device.ChangeMode(lease.OriginalReceiver, lease.OriginalLocation, 2);
            RequireState(device.Inspect(lease.OriginalReceiver, lease.OriginalLocation), lease.OriginalReceiver, lease.OriginalLocation, 2);
            RequireSame(lease, RequirePhase(journal, sid, token, "Switching"));
            long completed = utcNow(); RequireTime(lease, completed);
            if (completed < after) throw new InvalidOperationException("El reloj retrocedió durante el cambio de modo.");
        }

        public static void Restore(IControllerModeJournal journal, IControllerModeDevice device,
            string sid, string token, DeviceIdentity machine)
        {
            RequireMachine(machine);
            // Recovery deliberately has no original deadline or cancellation token.
            // The adapter first stops/joins any setter worker, then durably marks
            // Recovering. It cannot retire a journal while that worker is still alive.
            var lease = RequirePhase(journal, sid, token, "Recovering");
            var state = device.Inspect(lease.OriginalReceiver, lease.OriginalLocation);
            RequireKnownState(state, lease.OriginalReceiver, lease.OriginalLocation);
            RequireSame(lease, RequirePhase(journal, sid, token, "Recovering"));
            if (state.Mode != lease.OriginalMode) device.ChangeMode(lease.OriginalReceiver, lease.OriginalLocation, lease.OriginalMode);
            RequireState(device.Inspect(lease.OriginalReceiver, lease.OriginalLocation), lease.OriginalReceiver, lease.OriginalLocation, lease.OriginalMode);
            RequireSame(lease, RequirePhase(journal, sid, token, "Recovering"));
        }

        public static void VerifyRestored(IControllerModeJournal journal, IControllerModeDevice device,
            string sid, string token, DeviceIdentity machine)
        {
            RequireMachine(machine);
            var lease = RequirePhase(journal, sid, token, "Restored");
            RequireState(device.Inspect(lease.OriginalReceiver, lease.OriginalLocation), lease.OriginalReceiver, lease.OriginalLocation, lease.OriginalMode);
            RequireSame(lease, RequirePhase(journal, sid, token, "Restored"));
            // The platform may retire this exact journal only after this independent
            // readback succeeds, with the setter worker confirmed stopped.
        }

        private static void RequireMachine(DeviceIdentity machine)
        {
            if (machine == null || !machine.IsSupported)
                throw new InvalidOperationException("El cambio de modo admite solo la Legion Go 8APU1 / Z1 Extreme.");
        }
        private static ControllerModeLease RequirePhase(IControllerModeJournal journal, string sid, string token, string phase)
        {
            if (string.IsNullOrEmpty(token)) throw new InvalidDataException("Falta la identidad de la operación de modo.");
            var lease = journal.Load(); Validate(lease, sid, token);
            if (lease.Phase != phase) throw new InvalidOperationException("La fase de recuperación del modo cambió.");
            return Copy(lease);
        }
        private static void RequireTime(ControllerModeLease lease, long now)
        {
            if (now < lease.CreatedUtc || now >= lease.ExpiresUtc)
                throw new InvalidOperationException("La autorización del cambio de modo ha caducado.");
        }
        private static void RequireKnownState(ControllerModeState state, string original, string location)
        {
            if (state == null || !state.Present || state.Problem != 0 || (state.Mode != 1 && state.Mode != 2)
                || !string.Equals(state.Service, "usbccgp", StringComparison.OrdinalIgnoreCase)
                || !ControllerModeProtocol.IsLocationPath(location) || !string.Equals(state.Location, location, StringComparison.OrdinalIgnoreCase)
                || (state.Mode == 1 ? !string.Equals(state.Receiver, original, StringComparison.OrdinalIgnoreCase)
                    : !ControllerModeProtocol.IsTemporaryReceiver(state.Receiver)))
                throw new InvalidOperationException("No se confirmó el mismo receptor con un modo válido y coherente.");
        }
        private static void RequireState(ControllerModeState state, string original, string location, int mode)
        {
            RequireKnownState(state, original, location);
            if (state.Mode != mode) throw new InvalidOperationException("El firmware no confirmó el modo esperado.");
        }
        private static void RequireSame(ControllerModeLease expected, ControllerModeLease current)
        {
            // Both leases have already been validated. Preserve every immutable field,
            // not just a reusable token. Case-only receiver changes also fail closed.
            if (Protocol.Encode(expected) != Protocol.Encode(current))
                throw new InvalidOperationException("Cambió el respaldo durante la operación del modo.");
        }
        private static ControllerModeLease Copy(ControllerModeLease lease) =>
            Protocol.Decode<ControllerModeLease>(Protocol.Encode(lease));
    }
}
