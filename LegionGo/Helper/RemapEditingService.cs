// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Security.Cryptography;

namespace LegionTweaks
{
    // Catalog mutations remain separate from the explicit, bounded Y3 trial.
    internal sealed class RemapEditingService : IDisposable
    {
        private readonly object sync = new object();
        private readonly ControllerRemapWorkspace workspace;
        private readonly IFpsY3Trial trial;
        internal bool Supported { get; }
        internal RemapEditingService(IRemapProfileStorage storage, bool supported, IFpsY3Trial trial = null)
        { workspace = new ControllerRemapWorkspace(storage, new ControllerRemapEngine()); Supported = supported; this.trial = trial; }
        internal FpsY3TrialStatus TrialStatus => trial?.GetStatus();
        internal void StopTrial() { var state = trial?.GetStatus(); if (state?.Active == true || state?.RecoveryPending == true) trial.Stop(); }
        internal RemapEditingStatus Execute(string operation, RemapEditingRequest request)
        {
            lock (sync)
            {
                if (!Supported) throw new InvalidOperationException("Este modelo no admite el editor de mando.");
                if (operation == "remap_y3_stop") {
                    if (request != null) throw new ArgumentException("Devolver Y3 no admite una distribución nueva.");
                    trial?.Stop();
                    try { workspace.Load(); return Snapshot(); }
                    catch (Exception ex) {
                        return new RemapEditingStatus { Y3Trial = trial?.GetStatus(),
                            Message = "Y3 devuelto. No se confirmó el catálogo; el borrador se conserva. " + ex.Message };
                    }
                }
                if (operation == "remap_status")
                {
                    if (request != null) throw new ArgumentException("La consulta del editor no admite cambios.");
                    workspace.Load(); return Snapshot();
                }
                if (operation != "remap_save" && operation != "remap_delete" && operation != "remap_activate" && operation != "remap_y3_test")
                    throw new ArgumentException("Operación del editor desconocida.");
                if (request == null || request.Revision == null || request.Revision.Length != 64)
                    throw new ArgumentException("Consulta el catálogo antes de editarlo.");
                if (operation == "remap_save" && (request.Profile == null || request.Id != null)
                    || operation != "remap_save" && (request.Profile != null || string.IsNullOrEmpty(request.Id)))
                    throw new ArgumentException("Datos incompatibles con la operación del editor.");
                request.Profile?.Validate();
                workspace.Load();
                if (!string.Equals(request.Revision, Revision(workspace.Saved), StringComparison.Ordinal))
                    throw new InvalidOperationException("La distribución guardada cambió; vuelve a consultar antes de editar.");
                if (operation == "remap_activate")
                    throw new InvalidOperationException("La activación aún no está disponible: falta verificar la entrada, sustitución nativa y salida del remapeo.");
                if (operation == "remap_y3_test") {
                    if (trial == null) throw new InvalidOperationException("Este helper no incorpora la prueba nativa de Y3.");
                    var saved = Array.Find(workspace.Saved.Profiles, p => string.Equals(p.Id, request.Id, StringComparison.OrdinalIgnoreCase));
                    if (saved == null) throw new ArgumentException("Selecciona una distribución guardada.");
                    FpsY3TrialRules.Require(saved); trial.Start(saved.Copy()); return Snapshot();
                }
                var active = trial?.GetStatus();
                if (operation == "remap_delete" && (active?.Active == true || active?.RecoveryPending == true)
                    && string.Equals(active.ProfileId, request.Id, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Devuelve Y3 antes de eliminar la distribución de la prueba.");
                if (operation == "remap_save") { workspace.SetDraft(request.Profile); workspace.SaveDraft(); }
                else workspace.Delete(request.Id);
                return Snapshot();
            }
        }
        private RemapEditingStatus Snapshot()
        {
            var catalog = workspace.Saved;
            return new RemapEditingStatus { EditingAvailable = true, ActivationAvailable = false,
                Catalog = catalog, Revision = Revision(catalog), Y3Trial = trial?.GetStatus(),
                Message = trial?.GetStatus().Message ?? "Editor disponible. Guardar no activa el mando; la ruta de remapeo real sigue pendiente." };
        }
        private static string Revision(RemapProfiles catalog)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(catalog.Encode())).Replace("-", "").ToLowerInvariant();
        }
        public void Dispose() { trial?.Dispose(); }
    }
}
