// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LegionTweaks
{
    [DataContract]
    internal sealed class FpsY3InspectionResult
    {
        [DataMember] internal int Version { get; set; } = 1;
        [DataMember] internal string Scope { get; set; } = "current_process_identity";
        [DataMember] internal string Classification { get; set; }
        [DataMember(EmitDefaultValue = false)] internal string Reason { get; set; }
        [DataMember(EmitDefaultValue = false)] internal string Phase { get; set; }
        [DataMember(EmitDefaultValue = false)] internal bool? OriginalPresent { get; set; }
        [DataMember(EmitDefaultValue = false)] internal bool? ErrorPresent { get; set; }
        [DataMember(EmitDefaultValue = false)] internal string ContentSha256 { get; set; }
        [DataMember(EmitDefaultValue = false)] internal string FileIdentitySha256 { get; set; }
        // This snapshot never proves quiescence or authorizes a maintenance operation.
        [DataMember] internal bool AuthorizesMaintenance { get; set; }
    }

    internal sealed class FpsY3InspectionSnapshot
    {
        internal byte[] Bytes;
        internal string FileIdentity;
    }

    internal sealed class FpsY3InspectionException : Exception
    {
        internal string Category { get; }
        internal FpsY3InspectionException(string category) { Category = category; }
    }

    // Explicit read-only inspection, separate from native recovery and from the
    // ordinary helper startup. All outputs are categorical and omit the journal.
    internal static class FpsY3JournalInspector
    {
        internal const string Command = "--fps-y3-inspect";
        internal const int MaximumBytes = 16384;
        private const string Leaf = "fps-y3-trial.json";

        internal static FpsY3InspectionResult Blocked(string reason) =>
            new FpsY3InspectionResult { Classification = "blocked", Reason = reason };

        internal static bool TryDispatch(string[] args, TextWriter output,
            Func<FpsY3InspectionResult> inspect, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0 || args[0] == null
                || !args[0].StartsWith(Command, StringComparison.OrdinalIgnoreCase)) return false;
            FpsY3InspectionResult result;
            if (args.Length != 1 || args[0] != Command) result = Blocked("invalid_request");
            else {
                try { result = inspect?.Invoke() ?? Blocked("internal_error"); }
                catch { result = Blocked("internal_error"); }
            }
            if (result.Version != 1 || result.Scope != "current_process_identity" || result.AuthorizesMaintenance
                || Array.IndexOf(new[] { "absent", "pending", "restored_with_original", "restored_without_original", "blocked" }, result.Classification) < 0)
                result = Blocked("internal_error");
            output.WriteLine(Protocol.Encode(result)); output.Flush();
            // Success means only that the snapshot was classified. Pending and
            // restored-without-original still require the caller to stop.
            exitCode = result.Classification == "blocked" ? 1 : 0;
            return true;
        }

        internal static FpsY3InspectionResult InspectCurrentUser(string directory)
        {
            try {
                using (var user = WindowsIdentity.GetCurrent()) {
                    if (user.User == null || !new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
                        return Blocked("not_elevated");
                    string sid = user.User.Value;
                    var protection = new RemapProfileStorage(directory);
                    string path = Path.Combine(directory, Leaf);
                    return InspectUnderMutex(@"Global\LegionTweaks.FpsY3.Journal." + sid, sid,
                        () => ReadStable(path, () => protection.RequireProtectedLeaf(Leaf), NativeFileIdentity), 2000);
                }
            }
            catch { return Blocked("unavailable"); }
        }

        internal static FpsY3InspectionResult InspectUnderMutex(string name, string expectedSid,
            Func<FpsY3InspectionSnapshot> read, int timeoutMilliseconds)
        {
            try {
                using (var gate = new Mutex(false, name)) {
                    bool owns = false;
                    try {
                        try { owns = gate.WaitOne(timeoutMilliseconds); }
                        catch (AbandonedMutexException) { owns = true; return Blocked("abandoned_mutex"); }
                        if (!owns) return Blocked("busy");
                        return InspectSnapshot(expectedSid, read);
                    }
                    finally { if (owns) gate.ReleaseMutex(); }
                }
            }
            catch { return Blocked("unavailable"); }
        }

        internal static FpsY3InspectionResult InspectSnapshot(string expectedSid, Func<FpsY3InspectionSnapshot> read)
        {
            try {
                var snapshot = read();
                if (snapshot == null) return new FpsY3InspectionResult { Classification = "absent" };
                if (snapshot.Bytes == null || snapshot.Bytes.Length == 0 || snapshot.Bytes.Length > MaximumBytes
                    || string.IsNullOrEmpty(snapshot.FileIdentity)) return Blocked("invalid_data");
                var record = ParseRecord(snapshot.Bytes);
                ValidateForInspection(record, expectedSid);
                var result = new FpsY3InspectionResult {
                    Phase = record.Phase, OriginalPresent = record.Original != null, ErrorPresent = record.Error != null,
                    ContentSha256 = Digest(snapshot.Bytes),
                    FileIdentitySha256 = Digest(Encoding.ASCII.GetBytes(snapshot.FileIdentity))
                };
                if (record.Phase != "Restored") result.Classification = "pending";
                else if (record.Error != null) { result.Classification = "blocked"; result.Reason = "recorded_error"; }
                else result.Classification = record.Original == null ? "restored_without_original" : "restored_with_original";
                return result;
            }
            catch (FpsY3InspectionException ex) { return Blocked(ex.Category); }
            catch (InvalidDataException) { return Blocked("invalid_data"); }
            catch (ArgumentException) { return Blocked("invalid_data"); }
            catch (InvalidOperationException) { return Blocked("invalid_data"); }
            catch (UnauthorizedAccessException) { return Blocked("unavailable"); }
            catch (IOException) { return Blocked("unavailable"); }
            catch { return Blocked("unavailable"); }
        }

        internal static void ValidateForInspection(FpsY3LeaseRecord record, string expectedSid)
        {
            record.Validate(expectedSid); // Reuse recovery's identity/original/map validation.
            // Historical owners may have exited; validate their recorded times,
            // not current PID liveness. A clock anomaly is uncertain, so block.
            if (record.OwnerStarted > record.CreatedUtc || record.WatchStarted > DateTime.MaxValue.Ticks
                || record.WatchStarted != 0 && record.WatchStarted < record.CreatedUtc)
                throw new InvalidDataException("Tiempos del diario incoherentes.");
        }

        // The caller supplies the existing protected-directory check. Opened
        // read-only without delete/write sharing, read twice, and compare the
        // actual file identity and bytes before releasing either handle.
        internal static FpsY3InspectionSnapshot ReadStable(string path, Action requireProtected,
            Func<FileStream, string> identity)
        {
            try { requireProtected(); }
            catch (FileNotFoundException) { throw new FpsY3InspectionException("changed"); }
            try {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("Archivo protegido inválido.");
            }
            catch (FileNotFoundException) {
                try { requireProtected(); }
                catch (FileNotFoundException) { throw new FpsY3InspectionException("changed"); }
                try { File.GetAttributes(path); throw new FpsY3InspectionException("changed"); }
                catch (FileNotFoundException) { return null; }
            }
            try {
                using (var first = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    string firstId = identity(first); var firstBytes = ReadBounded(first);
                    requireProtected();
                    using (var second = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                        string secondId = identity(second); var secondBytes = ReadBounded(second);
                        requireProtected();
                        if (string.IsNullOrEmpty(firstId) || firstId != secondId || !firstBytes.SequenceEqual(secondBytes))
                            throw new FpsY3InspectionException("changed");
                        return new FpsY3InspectionSnapshot { Bytes = firstBytes, FileIdentity = firstId };
                    }
                }
            }
            catch (FileNotFoundException) { throw new FpsY3InspectionException("changed"); }
        }

        private static byte[] ReadBounded(FileStream stream)
        {
            long length = stream.Length;
            if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("Tamaño inválido.");
            var bytes = new byte[(int)length]; int offset = 0, read;
            while (offset < bytes.Length && (read = stream.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
            if (offset != bytes.Length || stream.ReadByte() != -1) throw new FpsY3InspectionException("changed");
            return bytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInfo
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInfo info);

        internal static string NativeFileIdentity(FileStream stream)
        {
            NativeFileInfo info;
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0
                || info.Links != 1 || (info.IndexHigh == 0 && info.IndexLow == 0))
                throw new InvalidDataException("Identidad de archivo inválida.");
            return info.VolumeSerial.ToString("X8", CultureInfo.InvariantCulture)
                + info.IndexHigh.ToString("X8", CultureInfo.InvariantCulture)
                + info.IndexLow.ToString("X8", CultureInfo.InvariantCulture);
        }

        private static string Digest(byte[] bytes)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }

        private static FpsY3LeaseRecord ParseRecord(byte[] bytes)
        {
            var root = Object(new StrictJson(bytes).Parse());
            Exact(root, "Sid", "Token", "Phase", "SourcePath", "Error", "OwnerPid", "WatchPid",
                "OwnerStarted", "WatchStarted", "CreatedUtc", "ExpiresUtc", "Original");
            var error = root["Error"];
            if (error != null && !(error is string)) throw new InvalidDataException();
            FpsY3NativeOriginal original = null;
            if (root["Original"] != null) {
                var value = Object(root["Original"]);
                Exact(value, "Path", "Container", "Usb3", "Usb4", "Map3", "Map4");
                original = new FpsY3NativeOriginal { Path = String(value["Path"]), Container = String(value["Container"]),
                    Usb3 = Bytes(value["Usb3"]), Usb4 = Bytes(value["Usb4"]),
                    Map3 = Bytes(value["Map3"]), Map4 = Bytes(value["Map4"]) };
            }
            return new FpsY3LeaseRecord { Sid = String(root["Sid"]), Token = String(root["Token"]),
                Phase = String(root["Phase"]), SourcePath = String(root["SourcePath"]), Error = (string)error,
                OwnerPid = Int32(root["OwnerPid"]), WatchPid = Int32(root["WatchPid"]),
                OwnerStarted = Integer(root["OwnerStarted"]), WatchStarted = Integer(root["WatchStarted"]),
                CreatedUtc = Integer(root["CreatedUtc"]), ExpiresUtc = Integer(root["ExpiresUtc"]), Original = original };
        }

        private static Dictionary<string, object> Object(object value) =>
            value as Dictionary<string, object> ?? throw new InvalidDataException();
        private static string String(object value) => value as string ?? throw new InvalidDataException();
        private static long Integer(object value) => value is long ? (long)value : throw new InvalidDataException();
        private static int Int32(object value)
        { long number = Integer(value); if (number < int.MinValue || number > int.MaxValue) throw new InvalidDataException(); return (int)number; }
        private static byte[] Bytes(object value)
        {
            var items = value as List<object>;
            if (items == null || items.Count != 64) throw new InvalidDataException();
            var bytes = new byte[64];
            for (int i = 0; i < bytes.Length; i++) { long n = Integer(items[i]); if (n < 0 || n > 255) throw new InvalidDataException(); bytes[i] = (byte)n; }
            return bytes;
        }
        private static void Exact(Dictionary<string, object> value, params string[] fields)
        { if (value.Count != fields.Length || fields.Any(field => !value.ContainsKey(field))) throw new InvalidDataException(); }

        // A small strict JSON reader for this bounded schema. It rejects duplicate
        // decoded names, unknown/missing fields, trailing data and non-integral
        // numbers; ticks are parsed directly as Int64, never through Double.
        private sealed class StrictJson
        {
            private readonly string text;
            private int position;
            internal StrictJson(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException();
                text = new UTF8Encoding(false, true).GetString(bytes);
                if (text.Length != 0 && text[0] == '\uFEFF') position = 1;
            }
            internal object Parse()
            { var value = Value(0); White(); if (position != text.Length) throw new InvalidDataException(); return value; }
            private object Value(int depth)
            {
                if (depth > 3) throw new InvalidDataException(); White();
                if (position >= text.Length) throw new InvalidDataException();
                char c = text[position];
                if (c == '{') return ReadObject(depth + 1);
                if (c == '[') return ReadArray(depth + 1);
                if (c == '"') return ReadString();
                if (c == 'n') { Literal("null"); return null; }
                if (c == '-' || c >= '0' && c <= '9') return Number();
                throw new InvalidDataException();
            }
            private Dictionary<string, object> ReadObject(int depth)
            {
                Take('{'); White(); var result = new Dictionary<string, object>(StringComparer.Ordinal);
                if (TryTake('}')) return result;
                while (true) {
                    White(); string name = ReadString(); White(); Take(':');
                    if (result.ContainsKey(name) || result.Count >= 32) throw new InvalidDataException();
                    result.Add(name, Value(depth)); White();
                    if (TryTake('}')) return result;
                    Take(',');
                }
            }
            private List<object> ReadArray(int depth)
            {
                Take('['); White(); var result = new List<object>();
                if (TryTake(']')) return result;
                while (true) {
                    if (result.Count >= 64) throw new InvalidDataException();
                    result.Add(Value(depth)); White();
                    if (TryTake(']')) return result;
                    Take(',');
                }
            }
            private string ReadString()
            {
                Take('"'); var value = new StringBuilder();
                while (position < text.Length) {
                    char c = text[position++];
                    if (c == '"') {
                        string result = value.ToString();
                        for (int i = 0; i < result.Length; i++) {
                            if (char.IsHighSurrogate(result[i])) { if (++i >= result.Length || !char.IsLowSurrogate(result[i])) throw new InvalidDataException(); }
                            else if (char.IsLowSurrogate(result[i])) throw new InvalidDataException();
                        }
                        return result;
                    }
                    if (c < 0x20) throw new InvalidDataException();
                    if (c != '\\') { value.Append(c); continue; }
                    if (position >= text.Length) throw new InvalidDataException();
                    c = text[position++];
                    switch (c) {
                        case '"': case '\\': case '/': value.Append(c); break;
                        case 'b': value.Append('\b'); break; case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break; case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            if (position + 4 > text.Length) throw new InvalidDataException();
                            int hex; if (!int.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier,
                                CultureInfo.InvariantCulture, out hex)) throw new InvalidDataException();
                            value.Append((char)hex); position += 4; break;
                        default: throw new InvalidDataException();
                    }
                }
                throw new InvalidDataException();
            }
            private long Number()
            {
                int start = position; if (text[position] == '-') position++;
                if (position >= text.Length || text[position] < '0' || text[position] > '9') throw new InvalidDataException();
                if (text[position] == '0') position++;
                else while (position < text.Length && text[position] >= '0' && text[position] <= '9') position++;
                long value;
                if (!long.TryParse(text.Substring(start, position - start), NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out value)) throw new InvalidDataException();
                return value;
            }
            private void Literal(string literal)
            { if (position + literal.Length > text.Length || string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) throw new InvalidDataException(); position += literal.Length; }
            private void White()
            { while (position < text.Length && (text[position] == ' ' || text[position] == '\t' || text[position] == '\r' || text[position] == '\n')) position++; }
            private void Take(char c) { if (!TryTake(c)) throw new InvalidDataException(); }
            private bool TryTake(char c)
            { if (position < text.Length && text[position] == c) { position++; return true; } return false; }
        }
    }
}
