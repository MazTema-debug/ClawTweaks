// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Security.Cryptography;

namespace LegionTweaks
{
    // Catalog-only service. There is deliberately no connection to a controller
    // worker, native writer, output injector, startup preference or power engine.
    internal sealed class RemapEditingService
    {
        private readonly object sync = new object();
        private readonly ControllerRemapWorkspace workspace;
        internal bool Supported { get; }
        internal RemapEditingService(IRemapProfileStorage storage, bool supported)
        { workspace = new ControllerRemapWorkspace(storage, new ControllerRemapEngine()); Supported = supported; }
        internal RemapEditingStatus Execute(string operation, RemapEditingRequest request)
        {
            lock (sync)
            {
                if (!Supported) throw new InvalidOperationException("Este modelo no admite el editor de mando.");
                if (operation == "remap_status")
                {
                    if (request != null) throw new ArgumentException("La consulta del editor no admite cambios.");
                    workspace.Load(); return Snapshot();
                }
                if (operation != "remap_save" && operation != "remap_delete" && operation != "remap_activate")
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
                if (operation == "remap_save") { workspace.SetDraft(request.Profile); workspace.SaveDraft(); }
                else workspace.Delete(request.Id);
                return Snapshot();
            }
        }
        private RemapEditingStatus Snapshot()
        {
            var catalog = workspace.Saved;
            return new RemapEditingStatus { EditingAvailable = true, ActivationAvailable = false,
                Catalog = catalog, Revision = Revision(catalog),
                Message = "Editor disponible. Guardar no activa el mando; la ruta de remapeo real sigue pendiente." };
        }
        private static string Revision(RemapProfiles catalog)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(catalog.Encode())).Replace("-", "").ToLowerInvariant();
        }
    }
}
