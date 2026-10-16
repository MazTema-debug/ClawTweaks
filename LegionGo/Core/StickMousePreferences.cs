// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text;

namespace LegionTweaks
{
    // Deliberately contains no Enabled field. Loading these values can never
    // restore cursor motion, availability, focus or an armed stick.
    [DataContract]
    public sealed class StickMousePreferences
    {
        public const int MaximumBytes = 1024;
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public int Sensitivity { get; set; } = StickMouse.DefaultSensitivity;
        [DataMember(IsRequired = true)] public int Deadzone { get; set; } = StickMouse.DefaultDeadzone;

        public void Validate()
        {
            if (Version != 1) throw new ArgumentException("Versión de preferencias del mouse incompatible.");
            var validator = new StickMouse(); validator.Configure(false, Sensitivity, Deadzone);
        }
        public static StickMousePreferences Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Archivo de preferencias del mouse vacío o demasiado grande.");
            try
            {
                string json = new UTF8Encoding(false, true).GetString(bytes);
                if (json.Length > 0 && json[0] == '\ufeff') json = json.Substring(1);
                var result = Protocol.Decode<StickMousePreferences>(json);
                if (result == null) throw new ArgumentException("Preferencias del mouse vacías.");
                result.Validate(); return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is SerializationException)
            { throw new InvalidDataException("Preferencias del mouse inválidas.", ex); }
        }
        public byte[] Encode()
        {
            Validate(); var bytes = Encoding.UTF8.GetBytes(Protocol.Encode(this));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Preferencias del mouse demasiado grandes.");
            return bytes;
        }
    }
}
