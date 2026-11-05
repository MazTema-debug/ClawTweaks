// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using System.Linq;

namespace LegionTweaks.QamProbe
{
    internal enum QamQuery { Power, Display, Lossless }
    internal static class QamReadText
    {
        internal static string Render(QamQuery query, Response response, DateTime utcNow, bool previous = false)
        {
            if (response == null || response.Version != Protocol.Version || !response.Success)
                return "Lectura sin confirmar.\n" + (response?.Message ?? "El helper no respondió.");
            switch (query) {
                case QamQuery.Power:
                    var power = response.State;
                    if (power?.Supported != true) return "Potencia sin confirmar.";
                    return TelemetryValue.DescribeLimits(power) + "\nAlimentación: " + (power.OnAc ? "AC" : "DC")
                        + "\nPotencia ASIC: " + Watts(power, "AsicPower")
                        + "\nFuente ASIC: " + (Reading(power, "AsicPower")?.Source ?? "N/D")
                        + "\n" + Stamp(power.CapturedAtUtc, utcNow, previous);
                case QamQuery.Display:
                    var display = response.Display?.Info;
                    if (response.Display?.Supported != true || display?.Current == null) return "Resolución sin confirmar.";
                    return "Resolución: " + display.Current.Width + " × " + display.Current.Height
                        + "\nFrecuencia: " + display.Current.Frequency + " Hz"
                        + "\nPantallas activas: " + display.ActiveDisplays
                        + "\n" + Stamp(display.CapturedAtUtc, utcNow, previous);
                case QamQuery.Lossless:
                    var lossless = response.LosslessScaling;
                    if (lossless?.Supported != true) return "Lossless Scaling sin confirmar.";
                    return "Instalado: " + (lossless.Installed ? "Sí" : "No detectado")
                        + "\nProceso: " + (!lossless.Running.HasValue ? "Sin confirmar" : lossless.Running.Value ? "En ejecución" : "No detectado")
                        + (lossless.Installed ? "\nVersión: " + (lossless.FileVersion ?? "N/D") : "")
                        + "\n" + Stamp(lossless.CapturedAtUtc, utcNow, previous)
                        + "\nEl proceso no confirma escalado ni LSFG activo.";
                default: throw new ArgumentOutOfRangeException(nameof(query));
            }
        }
        private static Reading Reading(Snapshot snapshot, string key)
        {
            var readings = snapshot.Values?.Where(x => x != null && x.Key == key).ToArray();
            return readings?.Length == 1 ? readings[0] : null;
        }
        private static string Watts(Snapshot snapshot, string key)
        {
            var value = Reading(snapshot, key);
            if (value?.Value == null || !string.IsNullOrEmpty(value.Error)) return "N/D";
            if (key == "AsicPower" && value.Unit != "W") return "N/D";
            return value.Value.Value.ToString(CultureInfo.InvariantCulture) + " W";
        }
        internal static string CapturedAt(QamQuery query, Response response) => query == QamQuery.Power ? response?.State?.CapturedAtUtc
            : query == QamQuery.Display ? response?.Display?.Info?.CapturedAtUtc : response?.LosslessScaling?.CapturedAtUtc;
        private static string Stamp(string text, DateTime now, bool previous)
        {
            if (!DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset capture)
                || capture.Offset != TimeSpan.Zero)
                return "Hora de lectura sin confirmar; vuelve a consultar.";
            var age = now.ToUniversalTime() - capture.UtcDateTime;
            string time = capture.ToLocalTime().ToString("HH:mm:ss");
            return age.TotalSeconds < -TelemetryFreshness.FutureToleranceSeconds ? "Hora de lectura futura sin confirmar: " + time
                : previous || age.TotalSeconds > TelemetryFreshness.MaximumAgeSeconds ? "Lectura anterior: " + time + " · vuelve a consultar."
                : "Lectura: " + time + " · solo lectura.";
        }
    }
}
