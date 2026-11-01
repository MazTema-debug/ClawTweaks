// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text;

namespace LegionTweaks
{
    internal sealed class AmdGraphicsJournal : IAmdGraphicsJournal
    {
        private readonly string path;
        internal AmdGraphicsJournal(string directory) { path = Path.Combine(directory, "amd-graphics-originals.json"); }
        public AmdGraphicsBackup Load()
        {
            if (!File.Exists(path)) return null;
            var file = new FileInfo(path);
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > Protocol.MaxMessageLength)
                throw new InvalidDataException("Copia AMD no válida; se conserva sin cambios.");
            string json = File.ReadAllText(path);
            var value = Protocol.Decode<AmdGraphicsBackup>(json);
            // A JSON null tombstone is an atomic clear. Any other malformed journal fails closed.
            if (value != null && (value.Original == null || !value.Original.Available || value.Original.Values == null
                || value.Original.Values.Length > 64 || value.Original.Values.Length == 0 || !value.Pending && value.Expected == null
                || value.DormantOriginals != null && value.DormantOriginals.Length > 16))
                throw new InvalidDataException("La copia AMD está dañada; no se reemplaza.");
            return value;
        }
        public void Save(AmdGraphicsBackup value)
        {
            string temporary = path + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Encode(value));
            if (bytes.Length > Protocol.MaxMessageLength) throw new InvalidDataException("Copia AMD demasiado grande.");
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
    }
    internal sealed class AmdGraphicsReadOnlyJournal : IAmdGraphicsJournal
    {
        public AmdGraphicsBackup Load() => null;
        public void Save(AmdGraphicsBackup value) => throw new InvalidOperationException("Diagnóstico AMD de solo lectura.");
    }
}
