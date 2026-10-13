// SPDX-License-Identifier: AGPL-3.0-only
using System;

namespace LegionTweaks
{
    internal sealed class HelperSession : IDisposable
    {
        private readonly ControlEngine engine;
        internal readonly TelemetryRecorder Recorder;
        private readonly IControllerService controller;

        internal HelperSession(ControlEngine engine, string directory, bool automaticSampling = true, IControllerService controller = null)
        {
            this.engine = engine;
            this.controller = controller;
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
            return response;
        }

        public void Dispose() { Recorder.Dispose(); }
    }
}
