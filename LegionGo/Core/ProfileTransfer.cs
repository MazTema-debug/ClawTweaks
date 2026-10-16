// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace LegionTweaks
{
    [DataContract]
    public sealed class ProfileArchive
    {
        [DataMember(IsRequired = true)] public string Format { get; set; } = "LegionTweaks.Profiles";
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public ProfileCatalog Catalog { get; set; }
    }
    public static class ProfileTransfer
    {
        public const int MaxBytes = 65536;
        public static string Export(ProfileCatalog catalog)
        {
            if (catalog == null) throw new ArgumentException("No hay perfiles para exportar.");
            catalog.Validate();
            string json = Protocol.Encode(new ProfileArchive { Catalog = catalog });
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) throw new ArgumentException("El archivo supera 64 KiB.");
            return json;
        }
        public static ProfileCatalog Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
                throw new ArgumentException("Archivo vacío o mayor de 64 KiB.");
            var archive = Protocol.Decode<ProfileArchive>(json);
            if (archive == null || archive.Format != "LegionTweaks.Profiles" || archive.Version != 1 || archive.Catalog == null)
                throw new ArgumentException("Formato de perfiles incompatible.");
            archive.Catalog.Validate();
            return Protocol.Decode<ProfileCatalog>(Protocol.Encode(archive.Catalog));
        }
        // Import only adds independent copies: it never replaces existing IDs or invokes Apply.
        public static ProfileCatalog MergeCopies(ProfileCatalog current, ProfileCatalog imported)
        {
            if (current == null || imported == null) throw new ArgumentException("Catálogo vacío.");
            current.Validate(); imported.Validate();
            if (current.Profiles.Count + imported.Profiles.Count > 20)
                throw new ArgumentException("La importación superaría el máximo de 20 perfiles. No se importó ninguno.");
            var next = Protocol.Decode<ProfileCatalog>(Protocol.Encode(current));
            foreach (var source in imported.Profiles)
            {
                var profile = source.Copy(); profile.Id = Guid.NewGuid().ToString("D"); profile.Name = profile.Name.Trim();
                string stem = profile.Name;
                for (int suffix = 1; next.Profiles.Any(x => string.Equals(x.Name.Trim(), profile.Name, StringComparison.OrdinalIgnoreCase)); suffix++)
                {
                    string tail = " (importado " + suffix + ")";
                    int keep = Math.Min(stem.Length, 40 - tail.Length);
                    if (keep > 0 && char.IsHighSurrogate(stem[keep - 1])) keep--;
                    profile.Name = stem.Substring(0, keep).TrimEnd() + tail;
                }
                next.Profiles.Add(profile);
            }
            next.Validate(); return next;
        }
    }
    public sealed class ProfileNameEdit
    {
        public string Text { get; private set; }
        public int Caret { get; private set; }
        public static ProfileNameEdit Insert(string text, int start, int length, string value)
        {
            text = text ?? ""; value = value ?? "";
            if (start < 0 || length < 0 || start > text.Length || length > text.Length - start)
                throw new ArgumentException("Selección de texto inválida.");
            string next = text.Remove(start, length).Insert(start, value);
            if (next.Length > 40 || next.Any(char.IsControl)) return new ProfileNameEdit { Text = text, Caret = start };
            return new ProfileNameEdit { Text = next, Caret = start + value.Length };
        }
        public static ProfileNameEdit Backspace(string text, int start, int length)
        {
            text = text ?? "";
            if (length > 0 || start == 0) return Insert(text, start, length, "");
            int count = start >= 2 && start <= text.Length && char.IsLowSurrogate(text[start - 1]) && char.IsHighSurrogate(text[start - 2]) ? 2 : 1;
            return Insert(text, start - count, count, "");
        }
    }
}
