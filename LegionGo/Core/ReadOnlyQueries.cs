// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace LegionTweaks
{
    // Separate from the package-authenticated command channel. No settings or context payloads.
    public static class ReadOnlyQueries
    {
        public const string PipePrefix = "LegionTweaks.Z1E.ReadOnly.v1.";
        public static bool Allows(Request request) => request != null && request.Version == Protocol.Version
            && (request.Operation == "telemetry" || request.Operation == "display_status" || request.Operation == "lossless_status")
            && request.Key == null && request.Values == null && !request.Ac && request.Profile == null
            && request.RtssAppearance == null && request.Display == null && request.GameBarInput == null
            && request.Remap == null && request.AmdGraphics == null && request.DesktopView == null;

        public static async Task<string> ReadLine(StreamReader reader)
        {
            var text = new StringBuilder();
            var character = new char[1];
            while (await reader.ReadAsync(character, 0, 1).ConfigureAwait(false) != 0) {
                if (character[0] == '\n') return text.ToString();
                if (text.Length == Protocol.MaxMessageLength) throw new InvalidDataException("Respuesta IPC demasiado larga.");
                text.Append(character[0]);
            }
            throw new EndOfStreamException("El helper cerró la conexión de lectura.");
        }
    }

    // Delegates expose only individual read operations, never the general command dispatcher.
    public sealed class ReadOnlyStatusService
    {
        private readonly bool supported;
        private readonly Func<Response> power;
        private readonly Func<DisplayInfo> display;
        private readonly Func<LosslessScalingStatus> lossless;
        public ReadOnlyStatusService(bool supported, Func<Response> power, Func<DisplayInfo> display, Func<LosslessScalingStatus> lossless)
        { this.supported = supported; this.power = power; this.display = display; this.lossless = lossless; }

        public Response Execute(Request request)
        {
            if (!ReadOnlyQueries.Allows(request)) return Error("Este canal solo admite consultas sin ajustes.");
            if (!supported) return Error("Solo se admite la Legion Go original 83E1 / Z1 Extreme.");
            try {
                switch (request.Operation) {
                    case "telemetry":
                        var value = power();
                        if (value?.Success != true || value.State?.Supported != true)
                            return Error(value?.Message ?? "Potencia sin confirmar.");
                        return new Response { Success = true, State = value.State, Message = "Potencia consultada; solo lectura." };
                    case "display_status":
                        // This read must not renew the command channel's selection/trial tokens.
                        var info = display();
                        if (info?.Current == null) return Error("Resolución sin confirmar.");
                        return new Response { Success = true, SupportsDisplay = true,
                            Display = new DisplayStatus { Supported = true, Info = info, Message = "Pantalla consultada; solo lectura." } };
                    default:
                        var state = lossless();
                        if (state?.Supported != true) return Error("Estado de Lossless Scaling sin confirmar.");
                        return new Response { Success = true, SupportsLosslessScaling = true, LosslessScaling = state, Message = state.Message };
                }
            }
            catch (Exception ex) { return Error(ex.Message); }
        }
        private static Response Error(string message) => new Response { Success = false, Message = message };
    }
}
