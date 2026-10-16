// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;

namespace LegionTweaks
{
    // Export DTOs deliberately contain no device paths, identities, process names,
    // profiles, account identifiers, input frames or arbitrary native error text.
    [DataContract]
    public sealed class DiagnosticReport
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string CapturedAtUtc { get; set; }
        [DataMember] public string HelperVersion { get; set; }
        [DataMember] public string WidgetVersion { get; set; }
        [DataMember] public string WidgetAssemblyVersion { get; set; }
        [DataMember] public DiagnosticController Controller { get; set; }
        [DataMember] public DiagnosticRecovery Recovery { get; set; }
        [DataMember] public DiagnosticHidHide HidHide { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticController
    {
        [DataMember] public bool Available { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public bool SourceConnected { get; set; }
        [DataMember] public bool Hidden { get; set; }
        [DataMember] public bool PreparingPlayerOne { get; set; }
        // Human-facing player number, unlike the zero-based controller IPC slot.
        [DataMember] public int? VirtualPlayer { get; set; }
        [DataMember] public string SlotCheckedAtUtc { get; set; }
        [DataMember] public long Frames { get; set; }
        [DataMember] public string Error { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticRecovery
    {
        [DataMember] public string PnpJournal { get; set; }
        [DataMember] public string PnpPhase { get; set; }
        [DataMember] public string HidHideJournal { get; set; }
        [DataMember] public string LastResult { get; set; }
        [DataMember] public string LastResultAtUtc { get; set; }
        [DataMember] public string LastPhase { get; set; }
        [DataMember] public string FailureStage { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public int? WorkerExitCode { get; set; }
        [DataMember] public bool PhysicalRecovered { get; set; }
        // Completed describes journal ownership, not successful virtual P1 activation.
        [DataMember] public bool RecoveryOperationEnded { get; set; }
    }

    [DataContract]
    public sealed class DiagnosticHidHide
    {
        [DataMember] public string State { get; set; }
        [DataMember] public bool Active { get; set; }
        [DataMember] public bool Inverted { get; set; }
        [DataMember] public int? ApplicationCount { get; set; }
        [DataMember] public int? HiddenDeviceCount { get; set; }
    }

    public static class Diagnostics
    {
        public const int MaxExportCharacters = 16384;
        private static readonly string[] phases = { "Armed", "Disabling", "Disabled", "Enabling", "Enabled", "Recovering" };
        private static readonly string[] stages = { "disable", "enable", "verify", "recovery", "watch", "handshake", "parent", "deadline", "exception" };
        private static readonly string[] states = { "absent", "present", "invalid", "unreadable" };
        private static readonly string[] errors = { "No se pudo consultar el estado del mando.", "La consulta del puesto virtual falló.",
            "Hay una recuperación pendiente del mando.", "El respaldo del mando no se pudo validar.", "La operación superó su tiempo permitido.",
            "HidHide no está disponible o rechazó la operación.", "ViGEmBus no está disponible o rechazó la operación.",
            "La fuente SDL del mando no está disponible.", "Cierra los juegos y Steam antes de preparar el mando.",
            "La operación del mando se interrumpió.", "Windows no confirmó la reconexión del mando." };

        public static DiagnosticController Controller(ControllerStatus value)
        {
            if (value == null) return new DiagnosticController { Error = errors[0] };
            return new DiagnosticController { Available = true, Active = value.Active, SourceConnected = value.Connected,
                Hidden = value.Hidden, PreparingPlayerOne = value.PreparingPlayerOne,
                VirtualPlayer = value.Active && value.VirtualSlot >= 0 && value.VirtualSlot < 4 ? value.VirtualSlot + 1 : null,
                SlotCheckedAtUtc = Time(value.SlotCheckedAtUtc), Frames = Math.Max(0, value.Frames),
                Error = ErrorSummary(value.Error) ?? (string.IsNullOrEmpty(value.SlotError) ? null : errors[1]) };
        }

        // Raw worker text can contain account/device paths. Only a known category
        // and a bounded numeric Configuration Manager result cross the IPC boundary.
        public static string ErrorSummary(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string text = raw.Length > 8192 ? raw.Substring(0, 8192) : raw;
            var cm = Regex.Match(text, @"Configuration Manager\s+0x([0-9a-fA-F]{1,8})\b", RegexOptions.CultureInvariant);
            if (cm.Success) return "Windows rechazó la operación del mando (Configuration Manager 0x" + cm.Groups[1].Value.ToUpperInvariant() + ").";
            foreach (string allowed in errors) if (text == allowed) return allowed;
            if (Has(text, "respaldo") && (Has(text, "inválid") || Has(text, "invalid") || Has(text, "validar"))) return errors[3];
            if (Has(text, "recuperación pendiente")) return errors[2];
            if (Has(text, "tiempo") || Has(text, "timeout") || Has(text, "tardó")) return errors[4];
            if (Has(text, "HidHide")) return errors[5];
            if (Has(text, "ViGEm")) return errors[6];
            if (Has(text, "SDL")) return errors[7];
            if (Has(text, "Steam") || Has(text, "Diablo")) return errors[8];
            if (Has(text, "no confirmó")) return errors[10];
            return errors[9];
        }

        public static DiagnosticRecovery Recovery(PhysicalRecoveryOutcome value, string resultState,
            string pnpJournal, string pnpPhase, string hideJournal)
        {
            var result = new DiagnosticRecovery { PnpJournal = State(pnpJournal), PnpPhase = Allowed(pnpPhase, phases),
                HidHideJournal = State(hideJournal), LastResult = State(resultState) };
            if (value == null || result.LastResult != "present") return result;
            result.LastResultAtUtc = Ticks(value.CapturedUtc); result.LastPhase = Allowed(value.Phase, phases);
            result.FailureStage = Allowed(value.FailureStage, stages); result.Error = ErrorSummary(value.Failure);
            result.WorkerExitCode = value.WorkerExitCode; result.PhysicalRecovered = value.Recovered;
            result.RecoveryOperationEnded = value.Completed;
            return result;
        }

        public static PhysicalRecoveryOutcome ParseOutcome(string json, string expectedSid)
        {
            if (json == null || json.Length > MaxExportCharacters) throw new InvalidDataException("Resultado PnP inválido.");
            var value = Protocol.Decode<PhysicalRecoveryOutcome>(json); Guid token;
            if (value == null || value.Version != 1 || string.IsNullOrEmpty(expectedSid) || value.Sid != expectedSid
                || value.Token == null || !Guid.TryParseExact(value.Token, "N", out token) || Ticks(value.CapturedUtc) == null)
                throw new InvalidDataException("Resultado PnP inválido.");
            return value;
        }

        // Reconstruct before rendering/export as well: never serialize an arbitrary
        // response object wholesale, including one supplied by an older helper.
        public static DiagnosticReport SafeCopy(DiagnosticReport value, string widgetVersion, string widgetAssemblyVersion = null)
        {
            if (value == null || value.Version != 1) throw new InvalidOperationException("El helper no devolvió un diagnóstico compatible.");
            var c = value.Controller; var r = value.Recovery; var h = value.HidHide;
            return new DiagnosticReport { CapturedAtUtc = Time(value.CapturedAtUtc), HelperVersion = Version(value.HelperVersion), WidgetVersion = Version(widgetVersion),
                WidgetAssemblyVersion = Version(widgetAssemblyVersion ?? value.WidgetAssemblyVersion),
                Controller = c == null ? Controller(null) : new DiagnosticController { Available = c.Available, Active = c.Active,
                    SourceConnected = c.SourceConnected, Hidden = c.Hidden, PreparingPlayerOne = c.PreparingPlayerOne,
                    VirtualPlayer = c.Active && c.VirtualPlayer >= 1 && c.VirtualPlayer <= 4 ? c.VirtualPlayer : null,
                    SlotCheckedAtUtc = Time(c.SlotCheckedAtUtc), Frames = Math.Max(0, c.Frames), Error = ErrorSummary(c.Error) },
                Recovery = r == null ? Recovery(null, "unreadable", "unreadable", null, "unreadable") : new DiagnosticRecovery {
                    PnpJournal = State(r.PnpJournal), PnpPhase = Allowed(r.PnpPhase, phases), HidHideJournal = State(r.HidHideJournal), LastResult = State(r.LastResult),
                    LastResultAtUtc = Time(r.LastResultAtUtc), LastPhase = Allowed(r.LastPhase, phases), FailureStage = Allowed(r.FailureStage, stages),
                    Error = ErrorSummary(r.Error), WorkerExitCode = r.WorkerExitCode, PhysicalRecovered = r.PhysicalRecovered,
                    RecoveryOperationEnded = r.RecoveryOperationEnded },
                HidHide = new DiagnosticHidHide { State = h?.State == "present" || h?.State == "deferred" ? h.State : "unreadable",
                    Active = h?.Active == true, Inverted = h?.Inverted == true, ApplicationCount = Count(h?.ApplicationCount), HiddenDeviceCount = Count(h?.HiddenDeviceCount) } };
        }

        public static string Export(DiagnosticReport value, string widgetVersion)
        {
            string json = Protocol.Encode(SafeCopy(value, widgetVersion));
            if (json.Length > MaxExportCharacters) throw new InvalidOperationException("El diagnóstico supera el tamaño permitido.");
            return json;
        }
        public static string Describe(DiagnosticReport value)
        {
            if (value == null) return "Pulsa Actualizar diagnóstico para obtener una captura local.";
            var v = SafeCopy(value, value.WidgetVersion); var c = v.Controller; var r = v.Recovery; var h = v.HidHide;
            var text = new StringBuilder("Captura UTC: " + (v.CapturedAtUtc ?? "sin confirmar") + "\nHelper " + (v.HelperVersion ?? "sin confirmar") + " · Widget " + (v.WidgetVersion ?? "sin confirmar"));
            if (v.WidgetAssemblyVersion != null) text.Append("\nEnsamblado del widget: ").Append(v.WidgetAssemblyVersion);
            if (v.HelperVersion != null && v.WidgetVersion != null && v.HelperVersion != v.WidgetVersion) text.Append("\nLas versiones del helper y del widget son diferentes.");
            text.Append("\nMando: ").Append(!c.Available ? "sin estado" : c.PreparingPlayerOne ? "preparando jugador 1" : c.Active ? "virtual activo" : "virtual apagado");
            if (c.Available) text.Append(" · Fuente ").Append(c.SourceConnected ? "conectada" : "sin conexión confirmada").Append(" · Ocultación ").Append(c.Hidden ? "activa" : "inactiva");
            text.Append("\nJugador virtual: ").Append(c.VirtualPlayer?.ToString(CultureInfo.InvariantCulture) ?? "sin confirmar").Append(" · Informes: ").Append(c.Frames);
            if (c.SlotCheckedAtUtc != null) text.Append("\nPuesto consultado UTC: ").Append(c.SlotCheckedAtUtc);
            if (c.Error != null) text.Append("\nMando: ").Append(c.Error);
            text.Append("\nRegistros de recuperación: PnP ").Append(StateLabel(r.PnpJournal)).Append(" · HidHide ").Append(StateLabel(r.HidHideJournal));
            if (r.PnpPhase != null) text.Append(" · Fase ").Append(r.PnpPhase);
            text.Append("\nÚltimo resultado PnP: ").Append(StateLabel(r.LastResult));
            if (r.LastResultAtUtc != null) text.Append(" · UTC ").Append(r.LastResultAtUtc);
            if (r.LastResult == "present") {
                if (r.FailureStage != null) text.Append("\nEtapa: ").Append(r.FailureStage);
                if (r.WorkerExitCode.HasValue) text.Append(" · Código del proceso: ").Append(r.WorkerExitCode.Value);
                if (r.Error != null) text.Append("\n").Append(r.Error);
                text.Append("\nRecuperación física registrada: ").Append(r.PhysicalRecovered ? "sí" : "sin confirmar");
                text.Append(" · Operación de recuperación terminada: ").Append(r.RecoveryOperationEnded ? "sí" : "sin confirmar");
            }
            text.Append("\nHidHide: ").Append(h.State == "deferred" ? "consulta aplazada durante la preparación" : h.State == "present" ? (h.Active ? "activo" : "inactivo") + (h.Inverted ? ", lista invertida" : ", lista normal") : "sin lectura confirmada");
            if (h.State == "present") text.Append(" · Aplicaciones: ").Append(h.ApplicationCount?.ToString(CultureInfo.InvariantCulture) ?? "N/D").Append(" · Dispositivos ocultos: ").Append(h.HiddenDeviceCount?.ToString(CultureInfo.InvariantCulture) ?? "N/D");
            text.Append("\nEl estado del mando es una copia del helper. Un registro PnP terminado no confirma la activación del virtual ni compatibilidad con un juego.");
            return text.ToString();
        }
        public static string Version(string value)
        {
            System.Version parsed;
            return value != null && value.Length <= 32 && System.Version.TryParse(value, out parsed) && parsed.Major <= 65535 && parsed.Minor <= 65535 && parsed.Build <= 65535 && parsed.Revision <= 65535 ? parsed.ToString() : null;
        }
        public static string Time(string value)
        {
            DateTimeOffset time;
            return value != null && value.Length <= 40 && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)
                ? time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : null;
        }
        public static string Ticks(long value) => value > 0 && value <= DateTime.MaxValue.Ticks ? new DateTime(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) : null;
        public static string State(string value) => Allowed(value, states) ?? "unreadable";
        private static string StateLabel(string value) => value == "absent" ? "ausente" : value == "present" ? "presente" : value == "invalid" ? "inválido" : "sin lectura confirmada";
        private static bool Has(string value, string find) => value.IndexOf(find, StringComparison.OrdinalIgnoreCase) >= 0;
        private static string Allowed(string value, string[] values) => Array.IndexOf(values, value) >= 0 ? value : null;
        private static int? Count(int? value) => value >= 0 && value <= 32768 ? value : null;
    }
}
