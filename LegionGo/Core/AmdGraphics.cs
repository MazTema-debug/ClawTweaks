// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace LegionTweaks
{
    [DataContract]
    public sealed class AmdGraphicsValue
    {
        [DataMember] public string Key { get; set; }
        [DataMember] public bool? Supported { get; set; }
        [DataMember] public int? Value { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public int? Minimum { get; set; }
        [DataMember] public int? Maximum { get; set; }
        [DataMember] public int? Step { get; set; }
    }
    [DataContract]
    public sealed class AmdGraphicsStatus
    {
        [DataMember] public bool Available { get; set; }
        [DataMember] public string RuntimeVersion { get; set; }
        [DataMember] public string Topology { get; set; }
        [DataMember] public int GpuCount { get; set; }
        [DataMember] public int DisplayCount { get; set; }
        [DataMember] public AmdGraphicsValue[] Values { get; set; } = new AmdGraphicsValue[0];
        [DataMember] public string CapturedAtUtc { get; set; }
        [DataMember] public string Revision { get; set; }
        [DataMember] public bool HasOriginals { get; set; }
        [DataMember] public bool RecoveryPending { get; set; }
        [DataMember] public string Message { get; set; }
        // Configuration is observable; activity inside a game is not reported by these APIs.
        [DataMember] public bool? ActiveInGame { get; set; }
        public AmdGraphicsValue Find(string key) => Values?.SingleOrDefault(v => v.Key == key);
        public string Fingerprint()
        {
            var value = new { RuntimeVersion, Topology, GpuCount, DisplayCount,
                Items = (Values ?? new AmdGraphicsValue[0]).OrderBy(v => v.Key, StringComparer.Ordinal)
                    .Select(v => new { v.Key, v.Supported, v.Value, v.Error, v.Minimum, v.Maximum, v.Step }).ToArray() };
            // Anonymous types are not a wire contract; use an unambiguous length-prefixed encoding.
            var data = new StringBuilder();
            foreach (string field in new[] { value.RuntimeVersion, value.Topology, GpuCount.ToString(), DisplayCount.ToString() })
                Append(data, field);
            foreach (var item in value.Items) {
                Append(data, item.Key); Append(data, item.Supported?.ToString()); Append(data, item.Value?.ToString());
                Append(data, item.Error); Append(data, item.Minimum?.ToString()); Append(data, item.Maximum?.ToString()); Append(data, item.Step?.ToString());
            }
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(data.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        private static void Append(StringBuilder text, string value) { text.Append(value == null ? -1 : value.Length).Append(':').Append(value); }
    }
    [DataContract]
    public sealed class AmdGraphicsRequest
    {
        [DataMember] public string Revision { get; set; }
        [DataMember] public string Key { get; set; }
        [DataMember] public int? Value { get; set; }
        public void Validate(bool restore)
        {
            if (Revision == null || Revision.Length != 64 || Revision.Any(c => !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')))
                throw new ArgumentException("Consulta primero los ajustes AMD.");
            if (restore) { if (Key != null || Value != null) throw new ArgumentException("Restaurar AMD no admite nuevos valores."); return; }
            if (Key != "rsr" && Key != "afmf" && Key != "rsr_sharpness" || !Value.HasValue)
                throw new ArgumentException("Selecciona un ajuste AMD válido.");
            if (Key != "rsr_sharpness" && Value != 0 && Value != 1)
                throw new ArgumentException("El interruptor AMD solo admite activar o desactivar.");
        }
    }
    public interface IAmdGraphicsBackend : IDisposable
    {
        AmdGraphicsStatus Read();
        void Write(string key, int value);
    }
    [DataContract]
    public sealed class AmdGraphicsBackup
    {
        [DataMember] public AmdGraphicsStatus Original { get; set; }
        [DataMember] public AmdGraphicsStatus Expected { get; set; }
        [DataMember] public bool Pending { get; set; }
        [DataMember] public AmdGraphicsValue[] DormantOriginals { get; set; }
    }
    public interface IAmdGraphicsJournal
    {
        AmdGraphicsBackup Load();
        void Save(AmdGraphicsBackup value);
    }
    public interface IAmdGraphicsControl
    {
        bool Supported { get; }
        bool HasOriginals { get; }
        AmdGraphicsStatus Inspect();
        AmdGraphicsStatus Apply(AmdGraphicsRequest request);
        AmdGraphicsStatus Restore(AmdGraphicsRequest request);
    }
    public sealed class AmdGraphicsControl : IAmdGraphicsControl
    {
        private readonly object sync = new object();
        private readonly Func<IAmdGraphicsBackend> open;
        private readonly IAmdGraphicsJournal journal;
        public bool Supported { get; }
        public bool HasOriginals { get { lock (sync) return journal.Load() != null; } }
        public AmdGraphicsControl(bool supported, Func<IAmdGraphicsBackend> open, IAmdGraphicsJournal journal)
        { Supported = supported; this.open = open; this.journal = journal; }
        // Called only by an explicit general Restore / uninstall restoration request.
        public static AmdGraphicsStatus RestoreSaved(IAmdGraphicsControl control)
        {
            if (control == null || !control.HasOriginals) return null;
            var current = control.Inspect();
            if (!current.Available) throw new InvalidOperationException(current.Message);
            return control.Restore(new AmdGraphicsRequest { Revision = current.Revision });
        }
        public AmdGraphicsStatus Inspect()
        {
            lock (sync) {
                if (!Supported) return Unavailable("Integración AMD no disponible en este modelo.");
                try { using (var backend = open()) return Present(backend.Read(), journal.Load(), "Ajustes globales del controlador. No se ha comprobado actividad dentro del juego."); }
                catch (Exception ex) { return Unavailable("No se pudieron consultar los ajustes AMD: " + ex.Message); }
            }
        }
        public AmdGraphicsStatus Apply(AmdGraphicsRequest request)
        {
            request?.Validate(false);
            if (request == null) throw new ArgumentException("Falta la selección AMD.");
            lock (sync) {
                RequireSupported();
                using (var backend = open()) {
                    var before = backend.Read(); RequireRevision(before, request.Revision);
                    var backup = journal.Load();
                    if (backup != null) RequireOwned(before, backup);
                    RequireSelection(before, request);
                    if (before.Find(request.Key).Value == request.Value)
                        return Present(before, backup, "El ajuste AMD ya tiene ese valor; no se escribió nada.");
                    var original = backup?.Original ?? Copy(before);
                    // Flush the complete original configuration BEFORE the first native write.
                    backup = new AmdGraphicsBackup { Original = original, Expected = Copy(before), Pending = true,
                        DormantOriginals = backup?.DormantOriginals };
                    journal.Save(backup);
                    AmdGraphicsValue[] transactionDormant = null;
                    try {
                        if (request.Key == "rsr" && request.Value == 1)
                            transactionDormant = PrepareRsr(backend, before, backup);
                        backend.Write(request.Key, request.Value.Value);
                        var after = backend.Read(); RequireComplete(after);
                        RequireExpectedEffect(before, after, request.Key);
                        if (after.Topology != before.Topology || after.Find(request.Key)?.Value != request.Value)
                            throw new InvalidOperationException("El controlador no confirmó el ajuste solicitado.");
                        backup.Expected = Copy(after); backup.Pending = false;
                        journal.Save(backup);
                        return Present(after, backup, "Ajuste AMD verificado por lectura. Actividad dentro del juego sin comprobar.");
                    }
                    catch (Exception failure) {
                        bool foreign = failure is ForeignGraphicsChange;
                        if (!foreign) {
                            try { RequireExpectedEffect(before, backend.Read(), request.Key); }
                            catch (ForeignGraphicsChange) { foreign = true; }
                            catch { /* A failed read cannot authorize overwriting unrelated values. Restore still requires a complete current snapshot. */ }
                        }
                        try {
                            var rollbackTarget = before;
                            if (foreign) {
                                rollbackTarget = backend.Read();
                                rollbackTarget.Values = rollbackTarget.Values.Select(v => IsAffected(request.Key, v.Key) ? before.Find(v.Key) : v).ToArray();
                            }
                            RestoreValues(backend, rollbackTarget, transactionDormant);
                            var reverted = backend.Read();
                            if (reverted.Fingerprint() != rollbackTarget.Fingerprint()) throw new InvalidOperationException("La devolución AMD no coincide.");
                            backup.Expected = foreign ? null : Copy(reverted); backup.Pending = foreign;
                            journal.Save(!foreign && reverted.Fingerprint() == original.Fingerprint() ? null : backup);
                        }
                        catch (Exception rollback) {
                            // A missing expected state is deliberately NOT permission to overwrite later changes.
                            backup.Pending = true; backup.Expected = null;
                            if (!foreign) try { backup.Expected = Copy(backend.Read()); } catch { }
                            try { journal.Save(backup); } catch { }
                            throw new InvalidOperationException("Fallo AMD y recuperación pendiente. Se conserva la copia original. " + failure.Message + " / " + rollback.Message);
                        }
                        if (foreign) throw new InvalidOperationException("Se detectaron cambios AMD ajenos. El ajuste solicitado se devolvió y los cambios ajenos se conservaron; copia original pendiente de revisión.");
                        throw new InvalidOperationException("No se aplicó el ajuste AMD; configuración anterior recuperada. " + failure.Message);
                    }
                }
            }
        }
        public AmdGraphicsStatus Restore(AmdGraphicsRequest request)
        {
            request?.Validate(true);
            if (request == null) throw new ArgumentException("Consulta AMD antes de restaurar.");
            lock (sync) {
                RequireSupported();
                using (var backend = open()) {
                    var current = backend.Read(); RequireRevision(current, request.Revision);
                    var backup = journal.Load();
                    if (backup == null) return Present(current, null, "No hay ajustes AMD originales pendientes.");
                    RequireOwned(current, backup, true);
                    backup.Pending = true; journal.Save(backup);
                    try {
                        RestoreValues(backend, backup.Original, backup.DormantOriginals);
                        var restored = backend.Read();
                        if (restored.Fingerprint() != backup.Original.Fingerprint())
                            throw new InvalidOperationException("La restauración AMD no coincide con la copia original.");
                        journal.Save(null);
                        return Present(restored, null, "Ajustes AMD originales restaurados y verificados.");
                    }
                    catch {
                        backup.Expected = null;
                        try { backup.Expected = Copy(backend.Read()); } catch { }
                        try { journal.Save(backup); } catch { }
                        throw;
                    }
                }
            }
        }
        private void RequireSupported() { if (!Supported) throw new InvalidOperationException("Modelo no compatible con ajustes AMD."); }
        private static void RequireRevision(AmdGraphicsStatus state, string revision)
        {
            if (!state.Available || state.Fingerprint() != revision) throw new InvalidOperationException("Los ajustes AMD cambiaron; vuelve a consultar.");
        }
        private static void RequireOwned(AmdGraphicsStatus state, AmdGraphicsBackup backup, bool recovering = false)
        {
            if (backup.Original == null || backup.Expected == null || backup.Expected.Fingerprint() != state.Fingerprint())
                throw new InvalidOperationException("Los ajustes AMD cambiaron fuera de esta operación. Copia original conservada; no se sobrescriben cambios externos.");
            if (backup.Pending && !recovering) throw new InvalidOperationException("Hay una recuperación AMD pendiente; restaura antes de aplicar más cambios.");
        }
        private static void RequireComplete(AmdGraphicsStatus state)
        {
            if (!state.Available || state.GpuCount != 1 || state.DisplayCount != 1 || string.IsNullOrEmpty(state.Topology)
                || state.Values == null || state.Values.Length == 0 || state.Values.Any(v => v.Supported == null || v.Supported == true && (v.Value == null || v.Error != null)))
                throw new InvalidOperationException("No se pudo leer toda la configuración AMD. La primera versión requiere una GPU AMD y una pantalla.");
        }
        private static void RequireSelection(AmdGraphicsStatus state, AmdGraphicsRequest request)
        {
            RequireComplete(state);
            var value = state.Find(request.Key);
            if (value?.Supported != true || !value.Value.HasValue) throw new InvalidOperationException("El controlador no admite ese ajuste AMD.");
            if (request.Key == "rsr_sharpness" && (!value.Minimum.HasValue || !value.Maximum.HasValue || !value.Step.HasValue
                || value.Step <= 0 || request.Value < value.Minimum || request.Value > value.Maximum
                || ((long)request.Value.Value - value.Minimum.Value) % value.Step.Value != 0))
                throw new ArgumentException("Nitidez fuera del rango publicado por AMD.");
        }
        private sealed class ForeignGraphicsChange : InvalidOperationException
        { internal ForeignGraphicsChange() : base("Cambió un ajuste AMD fuera de la selección solicitada.") { } }
        private static bool IsAffected(string selected, string key) => key == selected || selected == "rsr"
            && key != "afmf" && key != "rsr_sharpness";
        private static void RequireExpectedEffect(AmdGraphicsStatus before, AmdGraphicsStatus after, string selected)
        {
            if (before.RuntimeVersion != after.RuntimeVersion || before.GpuCount != after.GpuCount || before.DisplayCount != after.DisplayCount
                || !before.Values.Select(v => v.Key).OrderBy(k => k).SequenceEqual(after.Values.Select(v => v.Key).OrderBy(k => k)))
                throw new InvalidOperationException("La configuración del controlador cambió durante la operación.");
            foreach (var value in before.Values)
                if (!IsAffected(selected, value.Key) && Protocol.Encode(value) != Protocol.Encode(after.Find(value.Key)))
                    throw new ForeignGraphicsChange();
        }
        private AmdGraphicsValue[] PrepareRsr(IAmdGraphicsBackend backend, AmdGraphicsStatus before, AmdGraphicsBackup backup)
        {
            var transaction = new List<AmdGraphicsValue>();
            var originals = new List<AmdGraphicsValue>(backup.DormantOriginals ?? new AmdGraphicsValue[0]);
            foreach (var value in before.Values.Where(v => v.Key.EndsWith("integer_scaling", StringComparison.Ordinal) && v.Supported == false && !v.Value.HasValue)) {
                string scalingKey = value.Key.Substring(0, value.Key.Length - "integer_scaling".Length) + "gpu_scaling";
                var scaling = before.Find(scalingKey);
                if (scaling?.Supported != true || scaling.Value != 0)
                    throw new InvalidOperationException("No se puede conservar la configuración de escalado entero; no se solicitó RSR.");
                // The original GPU scaling flag was already flushed. This preparation belongs
                // solely to the user's explicit RSR enable command, never to inspection.
                backend.Write(scalingKey, 1);
                var prepared = backend.Read(); RequireComplete(prepared);
                var dormant = prepared.Find(value.Key);
                if (dormant?.Supported == false && dormant.Error == null) continue; // Still unsupported with GPU scaling on: no integer feature to preserve.
                if (dormant?.Supported != true || !dormant.Value.HasValue || dormant.Error != null)
                    throw new InvalidOperationException("El controlador no expone el valor de escalado entero; no se solicitó RSR.");
                var saved = new AmdGraphicsValue { Key = value.Key, Supported = true, Value = dormant.Value };
                transaction.Add(saved);
                if (backup.Original.Find(value.Key)?.Value == null && !originals.Any(v => v.Key == value.Key)) originals.Add(saved);
                backup.DormantOriginals = originals.ToArray();
                journal.Save(backup); // The latent value must be durable BEFORE RSR can disable it.
            }
            return transaction.ToArray();
        }
        private static void RestoreValues(IAmdGraphicsBackend backend, AmdGraphicsStatus target, AmdGraphicsValue[] dormant = null)
        {
            var current = backend.Read(); RequireComplete(current);
            RequireComplete(target);
            if (current.Topology != target.Topology || current.RuntimeVersion != target.RuntimeVersion)
                throw new InvalidOperationException("La GPU, pantalla o versión ADLX cambió; se conserva la copia original.");
            // RSR can suppress Boost/RIS/integer scaling. Disable it before restoring those dependencies.
            bool dependenciesDiffer = target.Values.Any(v => v.Key != "rsr" && v.Key != "afmf" && v.Key != "rsr_sharpness"
                && (current.Find(v.Key)?.Value != v.Value || current.Find(v.Key)?.Supported != v.Supported));
            dependenciesDiffer |= (dormant ?? new AmdGraphicsValue[0]).Any(v => current.Find(v.Key)?.Value != v.Value);
            if (current.Find("rsr")?.Value == 1 && (target.Find("rsr")?.Value != 1 || dependenciesDiffer)) backend.Write("rsr", 0);
            foreach (var value in dormant ?? new AmdGraphicsValue[0]) {
                if (!value.Key.EndsWith("integer_scaling", StringComparison.Ordinal) || value.Value != 0 && value.Value != 1)
                    throw new InvalidOperationException("Valor de recuperación AMD no válido.");
                string scalingKey = value.Key.Substring(0, value.Key.Length - "integer_scaling".Length) + "gpu_scaling";
                if (backend.Read().Find(scalingKey)?.Value != 1) backend.Write(scalingKey, 1);
            }
            var restoreValues = target.Values.Where(v => !(dormant ?? new AmdGraphicsValue[0]).Any(d => d.Key == v.Key))
                .Concat(dormant ?? new AmdGraphicsValue[0]);
            foreach (var value in restoreValues.OrderBy(RestoreOrder)) {
                if (!value.Value.HasValue || value.Key == "rsr") continue;
                var actual = backend.Read().Find(value.Key);
                if (actual?.Value != value.Value) backend.Write(value.Key, value.Value.Value);
                if ((dormant ?? new AmdGraphicsValue[0]).Any(d => d.Key == value.Key)
                    && backend.Read().Find(value.Key)?.Value != value.Value)
                    throw new InvalidOperationException("El controlador no confirmó el valor original de escalado entero.");
            }
            if (target.Find("rsr")?.Supported == true && backend.Read().Find("rsr")?.Value != target.Find("rsr").Value)
                backend.Write("rsr", target.Find("rsr").Value.Value);
        }
        private static int RestoreOrder(AmdGraphicsValue value) => value.Key.EndsWith("gpu_scaling", StringComparison.Ordinal) ? (value.Value == 1 ? 0 : 4)
            : value.Key.EndsWith("scaling_mode", StringComparison.Ordinal) ? 1 : value.Key.EndsWith("integer_scaling", StringComparison.Ordinal) ? 3 : 2;
        private static AmdGraphicsStatus Copy(AmdGraphicsStatus value) => Protocol.Decode<AmdGraphicsStatus>(Protocol.Encode(value));
        private static AmdGraphicsStatus Present(AmdGraphicsStatus state, AmdGraphicsBackup backup, string message)
        {
            state.Revision = state.Fingerprint(); state.HasOriginals = backup != null; state.RecoveryPending = backup?.Pending == true;
            state.Message = message; state.ActiveInGame = null; return state;
        }
        private static AmdGraphicsStatus Unavailable(string message) => new AmdGraphicsStatus { Message = message, CapturedAtUtc = DateTime.UtcNow.ToString("o") };
    }
}
