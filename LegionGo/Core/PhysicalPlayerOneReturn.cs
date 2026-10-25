// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    public interface IPhysicalControllerReturn
    {
        void ReturnPhysical();
    }

    internal interface IPhysicalPlayerOneReturnPort
    {
        void CheckInitial();
        bool[] ReadSlots();
        void Cycle(); // Existing independently supervised original-mode restoration.
    }

    internal static class PhysicalPlayerOneReturn
    {
        internal static int Run(IPhysicalPlayerOneReturnPort port)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            port.CheckInitial();
            var before = port.ReadSlots(); RequireSingle(before);
            if (before[0]) return 0;
            port.Cycle(); // One attempt; no retries or virtual-controller creation.
            var after = port.ReadSlots(); RequireSingle(after);
            if (!after[0]) throw new InvalidOperationException("El modo físico volvió, pero no se confirmó como jugador 1.");
            return 0;
        }

        private static void RequireSingle(bool[] slots)
        {
            if (slots == null || slots.Length != 4)
                throw new InvalidOperationException("No se confirmaron los puestos del mando físico.");
            int count = 0; foreach (bool connected in slots) if (connected) count++;
            if (count != 1) throw new InvalidOperationException("La devolución necesita un único mando físico y ningún virtual.");
        }
    }
}
