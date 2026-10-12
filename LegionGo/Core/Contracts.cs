// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LegionTweaks
{
    public static class Protocol
    {
        public const int Version = 1;
        public const string PipeName = "LegionTweaks.Z1E.v1";
        public const int MaxMessageLength = 16384;
        public static string Encode<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        public static T Decode<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaxMessageLength)
                throw new InvalidDataException("Invalid message size.");
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }

    [DataContract]
    public sealed class DeviceIdentity
    {
        [DataMember] public string Manufacturer { get; set; }
        [DataMember] public string Model { get; set; }
        [DataMember] public string Version { get; set; }
        [DataMember] public string Cpu { get; set; }
        public bool IsSupported
        {
            get
            {
                return string.Equals(Manufacturer?.Trim(), "LENOVO", StringComparison.OrdinalIgnoreCase)
                    && ((Model ?? "").StartsWith("83E1", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Version?.Trim(), "Legion Go 8APU1", StringComparison.OrdinalIgnoreCase))
                    && (Cpu ?? "").IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0
                    && (Cpu ?? "").IndexOf("Z1 Extreme", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    public enum Setting { Mode, Spl, Sppt, Fppt, FanFullSpeed, ChargeLimit, PowerLight, CpuBoost, Epp, OsPowerMode }

    [DataContract]
    public sealed class Address
    {
        [DataMember] public Setting Setting { get; set; }
        [DataMember] public bool Ac { get; set; }
        [DataMember] public string Scheme { get; set; }
        public string Id => Setting + ":" + Ac + ":" + Scheme;
    }
    [DataContract]
    public sealed class OriginalValue
    {
        [DataMember] public Address Address { get; set; }
        [DataMember] public int Value { get; set; }
    }
    [DataContract]
    public sealed class Reading
    {
        [DataMember] public string Key { get; set; }
        [DataMember] public int? Value { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Unit { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Source { get; set; }
    }
    [DataContract]
    public sealed class Snapshot
    {
        [DataMember] public DeviceIdentity Device { get; set; }
        [DataMember] public bool Supported { get; set; }
        [DataMember] public bool OnAc { get; set; }
        [DataMember] public Reading[] Values { get; set; }
        [DataMember] public int SavedOriginals { get; set; }
        [DataMember(EmitDefaultValue = false)] public string CapturedAtUtc { get; set; }
    }
    [DataContract]
    public sealed class Request
    {
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public string Operation { get; set; }
        [DataMember] public string Key { get; set; }
        [DataMember] public int[] Values { get; set; }
        [DataMember] public bool Ac { get; set; }
    }
    [DataContract]
    public sealed class Response
    {
        [DataMember] public int Version { get; set; } = Protocol.Version;
        [DataMember] public bool Success { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember] public Snapshot State { get; set; }
    }
}
