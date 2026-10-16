// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LegionTweaks;

internal static partial class Program
{
    private static void StickMousePreferencesTests()
    {
        Test("Absent mouse preferences migrate to known defaults without creating a file or enabling output", () => {
            var file = new MousePreferenceFileFake(); var output = new MouseOutputFake();
            var service = new StickMouseService(output, () => 0, new StickMousePreferenceStore(file));
            var status = service.GetStatus();
            Assert(status.Sensitivity == 8 && status.Deadzone == 15 && !status.Enabled && !status.Available
                && file.Writes == 0 && output.Inspections == 0 && file.Bytes == null, "migration saved or enabled cursor state");
        });
        Test("Mouse preferences persist only explicit bounded settings and always reload with the mode off", () => {
            var file = new MousePreferenceFileFake(); var store = new StickMousePreferenceStore(file);
            var first = new StickMouseService(new MouseOutputFake(), () => 0, store);
            first.SetAvailable(true); first.Configure(true, 20, 30);
            string json = Encoding.UTF8.GetString(file.Bytes);
            Assert(!json.Contains("Enabled") && !json.Contains("Active") && !json.Contains("Available"), "runtime activation state leaked into preferences");
            var second = new StickMouseService(new MouseOutputFake(), () => 0, new StickMousePreferenceStore(file));
            var status = second.GetStatus();
            Assert(status.Sensitivity == 20 && status.Deadzone == 30 && !status.Enabled && !status.Active
                && !status.Available && !status.WaitingForNeutral, "restart restored output or lost settings");
            second.SetAvailable(true);
            Assert(!second.GetStatus().Enabled, "virtual availability implicitly restored the saved mouse mode");
        });
        Test("Legacy extra Enabled data is never loaded or written as cursor activation", () => {
            var file = new MousePreferenceFileFake { Bytes = Encoding.UTF8.GetBytes("{\"Version\":1,\"Sensitivity\":12,\"Deadzone\":20,\"Enabled\":true}") };
            var service = new StickMouseService(new MouseOutputFake(), () => 0, new StickMousePreferenceStore(file));
            service.SetAvailable(true);
            Assert(!service.GetStatus().Enabled && service.GetStatus().Sensitivity == 12 && file.Writes == 0,
                "an unknown persisted activation field enabled output or changed the source file");
            service.Configure(false, 12, 20);
            Assert(!Encoding.UTF8.GetString(file.Bytes).Contains("Enabled"), "explicit save retained an old runtime-state field");
        });
        Test("Corrupt mouse preferences remain untouched on load and are backed up only by an explicit valid save", () => {
            var invalidFiles = new[] {
                Encoding.UTF8.GetBytes("not json"), Encoding.UTF8.GetBytes("null"), new byte[0],
                Encoding.UTF8.GetBytes("{\"Version\":1,\"Sensitivity\":12}"),
                Encoding.UTF8.GetBytes("{\"Version\":2,\"Sensitivity\":12,\"Deadzone\":20}"),
                Encoding.UTF8.GetBytes("{\"Version\":1,\"Sensitivity\":0,\"Deadzone\":20}"),
                new byte[] { 0xff, 0xfe, 0xff }, new byte[1025]
            };
            foreach (var invalid in invalidFiles)
            {
                var file = new MousePreferenceFileFake { Bytes = invalid.ToArray() };
                var service = new StickMouseService(new MouseOutputFake(), () => 0, new StickMousePreferenceStore(file));
                var status = service.GetStatus();
                Assert(status.Sensitivity == 8 && status.Deadzone == 15 && !status.Enabled && !string.IsNullOrEmpty(status.Error)
                    && file.Writes == 0 && file.Bytes.SequenceEqual(invalid), "load clobbered corrupt data or hid the fallback");
                MustFail(() => service.Configure(false, 0, 20));
                Assert(file.Writes == 0, "invalid explicit settings overwrote damaged preferences");
                service.Configure(false, 1, 5);
                Assert(file.Backups.Count == 1 && file.Backups[0].SequenceEqual(invalid)
                    && StickMousePreferences.Decode(file.Bytes).Sensitivity == 1 && service.GetStatus().Error == null,
                    "explicit recovery lost the original corrupt bytes or failed to clear the load warning");
            }
        });
        Test("Availability and validation checks precede preference writes", () => {
            var file = new MousePreferenceFileFake();
            var service = new StickMouseService(new MouseOutputFake(), () => 0, new StickMousePreferenceStore(file));
            MustFail(() => service.Configure(true, 12, 20));
            foreach (var pair in new[] { new[] { 0, 15 }, new[] { 21, 15 }, new[] { 8, 4 }, new[] { 8, 31 } })
                MustFail(() => service.Configure(false, pair[0], pair[1]));
            Assert(file.Writes == 0 && !service.GetStatus().Enabled, "rejected availability or bounds persisted settings");
        });
        Test("Preference write and access failures cannot prevent an explicit mouse OFF request", () => {
            foreach (bool accessFailure in new[] { false, true })
            {
                long now = 0; var file = new MousePreferenceFileFake(); var output = new MouseOutputFake();
                var service = new StickMouseService(output, () => now, new StickMousePreferenceStore(file));
                service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
                byte[] before = file.Bytes.ToArray();
                file.ThrowRead = accessFailure; file.ThrowWrite = !accessFailure;
                MustFail(() => service.Configure(false, 20, 30));
                var status = service.GetStatus();
                Assert(!status.Enabled && !status.Active && status.Sensitivity == 8 && status.Deadzone == 15
                    && file.Bytes.SequenceEqual(before) && !string.IsNullOrEmpty(status.Error), "failed persistence retained mouse output or partially applied settings");
                now = 16; service.Process(MouseFrame(32767, 0));
                Assert(output.Attempts == 0, "OFF continued emitting motion after a preference save failure");
            }
        });
        Test("Failed enabled-setting saves preserve the previous runtime values and neutral state", () => {
            long now = 0; var file = new MousePreferenceFileFake(); var output = new MouseOutputFake();
            var service = new StickMouseService(output, () => now, new StickMousePreferenceStore(file));
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
            file.ThrowWrite = true; MustFail(() => service.Configure(true, 20, 30));
            Assert(service.GetStatus().Enabled && service.GetStatus().Active && service.GetStatus().Sensitivity == 8,
                "failed ON settings save partially applied runtime values");
            now = 16; service.Process(MouseFrame(32767, 0));
            Assert(output.Attempts == 1, "failed settings save reset the previously armed state");
        });
        Test("A retained preference save does not block frames or availability and cannot reenable after disconnect", () => {
            foreach (bool reconnect in new[] { false, true })
            {
                var file = new MousePreferenceFileFake(); var output = new MouseOutputFake();
                var service = new StickMouseService(output, () => 0, new StickMousePreferenceStore(file));
                service.SetAvailable(true);
                using (var entered = new ManualResetEventSlim())
                using (var release = new ManualResetEventSlim())
                {
                    file.BeforeWrite = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("retained test save timed out"); };
                    var saving = Task.Run(() => MustFail(() => service.Configure(true, 20, 30)));
                    try
                    {
                        Assert(entered.Wait(2000), "preference save did not enter the test barrier");
                        var frame = Task.Run(() => service.Process(MouseFrame(10000, 0)));
                        Assert(frame.Wait(2000) && frame.Result.RX == 10000, "storage held the real-time frame gate");
                        var stopping = Task.Run(() => service.SetAvailable(false));
                        Assert(stopping.Wait(2000) && !service.GetStatus().Enabled, "storage delayed Stop or virtual availability");
                        if (reconnect) service.SetAvailable(true);
                    }
                    finally { release.Set(); }
                    Assert(saving.Wait(2000) && !service.GetStatus().Enabled && !service.GetStatus().Active,
                        "late save reenabled cursor output after disconnect/reconnect");
                }
            }
        });
        Test("OFF takes effect while an older ON save is retained and stays off when its own save fails", () => {
            var file = new MousePreferenceFileFake(); var output = new MouseOutputFake();
            var service = new StickMouseService(output, () => 0, new StickMousePreferenceStore(file));
            service.SetAvailable(true); service.Configure(true, 8, 15); service.Process(MouseFrame(0, 0));
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                file.BeforeWrite = () => { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException("retained test save timed out"); };
                var changing = Task.Run(() => MustFail(() => service.Configure(true, 20, 30)));
                Task<string> disabling = null;
                try
                {
                    Assert(entered.Wait(2000), "preference save did not enter the test barrier");
                    disabling = Task.Run(() => MustFail(() => service.Configure(false, 12, 20)));
                    Assert(SpinWait.SpinUntil(() => !service.GetStatus().Enabled, 2000), "OFF waited behind the retained save");
                    Assert(!disabling.IsCompleted && output.Attempts == 0, "test did not retain the save or emitted movement after OFF");
                    file.ThrowWrite = true;
                }
                finally { release.Set(); }
                Assert(changing.Wait(2000) && disabling != null && disabling.Wait(2000) && !service.GetStatus().Enabled,
                    "failed queued OFF save allowed an old ON request to commit");
            }
        });
        Test("Mouse frame processing stop and availability changes never perform preference I/O", () => {
            long now = 0; var file = new MousePreferenceFileFake();
            var service = new StickMouseService(new MouseOutputFake(), () => now, new StickMousePreferenceStore(file));
            service.SetAvailable(true); service.Configure(true, 12, 20);
            int reads = file.Reads, writes = file.Writes;
            for (int i = 0; i < 100; i++) { now += 16; service.Process(MouseFrame(i == 0 ? (short)0 : (short)20000, 0)); }
            service.Disable(); service.SetAvailable(false); service.SetAvailable(true);
            Assert(file.Reads == reads && file.Writes == writes && !service.GetStatus().Enabled,
                "hot path or automatic safety shutdown touched preferences or reenabled output");
        });
    }

    private sealed class MousePreferenceFileFake : IStickMousePreferenceFile
    {
        internal byte[] Bytes;
        internal int Reads, Writes;
        internal bool ThrowRead, ThrowWrite;
        internal Action BeforeWrite;
        internal readonly List<byte[]> Backups = new List<byte[]>();
        public byte[] Read()
        {
            Reads++;
            if (ThrowRead) throw new UnauthorizedAccessException("acceso rechazado");
            if (Bytes != null && Bytes.Length > 1024) throw new InvalidDataException("demasiado grande");
            return Bytes?.ToArray();
        }
        public void WriteAtomic(byte[] bytes, bool backupInvalid)
        {
            BeforeWrite?.Invoke();
            if (ThrowWrite) throw new IOException("escritura rechazada");
            if (backupInvalid && Bytes != null) Backups.Add(Bytes.ToArray());
            Bytes = bytes.ToArray(); Writes++;
        }
    }
}
