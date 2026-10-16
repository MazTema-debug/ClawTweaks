// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LegionTweaks;

internal static partial class Program
{
    private static void ControllerPreparationTraceTests()
    {
        Test("Preparation trace stops at 64 events and keeps a bounded UTF8 report", () => {
            var trace = PreparationTraceFixture();
            for (int i = 0; i < ControllerPreparationTraceRules.MaximumEvents; i++)
                Assert(trace.Record(ControllerPreparationStage.BeforeEnvironmentInspection, DateTime.UtcNow.Ticks, i, null), "event rejected before budget");
            Assert(!trace.HasCapacity && !trace.Record(ControllerPreparationStage.Finished, DateTime.UtcNow.Ticks, 100, null), "trace exceeded event budget");
            var value = trace.Snapshot(); byte[] data = ControllerPreparationTraceRules.Serialize(value);
            Assert(value.Events.Length == 64 && value.Truncated && data.Length < ControllerPreparationTraceRules.MaximumBytes, "trace unbounded");
            Assert(data.Length > 0 && data[0] == (byte)'{' && Encoding.UTF8.GetString(data).Contains("BeforeEnvironmentInspection"), "encoding or readable stage lost");
        });
        Test("Preparation trace rejects unknown stages invalid clocks and impossible open facts", () => {
            var trace = PreparationTraceFixture();
            Assert(!trace.Record((ControllerPreparationStage)999, DateTime.UtcNow.Ticks, 0, null), "unknown stage accepted");
            Assert(!trace.Record(ControllerPreparationStage.CommandEntered, 0, 0, null), "invalid UTC accepted");
            Assert(!trace.Record(ControllerPreparationStage.CommandEntered, DateTime.UtcNow.Ticks, -1, null), "negative elapsed accepted");
            Assert(!trace.Record(ControllerPreparationStage.PhysicalOpened, DateTime.UtcNow.Ticks, 0, null, physicalOpened: true), "open completion invented request");
            Assert(trace.Snapshot().Events.Length == 0, "rejected evidence changed trace");
            trace.Record(ControllerPreparationStage.CommandEntered, DateTime.UtcNow.Ticks, 10, null);
            Assert(!trace.Record(ControllerPreparationStage.Finished, DateTime.UtcNow.Ticks, 9, null), "monotonic clock reversal accepted");
        });
        Test("Preparation trace records UTC adjustment without reversing monotonic ordering", () => {
            var trace = PreparationTraceFixture(); long now = DateTime.UtcNow.Ticks;
            trace.Record(ControllerPreparationStage.CommandEntered, now, 0, null);
            Assert(trace.Record(ControllerPreparationStage.BeforeDisable, now - TimeSpan.TicksPerHour, 20, null), "wall clock adjustment erased evidence");
            var value = trace.Snapshot();
            Assert(value.Events[0].ElapsedMilliseconds == 0 && value.Events[1].ElapsedMilliseconds == 20 && value.Events[1].Sequence == 2, "ordering lost");
            ControllerPreparationTraceRules.Serialize(value);
        });
        Test("Preparation module snapshots retain only flags never names paths or frames", () => {
            var flags = ControllerPreparationTraceRules.Modules(new[] { "sDl2.DlL", "XInput1_4.dll", "ViGEmClient.dll", "C:\\Users\\Private\\secret.dll", "S-1-5-21-Secret", "HiddenToken" });
            Assert(flags.SnapshotAvailable && flags.SdlLoaded && flags.XInputLoaded && flags.ViGEmLoaded, "module classification failed");
            var trace = PreparationTraceFixture(); trace.Record(ControllerPreparationStage.AfterVigemClient, DateTime.UtcNow.Ticks, 0, flags);
            string json = Encoding.UTF8.GetString(ControllerPreparationTraceRules.Serialize(trace.Snapshot()));
            foreach (string secret in new[] { "Private", "secret.dll", "S-1-5", "HiddenToken", "\\\\", "Button", "Frame", "DeviceId", "Sid", "Token" })
                Assert(!json.Contains(secret), "private or irrelevant input escaped: " + secret);
            var path = ControllerPreparationTraceRules.Modules(new[] { "C:\\SDL2.dll", "OtherXInput.dll" });
            Assert(path.SnapshotAvailable && !path.SdlLoaded && !path.XInputLoaded, "paths or unrelated names matched");
        });
        Test("Preparation module failures and oversized inventories stay explicitly unavailable", () => {
            var failed = ControllerPreparationTraceRules.Modules(PreparationBrokenModules());
            Assert(!failed.SnapshotAvailable && !failed.SdlLoaded && !failed.XInputLoaded, "partial inventory represented as absent");
            var names = new string[ControllerPreparationTraceRules.MaximumModules + 1]; names[0] = "SDL2.dll";
            Assert(!ControllerPreparationTraceRules.Modules(names).SnapshotAvailable, "module inventory unbounded");
            Assert(!ControllerPreparationTraceRules.Modules(null).SnapshotAvailable, "missing snapshot represented as available");
        });
        Test("Preparation trace open facts are irreversible and snapshots do not alias", () => {
            var trace = PreparationTraceFixture(); var flags = new ControllerPreparationModuleFlags { SnapshotAvailable = true, SdlLoaded = true };
            trace.Record(ControllerPreparationStage.BeforePhysicalOpen, DateTime.UtcNow.Ticks, 0, flags, openRequested: true);
            trace.Record(ControllerPreparationStage.PhysicalOpened, DateTime.UtcNow.Ticks, 1, flags, physicalOpened: true);
            trace.Record(ControllerPreparationStage.AfterReaderDispose, DateTime.UtcNow.Ticks, 2, flags, openRequested: false, physicalOpened: false);
            flags.SdlLoaded = false;
            var value = trace.Snapshot(); value.Events[0].Modules.SdlLoaded = false; value.Events[1].Stage = "Secret";
            var actual = trace.Snapshot();
            Assert(actual.Events[0].Modules.SdlLoaded && actual.Events[1].Stage == "PhysicalOpened", "snapshot aliases mutable input");
            Assert(actual.Events[2].PhysicalOpenRequested && actual.Events[2].PhysicalOpened, "historical open fact was reset by disposal");
        });
        Test("Preparation storage failures do not clear evidence or prevent later writes", () => {
            var trace = PreparationTraceFixture(); trace.Record(ControllerPreparationStage.CommandEntered, DateTime.UtcNow.Ticks, 0, null);
            string persisted = "last good trace";
            Assert(!trace.TryWrite(_ => { throw new IOException("Private storage path"); }), "failed storage reported success");
            Assert(persisted == "last good trace" && trace.Snapshot().Events.Length == 1, "storage failure cleared previous evidence");
            Assert(trace.TryWrite(data => persisted = Encoding.UTF8.GetString(data)) && persisted.Contains("CommandEntered"), "later optional write failed");
            Assert(!persisted.Contains("Private storage path") && !trace.TryWrite(null), "storage exception leaked or missing writer accepted");
        });
        Test("Preparation serialization rejects free text and malformed report metadata", () => {
            var trace = PreparationTraceFixture(); trace.Record(ControllerPreparationStage.CommandEntered, DateTime.UtcNow.Ticks, 0, null);
            var report = trace.Snapshot(); report.Events[0].Stage = "C:\\Users\\Private\\file";
            MustFail(() => ControllerPreparationTraceRules.Serialize(report));
            report = trace.Snapshot(); report.Events[0].Stage = "999"; MustFail(() => ControllerPreparationTraceRules.Serialize(report));
            report = trace.Snapshot(); report.RunId = "authorization-token"; MustFail(() => ControllerPreparationTraceRules.Serialize(report));
            report = trace.Snapshot(); report.ProcessId = 0; MustFail(() => ControllerPreparationTraceRules.Serialize(report));
            report = trace.Snapshot(); report.Events[0].Sequence = 2; MustFail(() => ControllerPreparationTraceRules.Serialize(report));
            report = trace.Snapshot(); report.Events[0].Modules = new ControllerPreparationModuleFlags { SnapshotAvailable = false, SdlLoaded = true };
            var clean = Protocol.Decode<ControllerPreparationTraceReport>(Encoding.UTF8.GetString(ControllerPreparationTraceRules.Serialize(report)));
            Assert(!clean.Events[0].Modules.SnapshotAvailable && !clean.Events[0].Modules.SdlLoaded, "unknown snapshot retained asserted module flags");
        });
        Test("New preparation invalidates delayed writers from prior operations", () => {
            var generations = new ControllerPreparationTraceGeneration(); long older = generations.Begin();
            Assert(generations.IsCurrent(older) && !generations.IsCurrent(0), "initial ownership wrong");
            Action delayedOldWriter = () => Assert(!generations.IsCurrent(older), "old writer remained authorized to publish");
            long newer = generations.Begin(); delayedOldWriter();
            Assert(generations.IsCurrent(newer) && newer != older, "new ownership not unique");
            long newest = generations.Begin(); Assert(!generations.IsCurrent(newer) && generations.IsCurrent(newest), "multiple writers current");
        });
    }
    private static ControllerPreparationTraceBuffer PreparationTraceFixture() => new ControllerPreparationTraceBuffer(Guid.NewGuid().ToString("N"), 1234, DateTime.UtcNow.Ticks);
    private static IEnumerable<string> PreparationBrokenModules()
    {
        yield return "SDL2.dll"; throw new InvalidOperationException("Private process module path");
    }
}
