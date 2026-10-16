// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    [DataContract]
    public sealed class LegionSpaceState
    {
        [DataMember(IsRequired = true)] public int StartMode { get; set; }
        [DataMember(IsRequired = true)] public bool DelayedAutoStart { get; set; }
        [DataMember(IsRequired = true)] public bool Running { get; set; }
        public void Validate()
        {
            if (StartMode != 2 && StartMode != 3 && StartMode != 4)
                throw new InvalidOperationException("El tipo de inicio de Legion Space no es compatible.");
        }
        public LegionSpaceState Copy() => new LegionSpaceState { StartMode = StartMode, DelayedAutoStart = DelayedAutoStart, Running = Running };
        public bool SameAs(LegionSpaceState other) => other != null && StartMode == other.StartMode
            && DelayedAutoStart == other.DelayedAutoStart && Running == other.Running;
    }
    [DataContract]
    public sealed class LegionSpaceOriginal
    {
        [DataMember(IsRequired = true)] public int Version { get; set; } = 1;
        [DataMember(IsRequired = true)] public LegionSpaceState State { get; set; }
        public void Validate()
        {
            if (Version != 1 || State == null) throw new InvalidOperationException("Respaldo de Legion Space incompatible. Se conserva para revisión.");
            State.Validate();
        }
    }
    [DataContract]
    public sealed class LegionSpaceStatus
    {
        [DataMember] public bool Available { get; set; }
        [DataMember] public bool CanChange { get; set; }
        [DataMember] public LegionSpaceState State { get; set; }
        [DataMember] public LegionSpaceState Original { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public string CapturedAtUtc { get; set; }
    }
    public interface ILegionSpaceBackend
    {
        LegionSpaceState Read();
        void Configure(int startMode, bool delayedAutoStart);
        void SetRunning(bool running);
    }
    public interface ILegionSpaceJournal
    {
        LegionSpaceOriginal Load();
        void Save(LegionSpaceOriginal original);
        void Clear();
    }
    public interface ILegionSpaceControl
    {
        LegionSpaceStatus GetStatus();
        void Execute(string operation);
    }
    // Independent of power originals, profiles and controller recovery. No startup/exit writes.
    public sealed class LegionSpaceControl : ILegionSpaceControl
    {
        private readonly object gate = new object();
        private readonly ILegionSpaceBackend backend;
        private readonly ILegionSpaceJournal journal;
        private readonly bool supported;
        public LegionSpaceControl(ILegionSpaceBackend backend, ILegionSpaceJournal journal, bool supported)
        { this.backend = backend; this.journal = journal; this.supported = supported; }
        private LegionSpaceState Read()
        {
            var state = backend.Read();
            if (state == null) throw new InvalidOperationException("Estado de Legion Space sin confirmar.");
            state.Validate(); return state;
        }
        private LegionSpaceOriginal Original()
        { var value = journal.Load(); value?.Validate(); return value; }
        public LegionSpaceStatus GetStatus()
        {
            lock (gate)
            {
                var status = new LegionSpaceStatus { CapturedAtUtc = DateTime.UtcNow.ToString("O") };
                if (!supported) { status.Error = "Control disponible solo en la Legion Go 8APU1 / Z1 Extreme."; return status; }
                try { status.State = Read(); status.Available = true; status.Original = Original()?.State.Copy(); status.CanChange = true; }
                catch (Exception ex) { status.Error = ex.Message; }
                return status;
            }
        }
        public void Execute(string operation)
        {
            if (operation != "space_disable" && operation != "space_start" && operation != "space_restore")
                throw new ArgumentException("Acción de Legion Space inválida.");
            if (!supported) throw new InvalidOperationException("Equipo no compatible.");
            lock (gate)
            {
                var before = Read();
                var original = Original(); // A damaged journal must fail before any mutation.
                if (operation == "space_restore" && original == null)
                    throw new InvalidOperationException("No hay un estado guardado de Legion Space.");
                var desired = operation == "space_restore" ? original.State.Copy()
                    : new LegionSpaceState { StartMode = operation == "space_disable" ? 4 : 3,
                        DelayedAutoStart = before.DelayedAutoStart, Running = operation == "space_start" };
                if (original == null) journal.Save(new LegionSpaceOriginal { State = before.Copy() });
                try { Apply(desired); }
                catch (Exception ex)
                {
                    try { Apply(before); }
                    catch (Exception rollback) { throw new InvalidOperationException(ex.Message + " Recuperación pendiente: " + rollback.Message, ex); }
                    throw new InvalidOperationException(ex.Message + " Se recuperó el estado anterior.", ex);
                }
                // Keep the backup if deleting it fails, without undoing a successful restoration.
                if (operation == "space_restore") journal.Clear();
            }
        }
        private void Apply(LegionSpaceState desired)
        {
            desired.Validate();
            var current = Read();
            if (!desired.Running && current.Running) backend.SetRunning(false);
            // A disabled but still-running original is legal in Windows. Start on demand first,
            // then restore Disabled, rather than trying to start a disabled service.
            int firstMode = desired.Running && desired.StartMode == 4 ? 3 : desired.StartMode;
            current = Read();
            if (current.StartMode != firstMode || current.DelayedAutoStart != desired.DelayedAutoStart)
                backend.Configure(firstMode, desired.DelayedAutoStart);
            if (desired.Running && !Read().Running) backend.SetRunning(true);
            if (desired.StartMode != firstMode) backend.Configure(desired.StartMode, desired.DelayedAutoStart);
            if (!Read().SameAs(desired)) throw new InvalidOperationException("Windows no confirmó el estado solicitado de Legion Space.");
        }
    }
}
