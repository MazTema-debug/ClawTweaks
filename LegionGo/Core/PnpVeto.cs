// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Xml;

namespace LegionTweaks
{
    [DataContract]
    public sealed class PnpVetoDiagnostic
    {
        [DataMember] public string State { get; set; }
        [DataMember] public string WindowStartUtc { get; set; }
        [DataMember] public string WindowEndUtc { get; set; }
        [DataMember] public int MatchedEvents { get; set; }
        [DataMember] public bool? VetoObserved { get; set; }
        [DataMember] public bool? OutstandingOpenObserved { get; set; }
        [DataMember] public string[] Applications { get; set; }
        [DataMember] public int OtherApplicationEvents { get; set; }
        [DataMember] public int[] VetoTypes { get; set; }
        [DataMember] public bool PartialRead { get; set; }
    }

    // Private evidence context, never serialized into DiagnosticReport.
    public sealed class PnpVetoWindow
    {
        public string Endpoint { get; internal set; }
        public long StartUtc { get; internal set; }
        public long EndUtc { get; internal set; }
    }

    public static class PnpVetoRules
    {
        public const int MaximumRecords = 64, MaximumRecordCharacters = 16384, MaximumTotalCharacters = 131072;
        public const string SystemChannel = "System", DeviceChannel = "Microsoft-Windows-Kernel-PnP/Device Management";
        internal static readonly string[] Applications = { "LegionTweaks.Helper.exe", "GameInputSvc.exe", "explorer.exe", "dwm.exe", "GameBar.exe", "GameBarFTServer.exe" };
        public static PnpVetoWindow Window(PhysicalRecoveryOutcome value)
        {
            if (value == null || value.Version != 1 || !PhysicalRecoveryRules.IsEndpoint(value.Endpoint)
                || value.OperationStartedUtc <= 0 || value.DisableStartedUtc < value.OperationStartedUtc
                || value.DisableFailedUtc < value.DisableStartedUtc || value.CapturedUtc < value.DisableFailedUtc
                || value.CapturedUtc > DateTime.MaxValue.Ticks || value.DisableFailedUtc > DateTime.MaxValue.Ticks
                || value.DisableFailedUtc - value.OperationStartedUtc > TimeSpan.FromSeconds(45).Ticks) return null;
            return new PnpVetoWindow { Endpoint = value.Endpoint, StartUtc = value.DisableStartedUtc, EndUtc = value.DisableFailedUtc };
        }
        public static string XPath(PnpVetoWindow window, string channel)
        {
            if (window == null || (channel != SystemChannel && channel != DeviceChannel)) throw new ArgumentException("Ventana PnP inválida.");
            // Use explicit UTC SystemTime. FilterHashtable with DateTimeKind.Utc
            // returned a different interval on the test device; local time is never used here.
            return "*[System[Provider[@Name='Microsoft-Windows-Kernel-PnP'] and (EventID=" + (channel == SystemChannel ? "225" : "1000")
                + ") and TimeCreated[@SystemTime >= '" + Utc(window.StartUtc) + "' and @SystemTime <= '" + Utc(window.EndUtc) + "']]]";
        }
        public static PnpVetoDiagnostic Unknown(string state = "unknown") => new PnpVetoDiagnostic { State = state == "deferred" || state == "legacy" || state == "busy" ? state : "unknown",
            Applications = new string[0], VetoTypes = new int[0] };
        public static PnpVetoDiagnostic SafeCopy(PnpVetoDiagnostic value)
        {
            if (value == null) return Unknown();
            long start = 0, end = 0;
            bool window = ParseUtc(value.WindowStartUtc, out start) && ParseUtc(value.WindowEndUtc, out end)
                && end >= start && end - start <= TimeSpan.FromSeconds(45).Ticks;
            bool observed = window && value.MatchedEvents > 0 && value.MatchedEvents <= MaximumRecords && value.VetoObserved == true;
            if (!observed) { var unknown = Unknown(value.State); unknown.PartialRead = value.PartialRead; return unknown; }
            var types = (value.VetoTypes ?? new int[0]).Where(x => x >= 0 && x <= 13).Distinct().Take(14).OrderBy(x => x).ToArray();
            return new PnpVetoDiagnostic { State = value.State == "observed" && !value.PartialRead ? "observed" : "partial",
                WindowStartUtc = Utc(start), WindowEndUtc = Utc(end), MatchedEvents = value.MatchedEvents, VetoObserved = true,
                OutstandingOpenObserved = types.Contains(5) ? (bool?)true : null,
                Applications = (value.Applications ?? new string[0]).Where(x => Applications.Contains(x, StringComparer.OrdinalIgnoreCase))
                    .Select(x => Applications.First(a => string.Equals(a, x, StringComparison.OrdinalIgnoreCase))).Distinct().Take(Applications.Length).ToArray(),
                OtherApplicationEvents = Math.Max(0, Math.Min(MaximumRecords, value.OtherApplicationEvents)), VetoTypes = types,
                PartialRead = value.PartialRead || value.State != "observed" };
        }
        public static string Describe(PnpVetoDiagnostic value)
        {
            var safe = SafeCopy(value);
            if (safe.State == "deferred") return "Veto PnP: consulta aplazada hasta confirmar que no hay una reconexión o recuperación en curso.";
            if (safe.State == "legacy") return "Veto PnP: el resultado anterior no contiene una correlación exacta. Motivo sin confirmar.";
            if (safe.State == "busy") return "Veto PnP: hay una lectura de registros en curso. Actualiza el diagnóstico más tarde.";
            if (safe.VetoObserved != true) return "Veto PnP: " + (safe.PartialRead ? "lectura incompleta; " : "") + "sin eventos correlacionados disponibles. Esto no descarta un veto ni confirma su causa.";
            string text = "Veto PnP registrado por Windows" + (safe.PartialRead ? " (lectura parcial)" : "")
                + (safe.OutstandingOpenObserved == true ? ": accesos al dispositivo aún abiertos." : ".")
                + "\nVentana UTC: " + safe.WindowStartUtc + " — " + safe.WindowEndUtc;
            if (safe.Applications.Length > 0) text += "\nAplicaciones indicadas por Windows: " + string.Join(", ", safe.Applications) + ".";
            if (safe.OtherApplicationEvents > 0) text += "\nOtros eventos de aplicaciones: " + safe.OtherApplicationEvents + " (nombres omitidos).";
            return text + "\nLa lista describe este intento; no demuestra una causa única ni que otra reconexión vaya a funcionar.";
        }
        internal static string Utc(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        internal static bool ParseUtc(string value, out long ticks)
        {
            ticks = 0; DateTime parsed;
            if (value == null || value.Length > 40 || !DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)
                || parsed.Kind != DateTimeKind.Utc) return false;
            ticks = parsed.Ticks; return ticks > 0;
        }
    }

