// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Runtime.ExceptionServices;

namespace LegionTweaks
{
    public interface IAutomaticPlayerOneControllerService
    {
        void PreparePlayerOneAutomatic(ControllerLayout layout);
    }

    public interface IModePlayerOneControllerService
    {
        void PreparePlayerOneMode(ControllerLayout layout);
    }

    // A separate operation requires an explicit choice in the confirmation dialog.
    // The ordinary operation continues to respect Windows' removal vetoes.
    public interface IBusyPlayerOneControllerService
    {
        void PreparePlayerOneWithBusyReaders(ControllerLayout layout);
    }

    public interface IAutomaticPlayerOnePort : IPlayerOnePort
    {
        // The adapter must identify and validate the original, started Lenovo XInput
        // function and durably journal its recovery before attempting the change.
        void DisablePhysical();
        // Recovery must be idempotent, independent of cancellation and confined to
        // the same original function. It must retain the journal on failure.
        void EnablePhysical();
        // Retire the journal only after independently verifying physical recovery.
        void CompleteRecovery();
    }
    public interface IVirtualControllerSlotsPort
    {
        int?[] ReadVirtualSlots();
    }

    // Only coordinates observations and adapter operations. Native device control,
    // recovery persistence, model gating and target cleanup belong to the caller.
    public static class AutomaticPlayerOnePreparation
    {
        private const int TotalTimeout = 45000, ConnectTimeout = 8000, PollInterval = 100;

