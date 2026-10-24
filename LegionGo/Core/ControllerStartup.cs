// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class ControllerStartupPreferences
    {
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public bool Enabled { get; set; }
        public void Validate() {
            if (Version != 1) throw new InvalidOperationException("Preferencias de inicio incompatibles; se conserva el archivo.");
        }
    }

    [DataContract]
    public sealed class ControllerStartupAttempt
    {
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public string Id { get; set; }
        [DataMember(IsRequired = true)] public long StartedUtc { get; set; }
        [DataMember(IsRequired = true)] public string State { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Error { get; set; }
        public void Validate() {
            Guid id;
            if (Version != 1 || Id == null || Id.Length != 32 || !Guid.TryParseExact(Id, "N", out id)
                || StartedUtc <= 0 || StartedUtc > DateTime.MaxValue.Ticks
                || (State != "starting" && State != "active" && State != "failed" && State != "canceled")
                || (Error != null && Error.Length > 2048))
                throw new InvalidOperationException("El último inicio no se pudo confirmar; se conserva su registro.");
        }
    }

    [DataContract]
    public sealed class ControllerStartupStatus
    {
        [DataMember] public bool Supported { get; set; }
        [DataMember] public bool Enabled { get; set; }
        [DataMember] public string ProfileName { get; set; }
        [DataMember] public string State { get; set; }
        [DataMember] public string Message { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Error { get; set; }
        [DataMember(EmitDefaultValue = false)] public ControllerStartupDecision LastDecision { get; set; }
        [DataMember(EmitDefaultValue = false)] public string HistoryError { get; set; }
        [DataMember(EmitDefaultValue = false)] public bool SupportsHistory { get; set; }
    }

    public interface IControllerStartupStorage
    {
        ControllerStartupPreferences LoadPreferences();
        void SavePreferences(ControllerStartupPreferences preferences);
        ControllerStartupAttempt LoadAttempt();
        void SaveAttempt(ControllerStartupAttempt attempt);
        void ClearAttempt();
        bool ConsumeInstallationSuppression();
    }

    public interface IControllerStartupControl
    {
        ControllerStartupStatus GetStatus();
        void Configure(bool enabled);
        void CancelPending();
    }

    // One explicit startup call, no timers, input readers, power engine or retries.
    public sealed class ControllerStartup : IControllerStartupControl, IControllerStartupHistoryControl
    {
        private readonly object gate = new object();
        private readonly IControllerStartupStorage storage;
        private readonly Action<ControllerLayout> start;
        private readonly Func<ControllerStatus> current;
        private readonly Func<string> skipReason;
        private readonly bool supported;
        private bool loaded, enabled, attempted;
        private ControllerStartupAttempt pending;
        private string state = "disabled", message = "Inicio automático desactivado.", error;
        private readonly IControllerStartupHistoryStorage historyStorage;
        private ControllerStartupHistory history = new ControllerStartupHistory();
        private ControllerStartupDecision recordedDecision;
        private string historyError, runId;
        private long runStarted;
        private bool historyLoaded, runEnabled;

        public ControllerStartup(IControllerStartupStorage storage, Action<ControllerLayout> start,
            Func<ControllerStatus> current, Func<string> skipReason, bool supported = true)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
            this.start = start ?? throw new ArgumentNullException(nameof(start));
            this.current = current ?? throw new ArgumentNullException(nameof(current));
            this.skipReason = skipReason ?? throw new ArgumentNullException(nameof(skipReason));
            this.supported = supported;
            historyStorage = storage as IControllerStartupHistoryStorage;
            if (!supported) { state = "unsupported"; message = "Inicio del mando no compatible con este equipo."; return; }
            if (historyStorage != null) {
                try { var savedHistory = historyStorage.LoadHistory() ?? throw new InvalidOperationException(); savedHistory.Validate(); history = savedHistory; historyLoaded = true; }
                catch (Exception) { historyError = "No se pudo leer el historial de inicio. Se conserva el archivo."; }
            }
            try {
                var preference = storage.LoadPreferences() ?? throw new InvalidOperationException("No se recibieron las preferencias de inicio.");
                preference.Validate(); enabled = preference.Enabled; loaded = true;
                if (enabled) { state = "configured"; message = "Desktop configurado para el próximo inicio del helper."; }
            }
            catch (Exception ex) { Block("No se cargó el inicio automático; no se activó el mando.", ex); }
        }

        public ControllerStartupStatus GetStatus()
        {
            lock (gate) return new ControllerStartupStatus { Supported = supported, Enabled = enabled,
                ProfileName = DesktopControllerProfile.Name, State = state, Message = message, Error = error,
                LastDecision = history.Decisions.Length == 0 ? null : CopyDecision(history.Decisions[history.Decisions.Length - 1]),
                HistoryError = historyError, SupportsHistory = supported && historyStorage != null };
        }

        public ControllerStartupHistory GetHistory()
        {
            lock (gate) {
                RequireSupported();
                if (!historyLoaded || historyError != null) throw new InvalidOperationException(historyError ?? "Historial no disponible en este helper.");
                return Protocol.Decode<ControllerStartupHistory>(Protocol.Encode(history));
            }
        }

        public void Configure(bool value)
        {
            lock (gate) {
                RequireSupported();
                if (pending != null || current()?.PreparingPlayerOne == true)
                    throw new InvalidOperationException("Termina o cancela la preparación antes de guardar el inicio automático.");
                try {
                    storage.SavePreferences(new ControllerStartupPreferences { Enabled = value });
                    var saved = storage.LoadPreferences(); saved.Validate();
                    if (saved.Enabled != value) throw new InvalidOperationException("No se confirmó la preferencia guardada.");
                    enabled = saved.Enabled; loaded = true;
                    // Saving explicitly rearms a failed/interrupted startup, but
                    // never starts or alters the current controller session.
                    storage.ClearAttempt();
                    error = null; state = value ? "configured" : "disabled";
                    message = value ? "Guardado: Desktop se activará al iniciar el helper. La sesión actual no cambia."
                        : "Inicio automático desactivado. La sesión actual no cambia.";
                }
                catch (Exception ex) { Block("No se confirmó toda la configuración del inicio automático.", ex); throw; }
            }
        }

        public void RunAtStartup()
        {
            lock (gate) {
                if (attempted) return;
                attempted = true;
                runId = Guid.NewGuid().ToString("N"); runStarted = DateTime.UtcNow.Ticks; runEnabled = enabled;
                try {
                    if (!supported || !loaded) return;
                    if (storage.ConsumeInstallationSuppression()) {
                        state = "skipped"; message = "Inicio automático omitido durante la actualización. Se conserva la preferencia."; return;
                    }
                    if (!enabled) return;
                    var previous = storage.LoadAttempt(); previous?.Validate();
                    if (previous != null && (previous.State == "starting" || previous.State == "failed")) {
                        state = "blocked"; error = previous.Error;
                        message = "El inicio anterior falló o se interrumpió. Revisa el mando y vuelve a guardar esta opción para rearmarlo."; return;
                    }
                    var cached = current();
                    if (cached == null || cached.Active || cached.PreparingPlayerOne) {
                        state = "skipped"; message = "Inicio automático omitido: el estado del mando no está disponible o ya hay una sesión."; return;
                    }
                    string reason = skipReason();
                    if (!string.IsNullOrEmpty(reason)) { state = "skipped"; message = "Inicio automático omitido: " + reason; return; }
                    pending = new ControllerStartupAttempt { Id = Guid.NewGuid().ToString("N"), StartedUtc = DateTime.UtcNow.Ticks, State = "starting" };
                    try { storage.SaveAttempt(Copy(pending)); }
                    catch { pending = null; throw; } // No durable intent: no controller action.
                    state = "starting"; error = null; message = "Activando Desktop como jugador 1 al iniciar el helper…";
                    RecordDecision();
                    try { start(DesktopControllerProfile.CreateLayout()); }
                    catch (Exception ex) { Finish("failed", "No se inició Desktop. Revisa el mando y vuelve a guardar esta opción antes de otro inicio automático.", ex.Message); }
                }
                catch (Exception ex) { Block("Inicio automático detenido; no se repetirá durante esta sesión del helper.", ex); }
                finally { RecordDecision(); }
            }
        }

        public void Observe(ControllerStatus value)
        {
            lock (gate) {
                if (pending == null || value == null || value.PreparingPlayerOne) return;
                if (value.Active && value.Connected && value.Hidden && value.VirtualSlot == 0
                    && string.IsNullOrEmpty(value.Error) && string.IsNullOrEmpty(value.SlotError))
                    Finish("active", "Desktop se activó automáticamente como jugador 1.", null);
                else Finish("failed", "Desktop no confirmó el jugador 1. Revisa el mando y vuelve a guardar esta opción para rearmarlo.",
                    value.Error ?? "No se confirmó la sesión virtual del inicio.");
            }
        }

        public void CancelPending()
        {
            lock (gate) if (pending != null)
                Finish("canceled", "Inicio de Desktop cancelado. No se repetirá en esta sesión del helper.", null);
        }

        private void Finish(string nextState, string text, string failure)
        {
            var attempt = Copy(pending); attempt.State = nextState;
            attempt.Error = failure != null && failure.Length > 2048 ? failure.Substring(0, 2048) : failure;
            pending = null; state = nextState; message = text; error = attempt.Error;
            try { storage.SaveAttempt(attempt); }
            catch (Exception ex) {
                // The durable 'starting' record remains a block for the next
                // helper. Never stop input or throw from its status callback.
                error = "No se guardó el resultado del inicio. " + ex.Message;
            }
            RecordDecision();
        }
        private void RecordDecision()
        {
            if (!supported || !historyLoaded || historyStorage == null || runId == null) return;
            if (recordedDecision != null && recordedDecision.State == state && recordedDecision.Message == Limit(message)
                && recordedDecision.Error == Limit(error)) return;
            var decision = new ControllerStartupDecision { Id = runId, StartedUtc = runStarted, UpdatedUtc = DateTime.UtcNow.Ticks,
                EnabledAtStartup = runEnabled, State = state, Message = Limit(message), Error = Limit(error) };
            try {
                var next = history.With(decision);
                historyStorage.RecordDecision(CopyDecision(decision));
                history = next; recordedDecision = decision; historyError = null;
            }
            catch (Exception) {
                // Advisory evidence must never interrupt input, discard the
                // durable attempt guard, or cause another preparation/retry.
                historyError = "No se pudo guardar el resultado en el historial. Los registros anteriores se conservan.";
                historyLoaded = false;
            }
        }
        private static string Limit(string value)
        {
            if (value == null || value.Length <= 512) return value;
            return value.Substring(0, char.IsHighSurrogate(value[511]) ? 511 : 512);
        }
        private static ControllerStartupDecision CopyDecision(ControllerStartupDecision value) =>
            Protocol.Decode<ControllerStartupDecision>(Protocol.Encode(value));
        private static ControllerStartupAttempt Copy(ControllerStartupAttempt value) =>
            Protocol.Decode<ControllerStartupAttempt>(Protocol.Encode(value));
        private void Block(string text, Exception ex) { state = "blocked"; message = text; error = ex.Message; }
        private void RequireSupported() { if (!supported) throw new InvalidOperationException("Inicio de Desktop no compatible con este equipo."); }
    }
}