    // Parses bounded XML fixture/Windows records and projects only allowlisted
    // process leaf names. XML, paths, endpoints and command lines never escape.
    public sealed class PnpVetoAnalysis
    {
        private readonly object gate = new object();
        private readonly PnpVetoWindow window;
        private readonly HashSet<string> applications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> vetoTypes = new HashSet<int>();
        private int records, characters, matched, other;
        private bool systemComplete, deviceComplete, incomplete;
        public PnpVetoAnalysis(PnpVetoWindow window) { this.window = window ?? throw new ArgumentNullException(nameof(window)); }
        public bool Observe(string xml, string channel)
        {
            lock (gate)
            {
                if (++records > PnpVetoRules.MaximumRecords || xml == null || xml.Length > PnpVetoRules.MaximumRecordCharacters
                    || (characters += xml.Length) > PnpVetoRules.MaximumTotalCharacters) { incomplete = true; return false; }
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = PnpVetoRules.MaximumRecordCharacters };
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(new StringReader(xml), settings)) document.Load(reader);
                    var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("e", "http://schemas.microsoft.com/win/2004/08/events/event");
                    if (document.SelectSingleNode("/e:Event/e:System/e:Provider/@Name", ns)?.Value != "Microsoft-Windows-Kernel-PnP"
                        || document.SelectSingleNode("/e:Event/e:System/e:Channel", ns)?.InnerText != channel) return true;
                    string id = document.SelectSingleNode("/e:Event/e:System/e:EventID", ns)?.InnerText;
                    if ((channel != PnpVetoRules.SystemChannel || id != "225") && (channel != PnpVetoRules.DeviceChannel || id != "1000")) return true;
                    long captured;
                    if (!PnpVetoRules.ParseUtc(document.SelectSingleNode("/e:Event/e:System/e:TimeCreated/@SystemTime", ns)?.Value, out captured)
                        || captured < window.StartUtc || captured > window.EndUtc) return true;
                    string endpoint = Data(document, ns, id == "225" ? "DeviceInstance" : "DeviceInstanceId");
                    if (!string.Equals(endpoint, window.Endpoint, StringComparison.OrdinalIgnoreCase)) return true;
                    matched++;
                    if (id == "1000") { int type; if (int.TryParse(Data(document, ns, "VetoType"), NumberStyles.None, CultureInfo.InvariantCulture, out type) && type >= 0 && type <= 13) vetoTypes.Add(type); }
                    else
                    {
                        string raw = Data(document, ns, "ProcessName");
                        string leaf = raw == null || raw.Length > 2048 ? null : raw.Substring(Math.Max(raw.LastIndexOf('\\'), raw.LastIndexOf('/')) + 1);
                        string allowed = PnpVetoRules.Applications.FirstOrDefault(x => string.Equals(x, leaf, StringComparison.OrdinalIgnoreCase));
                        if (allowed == null) other++; else applications.Add(allowed);
                    }
                }
                catch { incomplete = true; }
                return true;
            }
        }
        public void Complete(string channel)
        {
            lock (gate) { if (channel == PnpVetoRules.SystemChannel) systemComplete = true; if (channel == PnpVetoRules.DeviceChannel) deviceComplete = true; }
        }
        public PnpVetoDiagnostic Snapshot(bool partial = false)
        {
            lock (gate) return matched == 0 ? new PnpVetoDiagnostic { State = "unknown", Applications = new string[0], VetoTypes = new int[0],
                PartialRead = partial || incomplete || !systemComplete || !deviceComplete } : PnpVetoRules.SafeCopy(new PnpVetoDiagnostic {
                State = !partial && !incomplete && systemComplete && deviceComplete ? "observed" : "partial", PartialRead = partial || incomplete || !systemComplete || !deviceComplete,
                WindowStartUtc = PnpVetoRules.Utc(window.StartUtc), WindowEndUtc = PnpVetoRules.Utc(window.EndUtc), MatchedEvents = matched,
                VetoObserved = true, VetoTypes = vetoTypes.ToArray(), Applications = applications.ToArray(), OtherApplicationEvents = other });
        }
        private static string Data(XmlDocument document, XmlNamespaceManager ns, string name)
        {
            var nodes = document.SelectNodes("/e:Event/e:EventData/e:Data[@Name='" + name + "']", ns);
            return nodes?.Count == 1 ? nodes[0].InnerText : null;
        }
    }
}
