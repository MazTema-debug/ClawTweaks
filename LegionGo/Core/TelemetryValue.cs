// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.Linq;

namespace LegionTweaks
{
    public static class TelemetryValue
    {
        public static int? Value(Snapshot snapshot, string key)
        {
            var reading = snapshot?.Values?.FirstOrDefault(x => x.Key == key);
            return reading?.Error == null ? reading?.Value : null;
        }

        public static string ModeName(int? mode)
        {
            switch (mode)
            {
                case 1: return "Silencioso";
                case 2: return "Equilibrado";
                case 3: return "Rendimiento";
                case 255: return "Personalizado";
                default: return "N/D";
            }
        }

        public static string DescribeLimits(Snapshot snapshot)
        {
            var mode = Value(snapshot, "Mode");
            string label = mode == 255 ? "Límites personalizados" :
                (mode == 1 || mode == 2 || mode == 3 ? "Valores guardados (Personalizado inactivo)" : "Valores personalizados (modo sin confirmar)");
            return "Modo Lenovo: " + ModeName(mode) + "\n" + label + " · SPL / SPPT / FPPT: "
                + Format(snapshot, "Spl", "W") + " / " + Format(snapshot, "Sppt", "W") + " / " + Format(snapshot, "Fppt", "W");
        }

        public static Reading Checked(string key, int? value, string unit, string source, int minimum, int maximum)
        {
            bool valid = value.HasValue && value >= minimum && value <= maximum;
            return new Reading { Key = key, Value = valid ? value : null, Unit = unit, Source = source,
                Error = valid ? null : "El sensor no entregó una lectura válida." };
        }

        public static string PowerNote(Snapshot snapshot)
        {
            int? gpu = Value(snapshot, "GpuPower"), asic = Value(snapshot, "AsicPower");
            return gpu.HasValue && asic.HasValue && gpu > asic
                ? "Potencia por revisar: AMD informa GPU > ASIC. Lecturas sin corregir." : null;
        }

        public static string Format(Snapshot snapshot, string key, string unit, double divisor = 1, string format = "0")
        {
            var reading = snapshot?.Values?.FirstOrDefault(x => x.Key == key);
            if (reading?.Value == null || reading.Error != null) return "N/D";
            return (reading.Value.Value / divisor).ToString(format, CultureInfo.CurrentCulture) + " " + unit;
        }
    }
}
