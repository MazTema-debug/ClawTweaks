// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using LegionTweaks;

internal static partial class Program
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr security);
    private static FpsY3InspectionResult InspectSynthetic(FpsY3LeaseRecord record, string expectedSid = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Encode(record));
        return InspectSyntheticBytes(bytes, expectedSid ?? record.Sid);
    }
    private static FpsY3InspectionResult InspectSyntheticBytes(byte[] bytes, string sid = "S-1-5-21-synthetic") =>
        FpsY3JournalInspector.InspectSnapshot(sid, () => new FpsY3InspectionSnapshot { Bytes = bytes, FileIdentity = "synthetic-file" });
    private static void InspectionBlocked(FpsY3InspectionResult result, string reason)
    {
        Assert(result.Classification == "blocked" && result.Reason == reason && !result.AuthorizesMaintenance,
            "unsafe or ambiguous inspection was accepted");
    }

    private static void FpsY3JournalInspectorTests()
    {
        Test("Y3 inspector distinguishes absence all pending phases and validated restored records without approving maintenance", () => {
            var absent = FpsY3JournalInspector.InspectSnapshot("synthetic", () => null);
            Assert(absent.Classification == "absent" && absent.ContentSha256 == null && !absent.AuthorizesMaintenance,
                "absence invented data or approved maintenance");
            foreach (string phase in new[] { "Preparing", "Original", "Applying", "Muted", "Restoring" }) {
                var r = TrialRecord(phase); var result = InspectSynthetic(r);
                Assert(result.Classification == "pending" && result.Phase == phase && result.OriginalPresent == (r.Original != null)
                    && result.ErrorPresent == false && !result.AuthorizesMaintenance, "pending phase lost");
            }
            var restored = TrialRecord("Restored"); var inspection = InspectSynthetic(restored);
            Assert(inspection.Classification == "restored_with_original" && inspection.OriginalPresent == true
                && inspection.ErrorPresent == false && inspection.ContentSha256.Length == 64
                && inspection.FileIdentitySha256.Length == 64 && !inspection.AuthorizesMaintenance, "restored original not classified");
            restored.Original = null; inspection = InspectSynthetic(restored);
            Assert(inspection.Classification == "restored_without_original" && inspection.OriginalPresent == false
                && !inspection.AuthorizesMaintenance, "missing original claimed a physical restoration");
            restored.Error = "private exception text"; inspection = InspectSynthetic(restored);
            Assert(inspection.Classification == "blocked" && inspection.Reason == "recorded_error" && inspection.ErrorPresent == true,
                "restored error was overlooked");
            restored.Error = ""; InspectionBlocked(InspectSynthetic(restored), "recorded_error");
            var pending = TrialRecord("Muted"); pending.Error = "private";
            inspection = InspectSynthetic(pending);
            Assert(inspection.Classification == "pending" && inspection.ErrorPresent == true && !inspection.AuthorizesMaintenance,
                "pending error was reported as clean");
        });

        Test("Y3 inspector reuses record and native-original validation without exposing private values", () => {
            var good = TrialRecord("Restored"); string token = good.Token;
            var result = InspectSynthetic(good); string output = Protocol.Encode(result);
            Assert(!output.Contains(good.Sid) && !output.Contains(token) && !output.Contains(good.SourcePath)
                && !output.Contains(good.Original.Container) && !output.Contains("Map3") && !output.Contains("Map4"), "private journal leaked");
            var mutations = new Action<FpsY3LeaseRecord>[] {
                r => r.Token = "invalid", r => r.Phase = "unknown", r => r.OwnerPid = 0, r => r.ExpiresUtc++,
                r => r.WatchPid = 4, r => r.SourcePath = "other", r => r.Original.Path += "other",
                r => r.Original.Map4[0]++, r => r.Original.Usb3[4] = 4, r => { r.Phase = "Muted"; r.Original = null; },
                r => r.OwnerStarted = r.CreatedUtc + 1,
                r => { r.WatchPid = 5; r.WatchStarted = r.CreatedUtc - 1; },
                r => { r.WatchPid = 5; r.WatchStarted = long.MaxValue; }
            };
            foreach (var mutate in mutations) {
                var bad = TrialCopy(good); mutate(bad); InspectionBlocked(InspectSynthetic(bad), "invalid_data");
            }
            InspectionBlocked(InspectSynthetic(good, "another-sid"), "invalid_data");
        });

        Test("Y3 inspector rejects duplicate ambiguous truncated malformed and unknown JSON fields", () => {
            string json = Protocol.Encode(TrialRecord("Restored"));
            Assert(json.Contains("\"OwnerPid\":123") && json.Contains("\"Error\":null") && json.Contains("\"Container\":"), "synthetic format changed");
            string[] malformed = {
                json.Insert(1, "\"Phase\":\"Restored\","),
                json.Insert(1, "\"\\u0050hase\":\"Restored\","),
                json.Replace("\"Container\":", "\"Container\":\"private\",\"Container\":"),
                json + "{}", json.Substring(0, json.Length - 1) + ",}", json.Substring(0, json.Length - 1),
                json.Replace("\"Sid\":", "\"UnknownSid\":"),
                json.Replace("\"OwnerPid\":123", "\"OwnerPid\":123.0"),
                json.Replace("\"OwnerPid\":123", "\"OwnerPid\":0123"),
                json.Replace("\"OwnerPid\":123", "\"OwnerPid\":1e2"),
                json.Replace("\"Error\":null", "\"Error\":\"\\ud800\""),
                json.Replace("\"Error\":null", "\"Error\":true")
            };
            foreach (string value in malformed) InspectionBlocked(InspectSyntheticBytes(Encoding.UTF8.GetBytes(value)), "invalid_data");
            InspectionBlocked(InspectSyntheticBytes(new byte[] { 0xff }), "invalid_data");
            InspectionBlocked(InspectSyntheticBytes(new byte[FpsY3JournalInspector.MaximumBytes + 1]), "invalid_data");
            InspectionBlocked(InspectSyntheticBytes(new byte[0]), "invalid_data");
        });

        Test("Y3 inspector preserves exact 64-bit timestamps and supports the serializer's UTF-8 bytes", () => {
            var record = TrialRecord("Restored");
            record.CreatedUtc = 638000000000000001L;
            record.OwnerStarted = record.CreatedUtc - 1;
            record.ExpiresUtc = record.CreatedUtc + TimeSpan.FromMilliseconds(FpsY3TrialRules.DurationMilliseconds).Ticks;
            var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(record));
            Assert(InspectSyntheticBytes(bytes).Classification == "restored_with_original", "precise ticks lost");
            var bom = new byte[bytes.Length + 3]; bom[0] = 0xef; bom[1] = 0xbb; bom[2] = 0xbf;
            Array.Copy(bytes, 0, bom, 3, bytes.Length);
            Assert(InspectSyntheticBytes(bom).Classification == "restored_with_original", "UTF-8 BOM rejected");
            record.ExpiresUtc++;
            InspectionBlocked(InspectSynthetic(record), "invalid_data");
        });

        Test("Y3 inspector verifies a stable read-only file twice without creating or changing files", () => WithRecordingDirectory(folder => {
            string path = Path.Combine(folder, "synthetic-journal.json"); var record = TrialRecord("Restored");
            var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(record)); File.WriteAllBytes(path, bytes);
            var before = File.GetLastWriteTimeUtc(path); int protectionChecks = 0, identities = 0;
            Action protect = () => { protectionChecks++; if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(); };
            var inspection = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(path, protect, stream => { identities++; return "same-file"; }));
            Assert(inspection.Classification == "restored_with_original" && protectionChecks >= 3 && identities == 2,
                "stable file was not protected and checked twice");
            Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && File.GetLastWriteTimeUtc(path) == before
                && Directory.GetFiles(folder).Length == 1, "inspection mutated the synthetic journal");
            string missing = Path.Combine(folder, "absent.json");
            inspection = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(missing, protect, stream => "unused"));
            Assert(inspection.Classification == "absent" && !File.Exists(missing), "absence created a file");
        }));

        Test("Y3 inspector fails closed on changed identity protection errors incomplete reads and occupied files", () => WithRecordingDirectory(folder => {
            string path = Path.Combine(folder, "synthetic-journal.json"); var record = TrialRecord("Restored");
            var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(record)); File.WriteAllBytes(path, bytes);
            int identities = 0;
            var result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(path, () => { }, stream => ++identities == 1 ? "first" : "different"));
            InspectionBlocked(result, "changed");
            result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(path, () => throw new UnauthorizedAccessException("private path"), stream => "unused"));
            InspectionBlocked(result, "unavailable");
            Assert(!Protocol.Encode(result).Contains("private path"), "access error leaked path");
            int checks = 0;
            result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(path, () => { if (++checks == 2) throw new IOException("private data"); }, stream => "same"));
            InspectionBlocked(result, "unavailable");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                    FpsY3JournalInspector.ReadStable(path, () => { }, stream => "same"));
                InspectionBlocked(result, "unavailable");
            }
            Assert(bytes.SequenceEqual(File.ReadAllBytes(path)), "failed inspection changed the journal");
            File.WriteAllBytes(path, new byte[FpsY3JournalInspector.MaximumBytes + 1]);
            result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(path, () => { }, stream => "same"));
            InspectionBlocked(result, "invalid_data");
            result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                FpsY3JournalInspector.ReadStable(Path.Combine(folder, "missing", "journal.json"),
                    () => throw new DirectoryNotFoundException(), stream => "unused"));
            InspectionBlocked(result, "unavailable");
        }));

        Test("Y3 inspector does not read when its synthetic journal mutex is busy or abandoned", () => {
            string name = "LegionTweaks.InspectorTest." + Guid.NewGuid().ToString("N");
            var record = TrialRecord("Restored"); int reads = 0;
            Func<FpsY3InspectionSnapshot> read = () => { reads++; return new FpsY3InspectionSnapshot {
                Bytes = Encoding.UTF8.GetBytes(Protocol.Encode(record)), FileIdentity = "synthetic" }; };
            using (var keep = new Mutex(false, name)) using (var acquired = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim()) {
                var owner = new Thread(() => { using (var held = new Mutex(false, name)) { held.WaitOne(); acquired.Set(); release.Wait(); held.ReleaseMutex(); } });
                owner.Start(); Assert(acquired.Wait(5000), "fixture did not acquire mutex");
                try { InspectionBlocked(FpsY3JournalInspector.InspectUnderMutex(name, record.Sid, read, 0), "busy"); }
                finally { release.Set(); owner.Join(5000); }
                Assert(reads == 0, "busy inspector read journal");
                var abandoned = new Thread(() => { using (var held = new Mutex(false, name)) { held.WaitOne(); } });
                abandoned.Start(); Assert(abandoned.Join(5000), "fixture owner did not exit");
                InspectionBlocked(FpsY3JournalInspector.InspectUnderMutex(name, record.Sid, read, 0), "abandoned_mutex");
                Assert(reads == 0, "abandoned inspector read journal");
                Assert(FpsY3JournalInspector.InspectUnderMutex(name, record.Sid, read, 0).Classification == "restored_with_original"
                    && reads == 1, "inspector did not release synthetic mutex");
            }
        });

        Test("Y3 inspector command rejects extra or misspelled arguments before any read and sanitizes failures", () => {
            int calls = 0, code; var output = new StringWriter();
            Func<FpsY3InspectionResult> inspect = () => { calls++; throw new Exception("private SID and device path"); };
            Assert(!FpsY3JournalInspector.TryDispatch(new[] { "status" }, output, inspect, out code) && calls == 0 && output.ToString() == "",
                "ordinary helper request entered inspector");
            Assert(FpsY3JournalInspector.TryDispatch(new[] { FpsY3JournalInspector.Command, "untrusted-path" }, output, inspect, out code)
                && code != 0 && calls == 0, "extra arguments reached protected data");
            var invalid = Protocol.Decode<FpsY3InspectionResult>(output.ToString()); InspectionBlocked(invalid, "invalid_request");
            output.GetStringBuilder().Clear();
            Assert(FpsY3JournalInspector.TryDispatch(new[] { "--FPS-Y3-INSPECT" }, output, inspect, out code)
                && code != 0 && calls == 0, "ambiguous command reached normal helper or inspector");
            output.GetStringBuilder().Clear();
            Assert(FpsY3JournalInspector.TryDispatch(new[] { FpsY3JournalInspector.Command }, output, inspect, out code)
                && code != 0 && calls == 1 && !output.ToString().Contains("private"), "inspector exception leaked details");
            InspectionBlocked(Protocol.Decode<FpsY3InspectionResult>(output.ToString()), "internal_error");
            output.GetStringBuilder().Clear();
            var record = TrialRecord("Restored");
            Assert(FpsY3JournalInspector.TryDispatch(new[] { FpsY3JournalInspector.Command }, output,
                () => InspectSynthetic(record), out code) && code == 0, "valid inspection did not return JSON");
            var result = Protocol.Decode<FpsY3InspectionResult>(output.ToString());
            Assert(result.Classification == "restored_with_original" && !result.AuthorizesMaintenance,
                "successful command authorized installation");
            output.GetStringBuilder().Clear();
            Assert(FpsY3JournalInspector.TryDispatch(new[] { FpsY3JournalInspector.Command }, output,
                () => InspectSynthetic(TrialRecord("Muted")), out code) && code == 0, "pending inspection did not return its classification");
            result = Protocol.Decode<FpsY3InspectionResult>(output.ToString());
            Assert(result.Classification == "pending" && !result.AuthorizesMaintenance,
                "successful inspection confused pending with permission to install");
            output.GetStringBuilder().Clear();
            Assert(FpsY3JournalInspector.TryDispatch(new[] { FpsY3JournalInspector.Command }, output,
                () => new FpsY3InspectionResult { Classification = "unknown", AuthorizesMaintenance = true }, out code)
                && code != 0, "unknown or authorization-bearing result succeeded");
            InspectionBlocked(Protocol.Decode<FpsY3InspectionResult>(output.ToString()), "internal_error");
        });

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Test("Y3 inspector identifies one Windows fixture file consistently through read-only handles", () => WithRecordingDirectory(folder => {
                string path = Path.Combine(folder, "synthetic-journal.json"); var record = TrialRecord("Restored");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Protocol.Encode(record)));
                int checks = 0;
                Action inspectAcl = () => { var acl = new FileInfo(path).GetAccessControl(); if (acl == null) throw new IOException(); checks++; };
                var result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                    FpsY3JournalInspector.ReadStable(path, inspectAcl, FpsY3JournalInspector.NativeFileIdentity));
                Assert(result.Classification == "restored_with_original" && result.FileIdentitySha256.Length == 64 && checks >= 3,
                    "Windows file identity not verified");
            }));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Test("Y3 inspector blocks a Windows fixture with an additional hard link", () => WithRecordingDirectory(folder => {
                string path = Path.Combine(folder, "synthetic-journal.json"); string link = Path.Combine(folder, "other-name.json");
                var record = TrialRecord("Restored"); var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(record));
                File.WriteAllBytes(path, bytes);
                if (!CreateHardLinkW(link, path, IntPtr.Zero)) throw new IOException("No se creó el enlace sintético: " + Marshal.GetLastWin32Error());
                var result = FpsY3JournalInspector.InspectSnapshot(record.Sid, () =>
                    FpsY3JournalInspector.ReadStable(path, () => { }, FpsY3JournalInspector.NativeFileIdentity));
                InspectionBlocked(result, "invalid_data");
                Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && bytes.SequenceEqual(File.ReadAllBytes(link)),
                    "hard-link inspection changed fixture data");
            }));
    }
}
