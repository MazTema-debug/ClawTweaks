// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    internal sealed class HelperSession : IDisposable
    {
        private readonly ControlEngine engine;
        internal readonly TelemetryRecorder Recorder;
        private readonly IControllerService controller;
        private readonly ILegionShortcuts shortcuts;
        private readonly ILegionSpaceControl legionSpace;
        private readonly Func<DiagnosticReport> diagnostics;
        private readonly Action capturePreflight;

        internal HelperSession(ControlEngine engine, string directory, bool automaticSampling = true, IControllerService controller = null, ILegionShortcuts shortcuts = null, ILegionSpaceControl legionSpace = null, Func<DiagnosticReport> diagnostics = null, Action capturePreflight = null)
        {
            this.engine = engine;
            this.controller = controller;
            this.shortcuts = shortcuts;
            this.legionSpace = legionSpace;
            this.diagnostics = diagnostics;
            this.capturePreflight = capturePreflight ?? PlayerOnePort.EnsureApplicationsClosed;
            Recorder = new TelemetryRecorder(() => engine.Execute(new Request { Operation = "status" }), directory, automaticSampling: automaticSampling);
        }

        internal Response Execute(Request request)
        {
            Response response;
            try
            {
                if (request == null || request.Version != Protocol.Version)
                    throw new InvalidOperationException("La versión del widget y del servicio no coincide.");
                switch (request.Operation)
                {
                    case "diagnostics":
                        if (diagnostics == null) throw new InvalidOperationException("Actualiza el helper para consultar el diagnóstico.");
                        response = new Response { Success = true, Message = "Diagnóstico consultado sin reconectar el mando.", Diagnostics = diagnostics() }; break;
                    case "space_status":
                    case "space_disable":
                    case "space_start":
                    case "space_restore":
                        if (legionSpace == null) throw new InvalidOperationException("Actualiza el helper para controlar Legion Space.");
                        if (request.Operation != "space_status") legionSpace.Execute(request.Operation);
                        response = new Response { Success = true, Message = request.Operation == "space_disable" ? "Legion Space detenido y deshabilitado."
                            : request.Operation == "space_start" ? "Legion Space iniciado con arranque manual."
                            : request.Operation == "space_restore" ? "Estado guardado de Legion Space restaurado." : "Estado de Legion Space." }; break;
                    case "legion_capture_start":
                        var capture = shortcuts as ILegionButtonCapture;
                        if (capture == null) throw new InvalidOperationException("Actualiza el helper para identificar los botones Legion.");
                        if (request.Values == null || request.Values.Length != 2 || request.Values[0] < 0 || request.Values[0] > 6
                            || request.Values[1] < 5 || request.Values[1] > 30) throw new ArgumentException("Selecciona un botón y una duración de 5–30 segundos.");
                        if (controller?.GetStatus()?.PreparingPlayerOne == true) throw new InvalidOperationException("Termina la preparación del jugador 1 antes de identificar botones.");
                        capturePreflight();
                        capture.StartCapture(request.Values[0], request.Values[1]);
                        response = new Response { Success = true, Message = "Identificación iniciada. Pulsa solo el botón elegido y suéltalo tres veces." }; break;
                    case "legion_capture_cancel":
                        var cancelCapture = shortcuts as ILegionButtonCapture;
                        if (cancelCapture == null) throw new InvalidOperationException("Actualiza el helper para identificar los botones Legion.");
                        cancelCapture.CancelCapture();
                        response = new Response { Success = true, Message = "Identificación detenida. Suelta los botones para volver a usar el atajo." }; break;
                    case "shortcut_set":
                        if (shortcuts == null) throw new InvalidOperationException("Actualiza el helper para usar los botones Legion.");
                        if (request.Values == null || request.Values.Length != 1 || request.Values[0] < 0 || request.Values[0] > 3)
                            throw new ArgumentException("Selecciona un atajo Legion válido.");
                        shortcuts.Configure((LegionShortcutMode)request.Values[0]);
                        response = new Response { Success = true, Message = "Atajo Legion guardado. Suelta los botones antes de probarlo." }; break;
                    case "recording_start":
                        Recorder.Start();
                        response = new Response { Success = true, Message = "Grabando CSV. Puedes cerrar Game Bar." }; break;
                    case "recording_stop":
                        Recorder.Stop();
                        response = new Response { Success = true, Message = "Registro detenido. El CSV está guardado." }; break;
                    case "recording_status":
                        response = new Response { Success = true, Message = "Estado del registro." }; break;
                    default: response = engine.Execute(request); break;
                }
            }
            catch (Exception ex) { response = new Response { Success = false, Message = ex.Message }; }
            response.Recording = Recorder.GetStatus();
            response.Controller = controller?.GetStatus();
            response.Mouse = (controller as IStickMouseControllerService)?.GetMouseStatus();
            response.Shortcuts = shortcuts?.GetStatus();
            if (request != null && (request.Operation == "status" || (request.Operation ?? "").StartsWith("space_", StringComparison.Ordinal)))
                response.LegionSpace = legionSpace?.GetStatus();
            return response;
        }

        public void Dispose() { Recorder.Dispose(); }
    }
}
