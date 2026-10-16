// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Security;
using LegionTweaks;

internal static partial class Program
{
    private const string VetoTestEndpoint = @"USB\VID_17EF&PID_61EB&MI_00\ANONYMIZED_TEST_1";
    private static void PnpVetoTests()
    {
        Test("PnP event window uses exact disable worker start and first failure instead of rollback capture", () => {
            var result = VetoOutcome(); result.CapturedUtc = result.DisableFailedUtc + TimeSpan.FromMinutes(5).Ticks;
            var window = PnpVetoRules.Window(result);
            Assert(window != null && window.StartUtc == result.DisableStartedUtc && window.EndUtc == result.DisableFailedUtc, "rollback changed event window");
            string xpath = PnpVetoRules.XPath(window, PnpVetoRules.SystemChannel);
            Assert(xpath.Contains("EventID=225") && xpath.Contains("@SystemTime") && xpath.Contains("2026-01-02T10:00:01.0000000Z") && !xpath.Contains("ANONYMIZED"), "XPath lost UTC or leaked endpoint");
            Assert(PnpVetoRules.XPath(window, PnpVetoRules.DeviceChannel).Contains("EventID=1000"), "wrong device management event");
        });
        Test("PnP correlation refuses legacy foreign endpoint oversized window and future malformed metadata", () => {
            Assert(PnpVetoRules.Window(new PhysicalRecoveryOutcome { CapturedUtc = DateTime.UtcNow.Ticks }) == null, "legacy result guessed device or interval");
            var result = VetoOutcome(); result.Endpoint = @"USB\VID_045E&PID_028E\FOREIGN"; Assert(PnpVetoRules.Window(result) == null, "foreign virtual endpoint accepted");
            result = VetoOutcome(); result.DisableStartedUtc = result.OperationStartedUtc - 1; Assert(PnpVetoRules.Window(result) == null, "worker before operation accepted");
            result = VetoOutcome(); result.DisableFailedUtc = result.OperationStartedUtc + TimeSpan.FromSeconds(46).Ticks; result.CapturedUtc = result.DisableFailedUtc;
            Assert(PnpVetoRules.Window(result) == null, "window exceeded 45 seconds");
            result = VetoOutcome(); result.CapturedUtc = result.DisableFailedUtc - 1; Assert(PnpVetoRules.Window(result) == null, "capture preceding failure accepted");
        });
        Test("PnP fixtures correlate exact endpoint time provider channel and event kind", () => {
            var result = VetoOutcome(); var window = PnpVetoRules.Window(result); var analysis = new PnpVetoAnalysis(window);
            string foreign = @"USB\VID_17EF&PID_61EB&MI_00\ANONYMIZED_TEST_2";
            analysis.Observe(VetoXml(225, foreign, result.DisableStartedUtc + 1, @"C:\Windows\explorer.exe"), PnpVetoRules.SystemChannel);
            analysis.Observe(VetoXml(225, VetoTestEndpoint, result.DisableStartedUtc - 1, @"C:\Windows\explorer.exe"), PnpVetoRules.SystemChannel);
            analysis.Observe(VetoXml(225, VetoTestEndpoint, result.DisableFailedUtc + 1, @"C:\Windows\explorer.exe"), PnpVetoRules.SystemChannel);
            analysis.Observe(VetoXml(225, VetoTestEndpoint, result.DisableStartedUtc + 1, @"C:\Windows\explorer.exe").Replace("Microsoft-Windows-Kernel-PnP", "ForeignProvider"), PnpVetoRules.SystemChannel);
            analysis.Observe(VetoXml(225, VetoTestEndpoint, result.DisableStartedUtc + 1, @"C:\Windows\explorer.exe"), PnpVetoRules.DeviceChannel);
            Assert(analysis.Snapshot().MatchedEvents == 0, "foreign or stale event was correlated");
            analysis.Observe(VetoXml(225, VetoTestEndpoint.ToLowerInvariant(), result.DisableStartedUtc, @"\Device\HarddiskVolume9\Windows\explorer.exe"), PnpVetoRules.SystemChannel);
            analysis.Complete(PnpVetoRules.SystemChannel); analysis.Complete(PnpVetoRules.DeviceChannel);
            Assert(analysis.Snapshot().MatchedEvents == 1 && analysis.Snapshot().Applications.Single() == "explorer.exe", "exact inclusive match rejected");
        });
        Test("PnP outstanding-open veto and allowlisted process names survive without private XML", () => {
            var result = VetoOutcome(); var analysis = new PnpVetoAnalysis(PnpVetoRules.Window(result));
            analysis.Observe(VetoXml(225, VetoTestEndpoint, result.DisableStartedUtc + 1, @"\Device\HarddiskVolume9\Private\GameInputSvc.exe"), PnpVetoRules.SystemChannel);
            analysis.Observe(VetoXml(225, VetoTestEndpoint, result.DisableStartedUtc + 2, @"C:\Users\PrivateName\SecretProgram.exe"), PnpVetoRules.SystemChannel);
            analysis.Observe(VetoXml(1000, VetoTestEndpoint, result.DisableFailedUtc, null, 5), PnpVetoRules.DeviceChannel);
            analysis.Complete(PnpVetoRules.SystemChannel); analysis.Complete(PnpVetoRules.DeviceChannel);
            var value = analysis.Snapshot(); string json = Protocol.Encode(value);
            Assert(value.VetoObserved == true && value.OutstandingOpenObserved == true && value.OtherApplicationEvents == 1 && value.MatchedEvents == 3, "useful veto evidence lost");
            foreach (string secret in new[] { "Private", "SecretProgram", "ANONYMIZED", "HarddiskVolume", "CommandLine", "S-1-5", "Token", "Endpoint", "DeviceInstance" })
                Assert(!json.Contains(secret), "private event detail escaped projection: " + secret);
            string text = PnpVetoRules.Describe(value);
            Assert(text.Contains("accesos al dispositivo aún abiertos") && text.Contains("no demuestra una causa única"), "veto scope or limitation missing");
        });
        Test("PnP zero events unavailable logs and malformed records remain unknown rather than no veto", () => {
            var analysis = new PnpVetoAnalysis(PnpVetoRules.Window(VetoOutcome()));
            analysis.Complete(PnpVetoRules.SystemChannel); analysis.Complete(PnpVetoRules.DeviceChannel);
            var empty = analysis.Snapshot();
            Assert(empty.State == "unknown" && empty.VetoObserved == null && !empty.PartialRead, "empty logs invented absent veto");
            Assert(PnpVetoRules.Describe(empty).Contains("no descarta un veto"), "empty logs overstated conclusion");
            analysis.Observe("<broken", PnpVetoRules.SystemChannel);
            Assert(analysis.Snapshot().VetoObserved == null && analysis.Snapshot().PartialRead, "invalid XML appeared complete");
            Assert(PnpVetoRules.SafeCopy(PnpVetoRules.Unknown("legacy")).State == "legacy", "legacy correlation became known");
        });
        Test("PnP read limits stop records and large XML and disclose partial findings", () => {
            var result = VetoOutcome(); var analysis = new PnpVetoAnalysis(PnpVetoRules.Window(result));
            string xml = VetoXml(225, VetoTestEndpoint, result.DisableStartedUtc, @"C:\Windows\dwm.exe");
            for (int i = 0; i < PnpVetoRules.MaximumRecords; i++) Assert(analysis.Observe(xml, PnpVetoRules.SystemChannel), "record budget ended early");
            Assert(!analysis.Observe(xml, PnpVetoRules.SystemChannel) && analysis.Snapshot().MatchedEvents == PnpVetoRules.MaximumRecords, "unbounded event count");
            Assert(analysis.Snapshot().PartialRead, "truncation concealed");
            var large = new PnpVetoAnalysis(PnpVetoRules.Window(result));
            Assert(!large.Observe(new string('x', PnpVetoRules.MaximumRecordCharacters + 1), PnpVetoRules.SystemChannel) && large.Snapshot().VetoObserved == null, "oversized XML processed");
            var dtd = new PnpVetoAnalysis(PnpVetoRules.Window(result));
            dtd.Observe("<!DOCTYPE Event [<!ENTITY data SYSTEM 'file:///Private/secret'>]><Event>&data;</Event>", PnpVetoRules.SystemChannel);
            Assert(dtd.Snapshot().VetoObserved == null && dtd.Snapshot().PartialRead, "external entity accepted");
        });
        Test("PnP export reconstructs allowlisted labels and never reports rollback as active virtual", () => {
            var result = VetoOutcome(); var analysis = new PnpVetoAnalysis(PnpVetoRules.Window(result));
            analysis.Observe(VetoXml(1000, VetoTestEndpoint, result.DisableStartedUtc, null, 5), PnpVetoRules.DeviceChannel);
            var value = analysis.Snapshot(); value.Applications = new[] { @"C:\Users\Private\App.exe", "GameInputSvc.exe", "PrivateApp.exe" };
            var report = DiagnosticFixture(); report.PnpVeto = value; report.Recovery.PhysicalRecovered = report.Recovery.RecoveryOperationEnded = true;
            string json = Diagnostics.Export(report, "0.1.15.0"); var copy = Protocol.Decode<DiagnosticReport>(json);
            Assert(copy.PnpVeto.Applications.Single() == "GameInputSvc.exe" && !json.Contains("Private"), "unsafe labels exported");
            Assert(copy.Controller.VirtualPlayer == null && Diagnostics.Describe(copy).Contains("no confirma la activación"), "rollback invented successful virtual");
        });
        Test("PnP protected outcome captures exact first disable context and preserves it through recovery", () => {
            long started = VetoOutcome().OperationStartedUtc;
            var lease = new PhysicalRecoveryLease { Token = Guid.NewGuid().ToString("N"), Sid = "S-1-5-21-1-2-3-1001", Device = VetoTestEndpoint,
                OwnerPid = 100, OwnerStarted = started - TimeSpan.TicksPerMinute, CreatedUtc = started,
                ExpiresUtc = started + TimeSpan.FromMilliseconds(PhysicalRecoveryRules.LeaseMilliseconds).Ticks,
                OriginalStatus = PhysicalRecoveryRules.Started, Phase = "Disabling", WorkerPid = 101, WorkerStarted = started + TimeSpan.TicksPerSecond };
            var value = PhysicalRecoveryRules.RecordOutcome(null, lease, lease.Sid, started + 4 * TimeSpan.TicksPerSecond, "disable", "Synthetic CM failure", 1);
            lease.Phase = "Enabled"; lease.WorkerPid = 102; lease.WorkerStarted = started + 10 * TimeSpan.TicksPerSecond;
            value = PhysicalRecoveryRules.RecordOutcome(value, lease, lease.Sid, started + 20 * TimeSpan.TicksPerSecond, "enable", "Later synthetic failure", 2, true, true);
            Assert(value.Endpoint == VetoTestEndpoint && value.DisableStartedUtc == started + TimeSpan.TicksPerSecond
                && value.DisableFailedUtc == started + 4 * TimeSpan.TicksPerSecond && value.OperationStartedUtc == started, "first disable context overwritten");
            Assert(PnpVetoRules.Window(value).EndUtc != value.CapturedUtc, "recovery timestamp used to correlate veto");
        });
    }
    private static PhysicalRecoveryOutcome VetoOutcome()
    {
        long start = new DateTime(2026, 1, 2, 10, 0, 0, DateTimeKind.Utc).Ticks;
        return new PhysicalRecoveryOutcome { Endpoint = VetoTestEndpoint, OperationStartedUtc = start, DisableStartedUtc = start + TimeSpan.TicksPerSecond,
            DisableFailedUtc = start + 5 * TimeSpan.TicksPerSecond, CapturedUtc = start + 15 * TimeSpan.TicksPerSecond };
    }
    private static string VetoXml(int id, string endpoint, long ticks, string application = null, int type = 0)
    {
        Func<string, string> escape = value => SecurityElement.Escape(value ?? "");
        return "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Kernel-PnP'/><EventID>" + id
            + "</EventID><TimeCreated SystemTime='" + new DateTime(ticks, DateTimeKind.Utc).ToString("O") + "'/><Channel>"
            + escape(id == 225 ? PnpVetoRules.SystemChannel : PnpVetoRules.DeviceChannel) + "</Channel></System><EventData><Data Name='"
            + (id == 225 ? "DeviceInstance" : "DeviceInstanceId") + "'>" + escape(endpoint) + "</Data>"
            + (id == 225 ? "<Data Name='ProcessName'>" + escape(application) + "</Data><Data Name='CommandLine'>Private arguments omitted</Data>"
                : "<Data Name='VetoType'>" + type + "</Data><Data Name='VetoName'>" + escape(endpoint) + "</Data>") + "</EventData></Event>";
    }
}
