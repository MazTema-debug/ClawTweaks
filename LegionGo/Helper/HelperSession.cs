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

        internal HelperSession(ControlEngine engine, string directory, bool automaticSampling = true, IControllerService controller = null, ILegionShortcuts shortcuts = null)
        {
            this.engine = engine;
            this.controller = controller;
            this.shortcuts = shortcuts;
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
            response.Shortcuts = shortcuts?.GetStatus();
            return response;
        }

        public void Dispose() { Recorder.Dispose(); }
    }
}
