// SPDX-License-Identifier: AGPL-3.0-only
using System;
using LegionTweaks;

internal static partial class Program
{
    private static void DiagnosticsTests()
    {
        Test("Diagnostic export contains bounded categories instead of private native text", () => {
            var report = DiagnosticFixture();
            report.Controller = Diagnostics.Controller(new ControllerStatus { Active = true, Connected = true, VirtualSlot = 0,
                Input = new GamepadFrame { LT = 255 }, Error = "Private C:\\Users\\SecretName\\config.json USB\\VID_17EF&PID_61EB\\SecretSerial S-1-5-21-100-200-300-400 token 1234567890abcdef" });
            var json = Diagnostics.Export(report, "0.1.13.0");
            foreach (string secret in new[] { "SecretName", "SecretSerial", "S-1-5-21", "1234567890abcdef", "VID_17EF", "Input", "LT", "Profile" })
                Assert(!json.Contains(secret), "diagnosis leaked " + secret);
            Assert(json.Contains("VirtualPlayer") && json.Length < Diagnostics.MaxExportCharacters, "diagnostic missing or oversized");
        });
        Test("Diagnostic errors retain only Configuration Manager numeric result", () => {
            string raw = "El proceso PNP terminó con código 1. No se pudo desconectar temporalmente el mando (Configuration Manager 0x17). C:\\Users\\Secret\\file S-1-5-21-100";
            string value = Diagnostics.ErrorSummary(raw);
            Assert(value.Contains("0x17") && !value.Contains("Secret") && !value.Contains("S-1-5"), "CM result lost or leaked text");
            Assert(Diagnostics.ErrorSummary(value) == value, "safe error changed on reconstruction");
            Assert(!Diagnostics.ErrorSummary("Configuration Manager 0x17SECRET").Contains("SECRET"), "unbounded CM code accepted");
        });
        Test("Diagnostic recovery completion never asserts virtual P1 activation", () => {
            var raw = DiagnosticOutcome(); raw.Recovered = true; raw.Completed = true; raw.Failure = "Configuration Manager 0x17"; raw.FailureStage = "disable";
            var report = DiagnosticFixture(); report.Recovery = Diagnostics.Recovery(raw, "present", "absent", null, "absent");
            string text = Diagnostics.Describe(report);
            Assert(report.Recovery.PhysicalRecovered && report.Recovery.RecoveryOperationEnded && report.Controller.VirtualPlayer == null, "rollback invented active P1");
            Assert(text.Contains("no confirma la activación") && text.Contains("0x17"), "recovery limitation or failure hidden");
        });
        Test("Diagnostic sanitization rejects path-like timestamps versions phases and counts", () => {
            var report = DiagnosticFixture(); report.CapturedAtUtc = "C:\\Users\\Secret\\file"; report.HelperVersion = "Secret version";
            report.Recovery.PnpPhase = "SecretSerial"; report.Recovery.FailureStage = "SecretProcess";
            report.HidHide.ApplicationCount = int.MaxValue; report.HidHide.HiddenDeviceCount = -1;
            var value = Diagnostics.SafeCopy(report, "0.1.13.0"); var json = Diagnostics.Export(value, "0.1.13.0");
            Assert(value.CapturedAtUtc == null && value.HelperVersion == null && value.Recovery.PnpPhase == null && value.Recovery.FailureStage == null, "unsafe text survived");
            Assert(value.HidHide.ApplicationCount == null && value.HidHide.HiddenDeviceCount == null && !json.Contains("Secret"), "invalid metadata escaped validation");
        });
        Test("Diagnostic slots are human numbered and inactive or invalid slots stay unconfirmed", () => {
            Assert(Diagnostics.Controller(new ControllerStatus { Active = true, VirtualSlot = 0 }).VirtualPlayer == 1, "zero based slot leaked");
            Assert(Diagnostics.Controller(new ControllerStatus { Active = true, VirtualSlot = 3 }).VirtualPlayer == 4, "last valid slot rejected");
            Assert(Diagnostics.Controller(new ControllerStatus { Active = false, VirtualSlot = 0 }).VirtualPlayer == null, "inactive controller retained old slot");
            Assert(Diagnostics.Controller(new ControllerStatus { Active = true, VirtualSlot = 4 }).VirtualPlayer == null, "out of range slot accepted");
            Assert(!Diagnostics.Controller(null).Available, "absent service appeared available");
        });
        Test("Diagnostic outcome parser rejects corrupt foreign oversized and future schema records", () => {
            var value = DiagnosticOutcome(); string sid = value.Sid;
            Assert(Diagnostics.ParseOutcome(Protocol.Encode(value), sid).Token == value.Token, "valid result rejected");
            MustFail(() => Diagnostics.ParseOutcome("{broken", sid));
            MustFail(() => Diagnostics.ParseOutcome(new string('x', Diagnostics.MaxExportCharacters + 1), sid));
            MustFail(() => Diagnostics.ParseOutcome(Protocol.Encode(value), "S-1-5-21-999"));
            value.Version = 2; MustFail(() => Diagnostics.ParseOutcome(Protocol.Encode(value), sid));
            value.Version = 1; value.Token = "not-a-token"; MustFail(() => Diagnostics.ParseOutcome(Protocol.Encode(value), sid));
            value.Token = Guid.NewGuid().ToString("N"); value.CapturedUtc = long.MaxValue; MustFail(() => Diagnostics.ParseOutcome(Protocol.Encode(value), sid));
        });
        Test("Diagnostic DTO has no sensitive raw recovery fields after JSON round trip", () => {
            var raw = DiagnosticOutcome(); raw.FailureStage = "disable"; raw.Failure = "Configuration Manager 0x17 path C:\\Users\\Secret";
            var report = DiagnosticFixture(); report.Recovery = Diagnostics.Recovery(raw, "present", "present", "Disabling", "absent");
            string json = Diagnostics.Export(report, "0.1.13.0"); var copy = Protocol.Decode<DiagnosticReport>(json);
            Assert(copy.Recovery.Error.Contains("0x17") && copy.Recovery.PnpPhase == "Disabling", "round trip lost useful result");
            Assert(!json.Contains(raw.Token) && !json.Contains(raw.Sid) && !json.Contains("Secret") && !json.Contains("\"Device\":") && !json.Contains("ApplicationPath"), "raw protected data serialized");
        });
        Test("Diagnostic snapshot does not mutate its cached input or retain its object references", () => {
            var cached = new ControllerStatus { Active = true, Connected = true, VirtualSlot = 0, Frames = 12, Error = "Sensitive error" };
            var projected = Diagnostics.Controller(cached); cached.VirtualSlot = 1; cached.Frames = 99;
            Assert(projected.VirtualPlayer == 1 && projected.Frames == 12 && cached.Error == "Sensitive error", "projection mutated or aliased cached input");
            var report = DiagnosticFixture(); var safe = Diagnostics.SafeCopy(report, "0.1.13.0"); report.Controller.Active = true;
            Assert(!safe.Controller.Active && !ReferenceEquals(report.Recovery, safe.Recovery), "safe report aliases mutable source");
        });
        Test("Diagnostic capture distinguishes pending unreadable invalid and absent journals", () => {
            var report = DiagnosticFixture(); report.Recovery = Diagnostics.Recovery(null, "invalid", "present", "Recovering", "unreadable");
            var copy = Diagnostics.SafeCopy(report, "0.1.13.0");
            Assert(copy.Recovery.LastResult == "invalid" && copy.Recovery.PnpJournal == "present" && copy.Recovery.HidHideJournal == "unreadable", "unreadable recovery appeared absent");
            Assert(!copy.Recovery.PhysicalRecovered && !copy.Recovery.RecoveryOperationEnded, "missing outcome invented recovery");
            report.HidHide.State = "deferred"; Assert(Diagnostics.Describe(report).Contains("consulta aplazada"), "preparation sampling pause hidden");
        });
        Test("Diagnostic version mismatches and unknown report versions remain explicit", () => {
            var report = DiagnosticFixture(); report.WidgetVersion = "0.1.99.0";
            Assert(Diagnostics.Describe(report).Contains("versiones del helper y del widget son diferentes"), "mismatched versions hidden");
            report.Version = 2; MustFail(() => Diagnostics.Export(report, "0.1.13.0"));
            Assert(Diagnostics.Time("2026-10-15T12:00:00-06:00").StartsWith("2026-10-15T18:00:00"), "timestamp not normalized to UTC");
        });
    }
    private static PhysicalRecoveryOutcome DiagnosticOutcome() => new PhysicalRecoveryOutcome { Token = Guid.NewGuid().ToString("N"), Sid = "S-1-5-21-100-200-300-400", CapturedUtc = DateTime.UtcNow.Ticks, Phase = "Enabled" };
    private static DiagnosticReport DiagnosticFixture() => new DiagnosticReport { CapturedAtUtc = DateTime.UtcNow.ToString("O"), HelperVersion = "0.1.13.0", WidgetVersion = "0.1.13.0",
        Controller = Diagnostics.Controller(new ControllerStatus()), Recovery = Diagnostics.Recovery(null, "absent", "absent", null, "absent"),
        HidHide = new DiagnosticHidHide { State = "present", ApplicationCount = 19, HiddenDeviceCount = 0 } };
}
