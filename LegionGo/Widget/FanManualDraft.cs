// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;

namespace LegionTweaks.Widget
{
    // Presentation only. There is no manual-fan API/range confirmed for this delivery.
    internal sealed class FanManualDraft
    {
        internal int? EditedPercent { get; private set; }
        internal int? ConfirmedPercent => null;
        internal bool CanApply => false;
        internal string DraftText => EditedPercent.HasValue ? "Borrador manual: " + EditedPercent + " %" : "Borrador manual: sin editar";
        internal void Edit(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 100)
                throw new ArgumentOutOfRangeException(nameof(value));
            EditedPercent = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }
        internal string RpmText(Snapshot snapshot, bool current)
        {
            if (snapshot?.Supported != true) return "RPM reales: sin lectura confirmada.";
            if (!current) return "RPM reales: lectura anterior; actualiza las lecturas.";
            var readings = snapshot.Values?.Where(x => x?.Key == "FanRpm").ToArray();
            var reading = readings?.Length == 1 ? readings[0] : null;
            if (reading?.Value == null || !string.IsNullOrEmpty(reading.Error) || reading.Value < 0 || reading.Value > 15000
                || reading.Unit != "RPM" || string.IsNullOrWhiteSpace(reading.Source))
                return "RPM reales: no disponibles.";
            return "RPM reales: " + reading.Value + " RPM · " + reading.Source;
        }
    }
}