        public static void Run(IAutomaticPlayerOnePort port, Action<PlayerOneStage> progress, int targetCount = 1)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            if (targetCount < 1 || targetCount > 2 || (targetCount == 2 && !(port is IVirtualControllerSlotsPort)))
                throw new ArgumentException("No se confirmó la consulta de todos los puestos virtuales.");
            var run = new RunContext(port, targetCount);
            bool recoveryPending = false;
            Exception failure = null;
            try
            {
                var initial = run.Inspect(false);
                RequireSource(initial);
                if (Occupied(initial, targetCount))
                {
                    run.Report(progress, PlayerOneStage.WaitingForFreeSlot);
                    var beforeDisable = run.Inspect(false);
                    RequireSource(beforeDisable);
                    if (Occupied(beforeDisable, targetCount))
                    {
                        // A native operation can change state and then throw. Recovery
                        // must run even if DisablePhysical never returns successfully.
                        recoveryPending = true;
                        port.DisablePhysical();
                        run.Check();
                        while (true)
                        {
                            var waiting = run.Inspect(false);
                            if (waiting.PhysicalDevices == 0 && Count(waiting) == 0) break;
                            run.Pause();
                        }
                    }
                }

                run.Report(progress, PlayerOneStage.Connecting);
                var beforeConnect = run.Inspect(false);
                if (Occupied(beforeConnect, targetCount)
                    || (recoveryPending && (beforeConnect.PhysicalDevices != 0 || Count(beforeConnect) != 0))
                    || (!recoveryPending && beforeConnect.PhysicalDevices != 1))
                    throw Invalid("Cambió el mando o volvió a ocuparse el jugador 1 antes de conectar el virtual.");

                long connectingAt = run.Now();
                port.ConnectVirtual();
                run.Check();
                while (true)
                {
                    run.CheckConnecting(connectingAt);
                    var connecting = run.Inspect(true);
                    if (recoveryPending && connecting.PhysicalDevices != 0)
                        throw Invalid("El mando físico reapareció antes de la recuperación prevista.");
                    bool slotsReady = run.SlotsReady();
                    run.CheckConnecting(connectingAt);
                    if (slotsReady && Reserved(connecting, targetCount)) break;
                    run.Pause();
                }

                // Restore the source as soon as the final target has player 1; do
                // not wait for a UI event or another phase before enabling it.
                if (recoveryPending)
                {
                    port.EnablePhysical();
                    run.Check();
                }
                run.Report(progress, PlayerOneStage.WaitingForPhysical);
                while (true)
                {
                    var waiting = run.Inspect(true);
                    if (IsReady(waiting, run.SlotsReady(), targetCount))
                    {
                        var confirmed = run.Inspect(true);
                        if (IsReady(confirmed, run.SlotsReady(), targetCount))
                        {
                            if (recoveryPending)
                            {
                                port.CompleteRecovery();
                                recoveryPending = false;
                                run.Check();
                            }
                            run.Report(progress, PlayerOneStage.Ready);
                            return;
                        }
                    }
                    run.Pause();
                }
            }
            catch (Exception error) { failure = error; }
            // Recover before propagating so both failures survive consistently
            // across runtimes, without replacing an exception during unwinding.
            if (recoveryPending)
            {
                try { port.EnablePhysical(); }
                catch (Exception recoveryError)
                {
                    const string message = "No se confirmó la recuperación del mando físico; se conserva el registro de recuperación.";
                    if (failure != null) throw new AggregateException(message, failure, recoveryError);
                    throw new InvalidOperationException(message, recoveryError);
                }
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void RequireSource(PlayerOneEnvironment state)
        {
            if (state.PhysicalDevices != 1)
                throw Invalid("No hay un único mando físico Lenovo disponible para preparar el jugador 1.");
        }

        private static bool IsReady(PlayerOneEnvironment state, bool slotsReady, int targets) =>
            state.PhysicalDevices == 1 && state.OtherDevices == targets && Count(state) == targets + 1
            && Reserved(state, targets) && slotsReady;

        private static bool Occupied(PlayerOneEnvironment state, int targets)
        { for (int i = 0; i < targets; i++) if (state.ConnectedSlots[i]) return true; return false; }
        private static bool Reserved(PlayerOneEnvironment state, int targets)
        { for (int i = 0; i < targets; i++) if (!state.ConnectedSlots[i]) return false; return true; }

        private static int Count(PlayerOneEnvironment state)
        {
            int count = 0;
            foreach (bool connected in state.ConnectedSlots) if (connected) count++;
            return count;
        }

        private static InvalidOperationException Invalid(string message) => new InvalidOperationException(message);

        private sealed class RunContext
        {
            private readonly IAutomaticPlayerOnePort port;
            private readonly long started;
            private readonly int targets;
            private long lastClock;
            internal RunContext(IAutomaticPlayerOnePort port, int targets)
            {
                this.port = port; this.targets = targets;
                if (port.Canceled) throw new OperationCanceledException("Preparación del jugador 1 cancelada.");
                started = lastClock = port.ElapsedMilliseconds;
                if (started < 0) throw Invalid("El reloj de preparación no es válido.");
            }
            internal long Now() { Check(); return lastClock; }
            internal void Check()
            {
                if (port.Canceled) throw new OperationCanceledException("Preparación del jugador 1 cancelada.");
                long now = port.ElapsedMilliseconds;
                if (now < lastClock) throw Invalid("El reloj de preparación retrocedió; no se confirmó el jugador 1.");
                lastClock = now;
                if (now - started >= TotalTimeout)
                    throw new TimeoutException("Se agotaron los 45 segundos para preparar el jugador 1; se intentará recuperar el mando físico.");
            }
            internal void CheckConnecting(long connectingAt)
            {
                Check();
                if (lastClock - connectingAt >= ConnectTimeout)
                    throw new TimeoutException(targets == 2 ? "Los dos puestos virtuales no se confirmaron en 8 segundos."
                        : "El driver no confirmó el virtual como jugador 1 en 8 segundos.");
            }
            internal void Report(Action<PlayerOneStage> progress, PlayerOneStage stage)
            {
                Check(); progress?.Invoke(stage); Check();
            }
            internal PlayerOneEnvironment Inspect(bool targetConnected)
            {
                Check();
                var state = port.Inspect();
                Check();
                if (state == null || state.ConnectedSlots == null || state.ConnectedSlots.Length != 4
                    || state.PhysicalDevices < 0 || state.PhysicalDevices > 1 || state.OtherDevices < 0)
                    throw Invalid("La lectura de mandos está incompleta o su identidad es ambigua.");
                var copy = new PlayerOneEnvironment { ConnectedSlots = (bool[])state.ConnectedSlots.Clone(),
                    PhysicalDevices = state.PhysicalDevices, OtherDevices = state.OtherDevices };
                // SetupAPI and XInput are independent snapshots; temporary incomplete
                // enumeration is allowed, but readiness requires both to agree twice.
                if ((!targetConnected && (copy.OtherDevices != 0 || Count(copy) > 1))
                    || (targetConnected && (copy.OtherDevices > targets || Count(copy) > targets + 1)))
                    throw Invalid("Se detectó otro mando durante la preparación del jugador 1.");
                return copy;
            }
            internal bool SlotsReady()
            {
                Check();
                int?[] slots = targets == 1 ? new[] { port.ReadVirtualSlot() } : ((IVirtualControllerSlotsPort)port).ReadVirtualSlots();
                Check();
                if (slots == null || slots.Length != targets) throw Invalid("No se confirmó la lectura de todos los puestos virtuales.");
                bool ready = true;
                for (int i = 0; i < targets; i++)
                {
                    if (slots[i].HasValue && slots[i].Value != i)
                        throw Invalid("El virtual del jugador " + (i + 1) + " no obtuvo su puesto previsto. No se repetirá la reconexión ni se cambiarán otros mandos.");
                    ready &= slots[i].HasValue;
                }
                return ready;
            }
            internal void Pause()
            {
                Check(); long before = lastClock;
                port.Wait(PollInterval);
                Check();
                if (lastClock <= before) throw Invalid("La espera no confirmó el avance del reloj de preparación.");
            }
        }
    }
}
