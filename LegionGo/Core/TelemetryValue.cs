// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.Linq;

namespace LegionTweaks
{
    public static class TelemetryValue
    {
        public static Reading Checked(string key, int? value, string unit, string source, int minimum, int maximum)
        {
            bool valid = value.HasValue && value >= minimum && value <= maximum;
            return new Reading { Key = key, Value = valid ? value : null, Unit = unit, Source = source,
                Error = valid ? null : "El sensor no entregó una lectura válida." };
        }

        public static string Format(Snapshot snapshot, string key, string unit, double divisor = 1, string format = "0")
        {
            var reading = snapshot?.Values?.FirstOrDefault(x => x.Key == key);
            if (reading?.Value == null || reading.Error != null) return "N/D";
            return (reading.Value.Value / divisor).ToString(format, CultureInfo.CurrentCulture) + " " + unit;
        }
    }
}
