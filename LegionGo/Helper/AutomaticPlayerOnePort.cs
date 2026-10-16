// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;

namespace LegionTweaks
{
    internal sealed class AutomaticPlayerOnePort : IAutomaticPlayerOnePort
    {
        private readonly PlayerOnePort inspection;
        private readonly PhysicalControllerRecovery recovery;
        private readonly string device;
        private readonly bool allowBusyReaders;
        private readonly ControllerPreparationTrace trace;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool pending;
        private bool inspectionTraced;
        internal AutomaticPlayerOnePort(PlayerOnePort inspection, PhysicalControllerRecovery recovery, string device, bool allowBusyReaders = false, ControllerPreparationTrace trace = null)
        { this.inspection = inspection; this.recovery = recovery; this.device = device; this.allowBusyReaders = allowBusyReaders; this.trace = trace; }
        public long ElapsedMilliseconds => clock.ElapsedMilliseconds;
        public bool Canceled => inspection.Canceled;
        public PlayerOneEnvironment Inspect()
        {
            CheckBudget();
            // Trace the first external probe only, never the polling loop.
            if (trace == null || inspectionTraced) return inspection.Inspect();
            inspectionTraced = true;
            trace.Record(ControllerPreparationStage.BeforeEnvironmentInspection);
            try { return inspection.Inspect(); }
            finally { trace.Record(ControllerPreparationStage.AfterEnvironmentInspection); }
        }
        public void ConnectVirtual() { CheckBudget(); inspection.ConnectVirtual(); CheckBudget(); }
        public int? ReadVirtualSlot() { CheckBudget(); return inspection.ReadVirtualSlot(); }
        public void Wait(int milliseconds) { inspection.Wait(milliseconds); }
        public void DisablePhysical()
        {
            CheckBudget();
            PlayerOnePort.EnsureApplicationsClosed();
            if (!PlayerOnePort.IsPhysicalEndpoint(device) || ControllerDevices.FindPhysicalInstance() != device)
                throw new InvalidOperationException("Cambió la identidad del mando Lenovo antes de reconectarlo.");
            pending = true; // Recovery remains necessary even if the native transition reports failure.
            trace?.Record(ControllerPreparationStage.BeforeDisable);
            try
            {
                recovery.DisablePhysical(device, allowBusyReaders);
                trace?.Record(ControllerPreparationStage.DisableReturned);
            }
            catch
            {
                trace?.Record(ControllerPreparationStage.DisableFailed);
                throw;
            }
            CheckBudget();
        }
        public void EnablePhysical() { recovery.Restore(); }
        public void CompleteRecovery() { CheckBudget(); recovery.CompleteRecovery(); pending = false; }
        internal void CheckBudget()
        {
            inspection.CheckBudget();
            if (clock.ElapsedMilliseconds >= 45000)
                throw new TimeoutException("La reconexión del jugador 1 tardó demasiado. Se recuperará el mando físico.");
            if (pending) recovery.CheckActive();
        }
    }
}
