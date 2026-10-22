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
        private readonly IRtssOverlay rtss;
        private readonly IControllerStartupControl startup;
        private readonly IOptiScalerMenuShortcut optiScaler;
        private readonly IDisplayControl display;

        internal HelperSession(ControlEngine engine, string directory, bool automaticSampling = true, IControllerService controller = null, ILegionShortcuts shortcuts = null, ILegionSpaceControl legionSpace = null, Func<DiagnosticReport> diagnostics = null, Action capturePreflight = null, IRtssOverlay rtss = null, IControllerStartupControl startup = null, IOptiScalerMenuShortcut optiScaler = null, IDisplayControl display = null)
        {
            this.engine = engine;
            this.controller = controller;
            this.shortcuts = shortcuts;
            this.legionSpace = legionSpace;
            this.diagnostics = diagnostics;
            this.capturePreflight = capturePreflight ?? PlayerOnePort.EnsureApplicationsClosed;
            this.rtss = rtss;
            this.startup = startup;
            this.optiScaler = optiScaler;
            this.display = display;
            Recorder = new TelemetryRecorder(() => {
                var response = engine.Execute(new Request { Operation = "status" });
                ObserveTelemetry(response);
                return response;
            }, directory, automaticSampling: automaticSampling);
        }

        internal Response Execute(Request request)
        {
            Response response;
            try
            {
                if (request == null || request.Version != Protocol.Version)
                    throw new InvalidOperationException("La versión del widget y del servicio no coincide.");
                bool displayOperation = (request.Operation ?? "").StartsWith("display_", StringComparison.Ordinal);
                if (request.Display != null && !displayOperation) throw new ArgumentException("Los datos de pantalla solo se admiten en su operación explícita.");
                if (displayOperation && (request.Key != null || request.Values != null || request.Profile != null || request.RtssAppearance != null || request.Ac))
                    throw new ArgumentException("La operación de pantalla no admite ajustes de energía, mando ni overlay.");
                if (request.Operation == "controller_stop" || request.Operation == "restore") startup?.CancelPending();
                if (request.Operation == "controller_split" && request.Profile == null && request.Values == null)
                    startup?.CancelPending(); // Explicit two-player choice wins over pending Desktop startup.
                switch (request.Operation)
                {
                    case "display_status":
                    case "display_preview":
                    case "display_confirm":
                    case "display_revert":
                    case "display_recover":
                        if (display?.Supported != true) throw new InvalidOperationException("Actualiza el helper para gestionar la pantalla.");
                        DisplayStatus displayStatus;
                        if (request.Operation == "display_status" || request.Operation == "display_recover")
                        {
                            if (request.Display != null) throw new ArgumentException("Esta consulta o recuperación no admite una nueva selección de pantalla.");
                            displayStatus = request.Operation == "display_status" ? display.Inspect() : display.Recover();
                        }
                        else if (request.Operation == "display_preview") displayStatus = display.Preview(request.Display);
                        else
                        {
                            if (request.Display?.TrialToken == null || request.Display.SnapshotId != null || request.Display.Mode != null)
                                throw new ArgumentException("La decisión solo admite el identificador de su prueba de pantalla.");
                            displayStatus = display.Decide(request.Display.TrialToken, request.Operation == "display_confirm");
                        }
                        response = new Response { Success = true, Display = displayStatus, Message = displayStatus.Message }; break;
                    case "optiscaler_menu":
                        if (optiScaler?.Supported != true) throw new InvalidOperationException("Actualiza el helper para usar el acceso a OptiScaler.");
                        if (request.Values != null || request.Key != null || request.Profile != null || request.RtssAppearance != null || request.Ac)
                            throw new ArgumentException("El acceso a OptiScaler solo admite el atajo fijo Insert.");
                        optiScaler.OpenMenu();
                        response = new Response { Success = true, Message = "Insert enviado. Comprueba el menú en el juego; no se confirmó que OptiScaler esté instalado ni activo." }; break;
                    case "rtss_configure":
                        if (!(rtss is IRtssAppearanceControl appearanceControl)) throw new InvalidOperationException("Actualiza el helper para personalizar el overlay RTSS.");
                        if (request.Values != null || request.Key != null || request.Profile != null || request.Ac || request.RtssAppearance == null)
                            throw new ArgumentException("Selecciona una apariencia válida para el overlay.");
                        appearanceControl.Configure(request.RtssAppearance.Copy());
                        response = new Response { Success = true, Message = "Apariencia recibida; la activación del overlay se conserva." }; break;
                    case "controller_startup_status":
                        if (startup == null) throw new InvalidOperationException("Actualiza el helper para configurar el inicio de Desktop.");
                        response = new Response { Success = true, Message = "Estado de inicio de Desktop consultado." }; break;
                    case "controller_startup_set":
                        if (startup == null) throw new InvalidOperationException("Actualiza el helper para configurar el inicio de Desktop.");
                        if (request.Profile != null || request.Values == null || request.Values.Length != 1 || request.Values[0] < 0 || request.Values[0] > 1)
                            throw new ArgumentException("Selecciona activar o desactivar el inicio del Desktop integrado.");
                        startup.Configure(request.Values[0] == 1);
                        response = new Response { Success = true, Message = "Preferencia de inicio guardada; no se cambió el mando ni la energía actuales." }; break;
                    case "rtss_status":
                        if (rtss == null) throw new InvalidOperationException("Actualiza el helper para usar el overlay RTSS.");
                        response = new Response { Success = true, Message = "Estado de la publicación RTSS consultado." }; break;
                    case "rtss_set":
                        if (rtss == null) throw new InvalidOperationException("Actualiza el helper para usar el overlay RTSS.");
                        if (request.Values == null || request.Values.Length != 1 || request.Values[0] < 0 || request.Values[0] > 1
                            || request.Key != null || request.Profile != null || request.Ac)
                            throw new ArgumentException("Selecciona activar o desactivar el overlay RTSS.");
                        if (request.RtssAppearance != null)
                        {
                            if (request.Values[0] != 1 || !(rtss is IRtssAppearanceControl control))
                                throw new ArgumentException("La apariencia solo acompaña a la activación en un helper compatible.");
                            control.Configure(request.RtssAppearance.Copy());
                        }
                        rtss.SetEnabled(request.Values[0] == 1);
                        response = new Response { Success = true, Message = request.Values[0] == 1
                            ? "Publicación RTSS solicitada. Consulta el estado; requiere RTSS abierto y un juego compatible."
                            : "Publicación RTSS desactivada. Consulta si queda limpieza pendiente." }; break;
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
            ObserveTelemetry(response);
            response.Recording = Recorder.GetStatus();
            response.Controller = controller?.GetStatus();
            response.Mouse = (controller as IStickMouseControllerService)?.GetMouseStatus();
            response.ControllerStartup = startup?.GetStatus();
            response.SupportsOptiScalerMenu = optiScaler?.Supported == true;
            response.SupportsDisplay = display?.Supported == true;
            response.Shortcuts = shortcuts?.GetStatus();
            try { response.Rtss = rtss?.GetStatus(); } catch { response.Rtss = null; }
            if (request != null && (request.Operation == "status" || (request.Operation ?? "").StartsWith("space_", StringComparison.Ordinal)))
                response.LegionSpace = legionSpace?.GetStatus();
            return response;
        }

        private void ObserveTelemetry(Response response)
        {
            // Optional presentation must never alter a command outcome or the CSV.
            if (response?.State == null) return;
            try { rtss?.Observe(response); } catch { }
        }
        internal void Suspend() { try { startup?.CancelPending(); } catch { } try { rtss?.Suspend(); } catch { } try { display?.CancelPending(); } catch { } }
        internal void Resume() { try { display?.Recover(); } catch { } }
        public void Dispose()
        {
            try { display?.Dispose(); } catch { }
            try { rtss?.Dispose(); } catch { }
            finally { Recorder.Dispose(); }
        }
    }
}
