// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Runtime.Serialization;

namespace LegionTweaks
{
    public interface IHideConfiguration
    {
        bool Active { get; set; }
        bool Inverted { get; }
        string[] Applications { get; set; }
        string[] Devices { get; set; }
    }
    [DataContract]
    public sealed class HideLease
    {
        [DataMember] public string Device { get; set; }
        [DataMember] public string Application { get; set; }
        [DataMember] public bool ActiveBefore { get; set; }
        [DataMember] public bool AddedApplication { get; set; }
        [DataMember] public int OwnerPid { get; set; }
        [DataMember] public long OwnerStarted { get; set; }
        public static bool Contains(string[] values, string value) => values.Contains(value, StringComparer.OrdinalIgnoreCase);
        public static void Check(IHideConfiguration config, string device)
        {
            if (config.Inverted) throw new InvalidOperationException("HidHide usa la lista invertida. Revisa su configuración antes de activar el mando virtual.");
            if (Contains(config.Devices, device)) throw new InvalidOperationException("El mando ya está oculto por otra configuración. Libéralo en HidHide antes de continuar.");
            if (!config.Active && config.Devices.Length > 0) throw new InvalidOperationException("HidHide tiene otros dispositivos pendientes de ocultar. Revisa su configuración antes de continuar.");
        }
        // Save this intent durably BEFORE Activate: recovery also handles a partial activation.
        public void Activate(IHideConfiguration config)
        {
            Check(config, Device);
            if (AddedApplication) config.Applications = config.Applications.Concat(new[] { Application }).ToArray();
            config.Devices = config.Devices.Concat(new[] { Device }).ToArray();
            config.Active = true;
            if (!config.Active || config.Inverted || !Contains(config.Devices, Device) || !Contains(config.Applications, Application))
                throw new InvalidOperationException("HidHide no confirmó la protección contra entradas duplicadas.");
        }
        public void Restore(IHideConfiguration config)
        {
            config.Devices = config.Devices.Where(x => !string.Equals(x, Device, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (AddedApplication) config.Applications = config.Applications.Where(x => !string.Equals(x, Application, StringComparison.OrdinalIgnoreCase)).ToArray();
            // A different application may have added a hidden device meanwhile. Preserve it.
            if (!ActiveBefore && config.Devices.Length == 0) config.Active = false;
            if (Contains(config.Devices, Device) || (AddedApplication && Contains(config.Applications, Application)))
                throw new InvalidOperationException("No se pudo devolver el mando físico. Se conserva el respaldo para reintentar.");
        }
    }
}
