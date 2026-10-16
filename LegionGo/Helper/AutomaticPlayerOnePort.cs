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
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool pending;
        internal AutomaticPlayerOnePort(PlayerOnePort inspection, PhysicalControllerRecovery recovery, string device)
        { this.inspection = inspection; this.recovery = recovery; this.device = device; }
        public long ElapsedMilliseconds => clock.ElapsedMilliseconds;
        public bool Canceled => inspection.Canceled;
        public PlayerOneEnvironment Inspect() { CheckBudget(); return inspection.Inspect(); }
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
            recovery.DisablePhysical(device);
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
