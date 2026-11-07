// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LegionTweaks
{
    internal sealed class FileJournal : IJournal
    {
        private readonly string path;
        public FileJournal(string path) { this.path = path; }
        public IList<OriginalValue> Load()
        {
            if (!File.Exists(path)) return new List<OriginalValue>();
            // Fail closed if damaged: never replace a backup with guessed/default values.
            return Protocol.Decode<List<OriginalValue>>(File.ReadAllText(path));
        }
        public void Save(IList<OriginalValue> values)
        {
            // Preserve the exact legacy document before converting register labels or clearing it.
            if (File.Exists(path))
            {
                var old = Protocol.Decode<List<OriginalValue>>(File.ReadAllText(path));
                if (old.Any(x => PowerLimits.IsLimit(x.Address.Setting) && x.PowerMappingVersion == 0))
                {
                    string legacyBackup = path + ".before-power-mapping-v1";
                    if (!File.Exists(legacyBackup)) File.Copy(path, legacyBackup, false);
                }
            }
            string temporary = path + ".tmp";
            // Keep legacy physical register labels on disk even for new captures. An
            // older helper ignores the version field but still restores the right cells.
            byte[] data = Encoding.UTF8.GetBytes(Protocol.Encode(values.Select(PowerLimits.ForStorage).ToList()));
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(data, 0, data.Length); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

}
