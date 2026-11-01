// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;

namespace LegionTweaks
{
    // A storage adapter must atomically persist and verify these bounded bytes.
    // It has no access to the controller or power settings. No disk adapter is
    // selected implicitly by this model or by opening an editor.
    public interface IRemapProfileStorage
    {
        byte[] Read();
        // Compare expected bytes and replace under the same storage lock.
        // Null expected means absent. A mismatch must leave storage untouched.
        void WriteAtomic(byte[] bytes, byte[] expected);
    }

    // Single-worker catalog/editor orchestration. Every boundary owns a copy;
    // Save does not call Activate, and activation always uses a saved snapshot.
    public sealed class ControllerRemapWorkspace
    {
        private readonly IRemapProfileStorage storage;
        private readonly ControllerRemapEngine engine;
        private RemapProfiles saved = new RemapProfiles();
        private RemapProfile draft;
        public ControllerRemapWorkspace(IRemapProfileStorage storage, ControllerRemapEngine engine)
        { this.storage = storage ?? throw new ArgumentNullException(nameof(storage)); this.engine = engine ?? throw new ArgumentNullException(nameof(engine)); }
        public RemapProfiles Saved => RemapProfiles.Decode(saved.Encode());
        public RemapProfile Draft => draft?.Copy();
        public bool Dirty => draft != null && (saved.Profiles.All(p => !SameId(p.Id, draft.Id))
            || Protocol.Encode(saved.Profiles.Single(p => SameId(p.Id, draft.Id))) != Protocol.Encode(draft));
        public void Load()
        {
            // An unreadable/invalid file is an error, never an empty replacement.
            var bytes = storage.Read(); var loaded = bytes == null ? new RemapProfiles() : RemapProfiles.Decode(bytes);
            saved = loaded; draft = null;
        }
        public void BeginNew(RemapMode mode, string name)
        { SetDraft(new RemapProfile { Mode = mode, Name = name }); }
        public void Select(string id)
        { draft = Find(id).Copy(); }
        public void SetDraft(RemapProfile value)
        { if (value == null) throw new ArgumentNullException(nameof(value)); draft = value.Copy(); }
        public void RevertDraft()
        { if (draft != null) draft = Find(draft.Id).Copy(); }
        public void SaveDraft()
        {
            if (draft == null) throw new InvalidOperationException("No hay un borrador para guardar.");
            var copy = draft.Copy();
            var catalog = new RemapProfiles { Profiles = saved.Profiles.Where(p => !SameId(p.Id, copy.Id)).Concat(new[] { copy }).ToArray() };
            Persist(catalog); draft = copy.Copy();
        }
        public void Delete(string id)
        {
            Find(id);
            if (engine.ActiveProfile != null && SameId(engine.ActiveProfile.Id, id))
                throw new InvalidOperationException("Detén la distribución activa antes de eliminarla.");
            Persist(new RemapProfiles { Profiles = saved.Profiles.Where(p => !SameId(p.Id, id)).ToArray() });
            if (draft != null && SameId(draft.Id, id)) draft = null;
        }
        public RemapOutputFrame ActivateSaved(string id, RemapCapabilities capabilities) => engine.Activate(Find(id), capabilities);
        private RemapProfile Find(string id)
        {
            var found = saved.Profiles.FirstOrDefault(p => SameId(p.Id, id));
            if (found == null) throw new ArgumentException("Selecciona una distribución guardada.");
            return found;
        }
        private void Persist(RemapProfiles catalog)
        {
            var bytes = catalog.Encode();
            // Detect another editor/process before overwriting its catalog.
            var current = storage.Read();
            var onDisk = current == null ? new RemapProfiles() : RemapProfiles.Decode(current);
            if (Protocol.Encode(onDisk) != Protocol.Encode(saved))
                throw new InvalidOperationException("El catálogo cambió; vuelve a cargarlo antes de guardar.");
            storage.WriteAtomic(bytes, current);
            var verified = RemapProfiles.Decode(storage.Read());
            if (Protocol.Encode(verified) != Protocol.Encode(catalog)) throw new InvalidOperationException("No se confirmó el catálogo guardado.");
            saved = verified;
        }
        private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
