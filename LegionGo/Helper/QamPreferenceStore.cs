// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text;

namespace LegionTweaks
{
    internal interface IQamPreferenceStore
    {
        QamPreferences Load();
        void Save(QamPreferences value);
    }
    internal sealed class QamPreferenceStore : IQamPreferenceStore
    {
        internal const string Name = "qam-preferences.json";
        private readonly string directory;
        internal QamPreferenceStore(string directory) { this.directory = directory; }
        private string FilePath(string name)
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("La carpeta protegida de configuración no está disponible.");
            string path = Path.Combine(directory, name);
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("La configuración no puede ser un enlace."); }
            catch (FileNotFoundException) { }
            return path;
        }
        public QamPreferences Load()
        {
            string path = FilePath(Name);
            try {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    if (stream.Length > 4096) throw new InvalidDataException("Configuración de menús demasiado larga.");
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true))) {
                        var value = Protocol.Decode<QamPreferences>(reader.ReadToEnd()); value.Validate(); return value;
                    }
                }
            }
            catch (FileNotFoundException) { return new QamPreferences(); }
        }
        public void Save(QamPreferences value)
        {
            value.Validate();
            string path = FilePath(Name), temporary = FilePath(Name + "." + Guid.NewGuid().ToString("N") + ".tmp");
            string backup = null;
            try { Load(); }
            catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException || ex is System.Runtime.Serialization.SerializationException || ex is System.Xml.XmlException || ex is DecoderFallbackException) {
                backup = FilePath(Name + ".invalid-" + Guid.NewGuid().ToString("N") + ".json");
            }
            try {
                byte[] bytes = Encoding.UTF8.GetBytes(Protocol.Encode(value));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, backup); else File.Move(temporary, path);
                if (Protocol.Encode(Load()) != Protocol.Encode(value)) throw new IOException("No se confirmó la configuración de los menús.");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
