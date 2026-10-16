// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    public enum PlayerOneStage { WaitingForFreeSlot, Connecting, WaitingForPhysical, Ready }

    public sealed class PlayerOneEnvironment
    {
        public bool[] ConnectedSlots { get; set; }
        public int PhysicalDevices { get; set; }
        // XUSB interfaces other than the Lenovo source, including our connected virtual target.
        public int OtherDevices { get; set; }
    }

    public interface IPlayerOnePort
    {
        long ElapsedMilliseconds { get; }
        bool Canceled { get; }
        PlayerOneEnvironment Inspect();
        void ConnectVirtual();
        int? ReadVirtualSlot();
        void Wait(int milliseconds);
    }

    public interface IPlayerOneControllerService
    {
        void PreparePlayerOne(ControllerLayout layout);
    }

    // Coordinates a manual FPS-switch sequence. It never disables, resets or hides hardware.
    // The caller owns the target and must disconnect it on cancellation or failure.
    public static class PlayerOnePreparation
    {
        private const int TotalTimeout = 90000, ConnectTimeout = 8000, PollInterval = 100;
        private const string RestoreFps = " Deja el interruptor FPS en OFF para devolver el mando físico.";

        public static void Run(IPlayerOnePort port, Action<PlayerOneStage> progress)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            var run = new RunContext(port);
            var initial = run.Inspect(false);
            int initialSlots = Count(initial);
            if (initialSlots == 1 && initial.PhysicalDevices != 1)
                throw Invalid("Hay un puesto XInput ocupado sin un único mando Lenovo identificado.");

            if (initial.ConnectedSlots[0])
            {
                run.Report(progress, PlayerOneStage.WaitingForFreeSlot);
                while (true)
                {
                    var waiting = run.Inspect(false);
                    // A disappearing source may briefly retain its old XInput slot. Never connect
                    // until that cached slot is gone, regardless of the interface count.
                    if (Count(waiting) == 0) break;
                    run.Pause();
                }
            }

            long connectingAt = run.Now();
            run.Report(progress, PlayerOneStage.Connecting);
            var beforeConnect = run.Inspect(false);
            if (beforeConnect.ConnectedSlots[0])
                throw Invalid("El jugador 1 volvió a ocuparse antes de conectar el virtual. Repite la preparación con el juego cerrado.");
            if (Count(beforeConnect) == 1 && beforeConnect.PhysicalDevices != 1)
                throw Invalid("Cambió la identidad del mando antes de conectar el virtual.");
            run.Check();
            port.ConnectVirtual();
            run.Check();

            while (true)
            {
                run.CheckConnecting(connectingAt);
                var connecting = run.Inspect(true);
                int? slot = run.Slot();
                run.CheckConnecting(connectingAt);
                if (slot == 0 && connecting.ConnectedSlots[0]) break;
                run.Pause();
            }

            run.Report(progress, PlayerOneStage.WaitingForPhysical);
            while (true)
            {
                var waiting = run.Inspect(true);
                int? slot = run.Slot();
                if (IsReady(waiting, slot))
                {
                    // Read back both enumeration and the target index again before announcing
                    // readiness; a previous snapshot is not proof of the current arrangement.
                    var confirmed = run.Inspect(true);
                    if (IsReady(confirmed, run.Slot()))
                    {
                        run.Report(progress, PlayerOneStage.Ready);
                        return;
                    }
                }
                run.Pause();
            }
        }

        private static bool IsReady(PlayerOneEnvironment state, int? slot) =>
            state.PhysicalDevices == 1 && state.OtherDevices == 1 && Count(state) == 2
            && state.ConnectedSlots[0] && slot == 0;

        private static int Count(PlayerOneEnvironment state)
        {
            int count = 0;
            foreach (bool connected in state.ConnectedSlots) if (connected) count++;
            return count;
        }

        private static InvalidOperationException Invalid(string message) => new InvalidOperationException(message + RestoreFps);

        private sealed class RunContext
        {
            private readonly IPlayerOnePort port;
            private readonly long started;
            private long lastClock;
            internal RunContext(IPlayerOnePort port)
            {
                this.port = port;
                if (port.Canceled) throw new OperationCanceledException("Preparación del jugador 1 cancelada." + RestoreFps);
                started = lastClock = port.ElapsedMilliseconds;
                if (started < 0) throw Invalid("El reloj de preparación no es válido.");
            }
            internal long Now() { Check(); return lastClock; }
            internal void Check()
            {
                if (port.Canceled) throw new OperationCanceledException("Preparación del jugador 1 cancelada." + RestoreFps);
                long now = port.ElapsedMilliseconds;
                if (now < lastClock) throw Invalid("El reloj de preparación retrocedió; no se confirmó el jugador 1.");
                lastClock = now;
                if (now - started >= TotalTimeout)
                    throw new TimeoutException("Se agotaron los 90 segundos para preparar el jugador 1. Cierra el juego antes de volver a intentarlo." + RestoreFps);
            }
            internal void CheckConnecting(long connectingAt)
            {
                Check();
                if (lastClock - connectingAt >= ConnectTimeout)
                    throw new TimeoutException("El driver no confirmó el virtual como jugador 1 en 8 segundos." + RestoreFps);
            }
            internal void Report(Action<PlayerOneStage> progress, PlayerOneStage stage)
            {
                Check();
                progress?.Invoke(stage);
                Check();
            }
            internal PlayerOneEnvironment Inspect(bool targetConnected)
            {
                Check();
                var state = port.Inspect();
                Check();
                if (state == null || state.ConnectedSlots == null || state.ConnectedSlots.Length != 4
                    || state.PhysicalDevices < 0 || state.PhysicalDevices > 1 || state.OtherDevices < 0)
                    throw Invalid("La lectura de mandos está incompleta o no identifica un único mando físico.");
                var copy = new PlayerOneEnvironment { ConnectedSlots = (bool[])state.ConnectedSlots.Clone(),
                    PhysicalDevices = state.PhysicalDevices, OtherDevices = state.OtherDevices };
                if ((!targetConnected && (copy.OtherDevices != 0 || Count(copy) > 1))
                    // SetupAPI and XInput are separate observations: the physical slot can return
                    // before its interface is enumerated. Wait for complete evidence in IsReady.
                    || (targetConnected && (copy.OtherDevices > 1 || Count(copy) > 2)))
                    throw Invalid("Se detectó otro mando o cambió la cantidad de dispositivos. Desconecta los mandos adicionales antes de repetir la preparación.");
                return copy;
            }
            internal int? Slot()
            {
                Check();
                int? slot = port.ReadVirtualSlot();
                Check();
                if (slot.HasValue && slot.Value != 0)
                    throw Invalid("El virtual no obtuvo el jugador 1. No se cambiará automáticamente el orden de otros mandos.");
                return slot;
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
