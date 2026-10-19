// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    // The supervisor owns the journal and every mode writer. This adapter only
    // binds that recovery operation to the existing player-one coordinator.
    internal interface IPlayerOneModeRecovery
    {
        ControllerModeState Inspect();
        void EnterDirectInput(string originalReceiver, string originalLocation);
        void CheckActive();
        void Restore();
        void CompleteRecovery();
    }

    internal sealed class ModePlayerOnePort : IAutomaticPlayerOnePort
    {
        private readonly IPlayerOnePort inspection;
        private readonly IPlayerOneModeRecovery recovery;
        private readonly Func<string> currentPhysical;
        private readonly Func<string, string> receiverRoot;
        private readonly long started;
        private long lastClock;
        private string originalReceiver, originalLocation;
        private bool pending, connectAttempted;

        internal ModePlayerOnePort(IPlayerOnePort inspection, IPlayerOneModeRecovery recovery,
            Func<string> currentPhysical, Func<string, string> receiverRoot)
        {
            this.inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
            this.recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
            this.currentPhysical = currentPhysical ?? throw new ArgumentNullException(nameof(currentPhysical));
            this.receiverRoot = receiverRoot ?? throw new ArgumentNullException(nameof(receiverRoot));
            started = lastClock = inspection.ElapsedMilliseconds;
            CheckClock();
        }

        public long ElapsedMilliseconds => inspection.ElapsedMilliseconds;
        public bool Canceled => inspection.Canceled;

        public PlayerOneEnvironment Inspect()
        {
            CheckBudget();
            var state = inspection.Inspect();
            CheckBudget();
            return state;
        }

        public void DisablePhysical()
        {
            CheckBudget();
            if (pending || connectAttempted)
                throw Invalid("La preparación de modo ya comenzó; no se repetirá el cambio.");
            RequireEnvironment(Inspect(), 1, true);
            string endpoint = RequirePhysical();
            string root = receiverRoot(endpoint);
            if (!ControllerModeProtocol.IsOriginalReceiver(root))
                throw Invalid("El receptor del mando no pertenece al par de modos admitido.");

            var state = recovery.Inspect();
            CheckBudget();
            RequireMode(state, root, state == null ? null : state.Location, 1);
            // The mode read can block. Confirm the exact source again immediately
            // before asking the supervisor to arm its immutable recovery binding.
            RequireEnvironment(Inspect(), 1, true);
            if (!Same(RequirePhysical(), endpoint) || !Same(receiverRoot(endpoint), root))
                throw Invalid("Cambió la identidad del mando antes de preparar el modo temporal.");
            CheckBudget();
            originalReceiver = root;
            originalLocation = state.Location;
            // Enter can persist and change the mode, then throw. Recovery is owed
            // even when this call never returns successfully.
            pending = true;
            recovery.EnterDirectInput(originalReceiver, originalLocation);
            CheckBudget();
        }

        public void ConnectVirtual()
        {
            CheckBudget();
            if (connectAttempted)
                throw Invalid("El virtual ya tuvo su único intento de conexión.");
            if (pending)
            {
                var state = recovery.Inspect();
                CheckBudget();
                RequireMode(state, originalReceiver, originalLocation, 2);
                RequireEnvironment(Inspect(), 0, false);
            }
            else
            {
                // A source already in a later slot needs no mode query or change.
                RequireEnvironment(Inspect(), 1, false);
                if (!ControllerModeProtocol.IsOriginalReceiver(receiverRoot(RequirePhysical())))
                    throw Invalid("No se confirmó el receptor original antes de conectar el virtual.");
            }
            CheckBudget();
            connectAttempted = true;
            inspection.ConnectVirtual();
            CheckBudget();
        }

        public int? ReadVirtualSlot()
        {
            CheckBudget();
            int? slot = inspection.ReadVirtualSlot();
            CheckBudget();
            return slot;
        }

        public void Wait(int milliseconds)
        {
            CheckBudget();
            inspection.Wait(milliseconds);
            CheckBudget();
        }

        public void EnablePhysical()
        {
            // Never gate recovery on the canceled operation or its expired lease.
            // A preflight failure has no journal and requires no restoration.
            if (pending) recovery.Restore();
        }

        public void CompleteRecovery()
        {
            if (!pending) throw Invalid("No hay un cambio de modo pendiente de confirmar.");
            CheckBudget();
            var state = recovery.Inspect();
            CheckBudget();
            RequireMode(state, originalReceiver, originalLocation, 1);
            if (!Same(receiverRoot(RequirePhysical()), originalReceiver))
                throw Invalid("No volvió la función física del receptor original.");
            CheckBudget();
            // The supervisor independently verifies recovery and retirement. If
            // it throws, keep pending so the coordinator still requests recovery.
            recovery.CompleteRecovery();
            pending = false;
        }

        internal void CheckBudget()
        {
            CheckClock();
            if (pending)
            {
                recovery.CheckActive();
                CheckClock();
            }
        }

        private void CheckClock()
        {
            if (Canceled) throw new OperationCanceledException("Preparación del jugador 1 cancelada; se recuperará el mando físico.");
            long now = inspection.ElapsedMilliseconds;
            if (started < 0 || now < lastClock)
                throw Invalid("El reloj de preparación de modo no es válido o retrocedió.");
            lastClock = now;
            if (now - started >= ControllerModeRecoveryRules.LeaseMilliseconds)
                throw new TimeoutException("Se agotaron los 30 segundos de preparación de modo; se recuperará el mando físico.");
        }

        private string RequirePhysical()
        {
            string endpoint = currentPhysical();
            if (!PlayerOnePort.IsPhysicalEndpoint(endpoint))
                throw Invalid("No se confirmó una única función física Lenovo.");
            return endpoint;
        }

        private static void RequireMode(ControllerModeState state, string original, string location, int expected)
        {
            if (state == null || state.Mode != expected || !state.Present || state.Problem != 0
                || !string.Equals(state.Service, "usbccgp", StringComparison.OrdinalIgnoreCase)
                || !ControllerModeProtocol.IsOriginalReceiver(original)
                || !ControllerModeProtocol.IsLocationPath(location) || !Same(state.Location, location)
                || (expected == 1 ? !Same(state.Receiver, original) : !ControllerModeProtocol.IsTemporaryReceiver(state.Receiver)))
                throw Invalid("No se confirmó el mismo receptor saludable en el modo esperado.");
        }

        private static void RequireEnvironment(PlayerOneEnvironment state, int physical, bool occupied)
        {
            if (state == null || state.PhysicalDevices != physical || state.OtherDevices != 0
                || state.ConnectedSlots == null || state.ConnectedSlots.Length != 4
                || state.ConnectedSlots[0] != occupied)
                throw Invalid("Cambió el mando o el puesto 1 antes de la operación.");
            int slots = 0;
            foreach (bool connected in state.ConnectedSlots) if (connected) slots++;
            if (slots > physical || (occupied && slots != 1))
                throw Invalid("Se detectó un puesto adicional antes de la operación.");
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static InvalidOperationException Invalid(string message) => new InvalidOperationException(message);
    }
}
