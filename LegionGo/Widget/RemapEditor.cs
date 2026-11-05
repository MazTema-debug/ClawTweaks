// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LegionTweaks.Widget
{
    internal interface IRemapEditorService { Task<Response> Send(Request request); }

    // Per-view editor state. No engine, protected file access or hardware activation.
    internal sealed class RemapEditor
    {
        private readonly IRemapEditorService service;
        private readonly object sync = new object();
        private readonly Dictionary<string, RemapDraft> drafts = new Dictionary<string, RemapDraft>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<RemapMode, string> lastMode = new Dictionary<RemapMode, string>();
        private int generation;
        internal RemapEditor(IRemapEditorService service) { this.service = service; New(RemapMode.Gamepad); }
        internal RemapDraft Current { get; private set; }
        internal RemapProfiles Catalog { get; private set; } = new RemapProfiles();
        internal string Revision { get; private set; }
        internal bool EditingAvailable { get; private set; }
        internal bool ReloadRequired { get; private set; } = true;
        internal bool IsBusy { get; private set; }
        internal string Message { get; private set; } = "Consulta el catálogo para comprobar si el helper admite guardar distribuciones.";
        internal bool CanSave => !IsBusy && EditingAvailable && !ReloadRequired && Revision != null;
        internal FpsY3TrialStatus Y3Trial { get; private set; }
        internal bool CanTestY3 => CanSave && !Current.Dirty && Y3Trial?.Available == true && !Y3Trial.Active
            && !Y3Trial.RecoveryPending && FpsY3TrialRules.CanUse(SavedCurrent);
        internal bool CanStopY3 => !IsBusy && (Y3Trial?.Active == true || Y3Trial?.RecoveryPending == true);
        internal void ObserveY3(FpsY3TrialStatus status)
        { lock (sync) Y3Trial = status == null ? null : Protocol.Decode<FpsY3TrialStatus>(Protocol.Encode(status)); }
        internal RemapProfile SavedCurrent => Catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, Current.Id, StringComparison.OrdinalIgnoreCase));

        internal void New(RemapMode mode)
        {
            string stem = mode == RemapMode.Fps ? "Mi distribución FPS" : mode == RemapMode.Split ? "Mi distribución J1 y J2" : "Mi distribución";
            string name = stem; int suffix = 2;
            while (Catalog.Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) || drafts.Values.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) name = stem + " " + suffix++;
            Current = new RemapDraft { Id = Guid.NewGuid().ToString("D"), Name = name, Mode = mode, Dirty = true };
            drafts[Current.Id] = Current; lastMode[mode] = Current.Id;
        }
        internal void SelectMode(RemapMode mode)
        {
            if (!Enum.IsDefined(typeof(RemapMode), mode)) throw new ArgumentException("Modo desconocido.");
            string id;
            if (lastMode.TryGetValue(mode, out id) && drafts.ContainsKey(id)) Current = drafts[id];
            else {
                var saved = Catalog.Profiles.FirstOrDefault(p => p.Mode == mode);
                if (saved != null) Select(saved.Id); else New(mode);
            }
        }
        internal void Select(string id)
        {
            RemapDraft draft;
            if (!drafts.TryGetValue(id, out draft)) {
                var saved = Catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                if (saved == null) throw new ArgumentException("La distribución ya no está en el catálogo. Recarga para comprobarlo.");
                draft = RemapDraft.From(saved); drafts[id] = draft;
            }
            Current = draft; lastMode[draft.Mode] = draft.Id;
        }
        internal RemapDraft[] Choices => Catalog.Profiles.Where(p => p.Mode == Current.Mode).Select(p =>
            drafts.ContainsKey(p.Id) ? drafts[p.Id] : RemapDraft.From(p))
            .Concat(drafts.Values.Where(d => d.Mode == Current.Mode && !Catalog.Profiles.Any(p => string.Equals(p.Id, d.Id, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        internal void RestoreSaved()
        {
            var saved = SavedCurrent;
            if (saved == null) return;
            Current = RemapDraft.From(saved); drafts[Current.Id] = Current;
        }
        internal void Invalidate(string message = "Vista reabierta. Actualiza el catálogo; tu borrador se conserva.")
        {
            lock (sync) {
                generation++; IsBusy = false; EditingAvailable = false; Revision = null; ReloadRequired = true; Message = message;
            }
        }
        internal Task<bool> Refresh() => Send(new Request { Operation = "remap_status" });
        internal Task<bool> TestY3()
        {
            if (!CanTestY3) { Message = "Guarda una distribución FPS con solo Y3 → una tecla en Pulso y el resto Original; consulta un helper compatible."; return Task.FromResult(false); }
            return Send(new Request { Operation = "remap_y3_test", Remap = new RemapEditingRequest { Revision = Revision, Id = SavedCurrent.Id } });
        }
        internal Task<bool> StopY3() => Send(new Request { Operation = "remap_y3_stop" });
        internal Task<bool> Save()
        {
            if (!CanSave) { Message = "Actualiza el catálogo con un helper compatible antes de guardar. Tu borrador se conserva."; return Task.FromResult(false); }
            RemapProfile profile;
            try { profile = Current.Build(); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { Message = ex.Message; return Task.FromResult(false); }
            var draft = Current; int version = draft.EditVersion;
            return Send(new Request { Operation = "remap_save", Remap = new RemapEditingRequest { Revision = Revision, Profile = profile } }, () => {
                var saved = Catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, draft.Id, StringComparison.OrdinalIgnoreCase));
                if (saved != null && draft.EditVersion == version) {
                    var clean = RemapDraft.From(saved); drafts[clean.Id] = clean;
                    if (ReferenceEquals(Current, draft)) Current = clean;
                }
            });
        }
        internal Task<bool> Delete(string id)
        {
            if (!CanSave || !Catalog.Profiles.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))) {
                Message = "Recarga el catálogo antes de eliminar una distribución guardada."; return Task.FromResult(false);
            }
            return Send(new Request { Operation = "remap_delete", Remap = new RemapEditingRequest { Revision = Revision, Id = id } }, () => {
                RemapDraft draft;
                if (drafts.TryGetValue(id, out draft) && !draft.Dirty) {
                    drafts.Remove(id);
                    if (ReferenceEquals(Current, draft)) New(draft.Mode);
                }
                // A dirty local draft remains editable, even when its saved copy is deleted.
            });
        }
        private async Task<bool> Send(Request request, Action accepted = null)
        {
            int operation;
            lock (sync) {
                if (IsBusy) return false;
                IsBusy = true; operation = ++generation;
                Message = request.Operation == "remap_status" ? "Consultando catálogo…" : request.Operation == "remap_save" ? "Guardando distribución…"
                    : request.Operation == "remap_y3_test" ? "Comprobando e iniciando la prueba de Y3…"
                    : request.Operation == "remap_y3_stop" ? "Devolviendo la asignación original de Y3…" : "Eliminando distribución guardada…";
            }
            try {
                var response = await service.Send(request).ConfigureAwait(false);
                lock (sync) {
                    if (operation != generation) return false;
                    if (response == null || !response.SupportsRemapEditing) throw new InvalidOperationException("Este helper no admite el editor. Actualízalo para guardar; tu borrador se conserva.");
                    if (response.FpsY3Trial != null) ObserveY3(response.FpsY3Trial);
                    if (!response.Success) throw new InvalidOperationException((response.Message ?? "No se completó la operación.") + " Recarga el catálogo antes de repetir; tu borrador se conserva.");
                    var status = response.Remap;
                    if (request.Operation == "remap_y3_stop" && status?.Y3Trial != null && !status.Y3Trial.Active
                        && !status.Y3Trial.RecoveryPending && !status.EditingAvailable) {
                        ObserveY3(status.Y3Trial); EditingAvailable = false; ReloadRequired = true; Revision = null;
                        Message = status.Message; return true;
                    }
                    if (status?.EditingAvailable != true || status.Catalog == null || status.Revision == null
                        || status.Revision.Length != 64 || status.Revision.Any(c => !Uri.IsHexDigit(c)))
                        throw new InvalidOperationException("El catálogo no está confirmado. Recarga; tu borrador se conserva.");
                    status.Catalog.Validate();
                    if (request.Operation == "remap_save") {
                        var confirmed = status.Catalog.Profiles.FirstOrDefault(p => p.Id == request.Remap.Profile.Id);
                        if (confirmed == null || Protocol.Encode(confirmed) != Protocol.Encode(request.Remap.Profile))
                            throw new InvalidOperationException("El helper no confirmó la distribución enviada. Recarga el catálogo; el borrador se conserva.");
                    }
                    if (request.Operation == "remap_delete" && status.Catalog.Profiles.Any(p => p.Id == request.Remap.Id))
                        throw new InvalidOperationException("El helper no confirmó la eliminación. Recarga el catálogo.");
                    var prior = SavedCurrent;
                    var next = status.Catalog.Profiles.FirstOrDefault(p => p.Id == Current.Id);
                    bool externalChange = request.Operation == "remap_status" && Current.Dirty && prior != null
                        && (next == null || Protocol.Encode(prior) != Protocol.Encode(next));
                    Catalog = Protocol.Decode<RemapProfiles>(Protocol.Encode(status.Catalog));
                    Revision = status.Revision; EditingAvailable = true; ReloadRequired = false; ObserveY3(status.Y3Trial ?? response.FpsY3Trial);
                    // Refresh replaces only clean drafts; an external edit never overwrites a local draft.
                    foreach (var id in drafts.Keys.ToArray()) {
                        var previous = drafts[id];
                        var saved = Catalog.Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                        if (!previous.Dirty && saved != null) {
                            var clean = RemapDraft.From(saved); drafts[id] = clean;
                            if (ReferenceEquals(Current, previous)) Current = clean;
                        }
                        else if (!previous.Dirty && saved == null) {
                            drafts.Remove(id);
                            if (ReferenceEquals(Current, previous)) {
                                var replacement = Catalog.Profiles.FirstOrDefault(p => p.Mode == previous.Mode);
                                if (replacement == null) New(previous.Mode); else Select(replacement.Id);
                            }
                        }
                    }
                    accepted?.Invoke();
                    Message = request.Operation == "remap_save" ? "Distribución guardada. El mando y la energía no han cambiado."
                        : request.Operation == "remap_delete" ? "Copia guardada eliminada. El borrador local, si tenía cambios, se conserva."
                        : "Catálogo actualizado. Los borradores con cambios se conservan.";
                    if (request.Operation == "remap_save" && Current.Dirty) Message += " Los cambios posteriores del borrador aún no están guardados.";
                    if (externalChange) Message += " La copia guardada de este borrador cambió en otro editor. Revísala antes de volver a guardar.";
                    if (request.Operation == "remap_y3_test" || request.Operation == "remap_y3_stop")
                        Message = Y3Trial?.Message ?? status.Message;
                    return true;
                }
            }
            catch (Exception ex) {
                lock (sync) {
                    if (operation == generation) {
                        EditingAvailable = false; Revision = null; ReloadRequired = true;
                        Message = ex.Message + (request.Operation == "remap_status" ? "" : " Comprueba el catálogo antes de repetir: el resultado del guardado puede requerir confirmación.");
                    }
                }
                return false;
            }
            finally { lock (sync) { if (operation == generation) IsBusy = false; } }
        }
    }
}
