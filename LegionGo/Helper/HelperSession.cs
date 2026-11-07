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
        private readonly ILosslessScalingStatusProvider losslessScaling;
        private readonly RemapEditingService remap;
        private readonly IAmdGraphicsControl amdGraphics;
        private readonly IPawnIoDependency pawnIo;
        private readonly IQamLifecycle qam;

        internal HelperSession(ControlEngine engine, string directory, bool automaticSampling = true, IControllerService controller = null, ILegionShortcuts shortcuts = null, ILegionSpaceControl legionSpace = null, Func<DiagnosticReport> diagnostics = null, Action capturePreflight = null, IRtssOverlay rtss = null, IControllerStartupControl startup = null, IOptiScalerMenuShortcut optiScaler = null, IDisplayControl display = null, ILosslessScalingStatusProvider losslessScaling = null, RemapEditingService remap = null, IAmdGraphicsControl amdGraphics = null, IPawnIoDependency pawnIo = null, IQamLifecycle qam = null)
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
            this.losslessScaling = losslessScaling;
            this.remap = remap;
            this.amdGraphics = amdGraphics;
            this.pawnIo = pawnIo;
            this.qam = qam;
            Recorder = new TelemetryRecorder(() => {
                var response = engine.Execute(new Request { Operation = "status" });
                ObserveTelemetry(response);
                return response;
            }, directory, automaticSampling: automaticSampling);
        }

        internal Response Execute(Request request, int authenticatedClientPid = 0)
        {
            Response response;
            try
            {
                if (request == null || request.Version != Protocol.Version)
                    throw new InvalidOperationException("La versión del widget y del servicio no coincide.");
                bool qamOperation = (request.Operation ?? "").StartsWith("qam_", StringComparison.Ordinal);
                if (request.DesktopView != null && !qamOperation) throw new ArgumentException("La vista de escritorio solo se admite al registrarla.");
                if (qamOperation) QamLifecycleRules.Validate(request);
                if ((request.Operation ?? "").StartsWith("pawnio_", StringComparison.Ordinal)
                    && (request.Key != null || request.Values != null || request.Ac || request.Profile != null
                    || request.Display != null || request.RtssAppearance != null || request.GameBarInput != null
                    || request.Remap != null || request.AmdGraphics != null))
                    throw new ArgumentException("La operación de PawnIO no admite ajustes, rutas ni enlaces.");
                bool amdOperation = (request.Operation ?? "").StartsWith("amd_graphics_", StringComparison.Ordinal);
                if (request.AmdGraphics != null && !amdOperation)
                    throw new ArgumentException("Los datos AMD solo se admiten en su operación explícita.");
                if (amdOperation && (request.Key != null || request.Values != null || request.Ac || request.Profile != null
                    || request.Display != null || request.RtssAppearance != null || request.GameBarInput != null || request.Remap != null))
                    throw new ArgumentException("La operación AMD no admite ajustes de otros módulos.");
                bool remapOperation = (request.Operation ?? "").StartsWith("remap_", StringComparison.Ordinal);
                if (request.Remap != null && !remapOperation)
                    throw new ArgumentException("Los datos del editor solo se admiten en su operación explícita.");
                if (remapOperation && (request.Key != null || request.Values != null || request.Ac || request.Profile != null
                    || request.Display != null || request.RtssAppearance != null || request.GameBarInput != null))
                    throw new ArgumentException("El editor no admite ajustes de energía, mando, pantalla ni overlay.");
                if (request.GameBarInput != null) {
                    if (request.Operation != "recording_status" || request.Key != null || request.Values != null || request.Ac
                        || request.Profile != null || request.Display != null || request.RtssAppearance != null)
                        throw new ArgumentException("El contexto del visor solo admite una consulta de estado.");
                    request.GameBarInput.Validate();
                    (controller as IGameBarControllerInput)?.ObserveGameBarInput(request.GameBarInput.Copy());
                }
                bool displayOperation = (request.Operation ?? "").StartsWith("display_", StringComparison.Ordinal);
                if (request.Display != null && !displayOperation) throw new ArgumentException("Los datos de pantalla solo se admiten en su operación explícita.");
                if (displayOperation && (request.Key != null || request.Values != null || request.Profile != null || request.RtssAppearance != null || request.Ac))
                    throw new ArgumentException("La operación de pantalla no admite ajustes de energía, mando ni overlay.");
                if (request.Operation == "controller_stop" || request.Operation == "restore") startup?.CancelPending();
                if (request.Operation == "controller_stop" || request.Operation == "restore" || request.Operation == "profile_apply"
                    || request.Operation == "controller_split" || request.Operation == "controller_split_general" || request.Operation == "controller_desktop"
                    || request.Operation == "controller_player_one" || request.Operation == "controller_player_one_auto"
                    || request.Operation == "controller_player_one_busy" || request.Operation == "controller_player_one_mode"
                    || request.Operation == "fps_keys_set" || request.Operation == "legion_capture_start"
                    || request.Operation == "space_start" || request.Operation == "space_restore") remap?.StopTrial();
                if ((request.Operation == "controller_split" || request.Operation == "controller_split_general")
                    && request.Profile == null && request.Values == null && request.Key == null && !request.Ac
                    && request.RtssAppearance == null && request.Display == null)
                    startup?.CancelPending(); // Explicit two-player choice wins over pending Desktop startup.
                switch (request.Operation)
                {
                    case "qam_status":
                        if (qam == null) throw new InvalidOperationException("Actualiza el helper para usar el acceso al menú rápido.");
                        response = new Response { Success = true, Message = "Estado del menú consultado; no se aplicaron ajustes." }; break;
                    case "qam_open":
                        if (qam == null) throw new InvalidOperationException("Actualiza el helper para abrir el menú rápido.");
                        var opened = qam.Open();
                        response = new Response { Success = true, Message = opened.Message }; break;
                    case "qam_configure":
                        if (qam == null) throw new InvalidOperationException("Actualiza el helper para configurar los menús.");
                        var configured = qam.Configure(request.Values[0] == 1, (LegionShortcutMode)request.Values[1], (LegionShortcutMode)request.Values[2]);
                        response = new Response { Success = true, Message = configured.Message }; break;
                    case "qam_desktop_register":
                        if (qam == null) throw new InvalidOperationException("Actualiza el helper para registrar la app de escritorio.");
                        qam.RegisterDesktop(request.DesktopView, authenticatedClientPid);
                        response = new Response { Success = true, Message = "Vista de escritorio registrada; no se abrió ni minimizó." }; break;
                    case "pawnio_status":
                    case "pawnio_install":
                        if (pawnIo?.GetStatus().Supported != true)
                            throw new InvalidOperationException("Comprueba la conexión y actualiza el helper para consultar PawnIO.");
                        var pawnStatus = request.Operation == "pawnio_install" ? pawnIo.BeginInstall() : pawnIo.Refresh();
                        response = new Response { Success = pawnStatus.InstallState != "error" && pawnStatus.InstallState != "failed",
                            PawnIo = pawnStatus, Message = pawnStatus.Message }; break;
                    case "restore":
                        response = engine.Execute(request);
                        if (response.Success) {
                            try {
                                response.AmdGraphics = AmdGraphicsControl.RestoreSaved(amdGraphics);
                                if (response.AmdGraphics != null) response.Message += " " + response.AmdGraphics.Message;
                            }
                            catch (Exception ex) { response.Success = false; response.Message += " Restauración AMD pendiente: " + ex.Message; }
                        }
                        break;
                    case "amd_graphics_status":
                    case "amd_graphics_apply":
                    case "amd_graphics_restore":
                        if (amdGraphics?.Supported != true) throw new InvalidOperationException("Actualiza el helper para consultar los ajustes AMD.");
                        AmdGraphicsStatus amdStatus;
                        if (request.Operation == "amd_graphics_status") {
                            if (request.AmdGraphics != null) throw new ArgumentException("La consulta AMD no admite nuevos valores.");
                            amdStatus = amdGraphics.Inspect();
                        }
                        else amdStatus = request.Operation == "amd_graphics_apply" ? amdGraphics.Apply(request.AmdGraphics) : amdGraphics.Restore(request.AmdGraphics);
                        response = new Response { Success = amdStatus.Available, AmdGraphics = amdStatus, Message = amdStatus.Message }; break;
                    case "remap_status":
                    case "remap_save":
                    case "remap_delete":
                    case "remap_activate":
                    case "remap_y3_test":
                    case "remap_y3_stop":
                        if (remap?.Supported != true) throw new InvalidOperationException("Actualiza el helper para editar las distribuciones del mando.");
                        var remapStatus = remap.Execute(request.Operation, request.Remap);
                        response = new Response { Success = true, Remap = remapStatus, Message = remapStatus.Message }; break;
                    case "lossless_status":
                        if (losslessScaling?.Supported != true) throw new InvalidOperationException("Actualiza el helper para consultar Lossless Scaling.");
                        if (request.Key != null || request.Values != null || request.Profile != null || request.RtssAppearance != null || request.Display != null || request.Ac)
                            throw new ArgumentException("La consulta de Lossless Scaling no admite ajustes ni rutas.");
                        var scalingStatus = losslessScaling.Inspect();
                        response = new Response { Success = true, LosslessScaling = scalingStatus, Message = scalingStatus.Message }; break;
                    case "display_status":
                    case "display_resolution":
                    case "display_refresh_rate":
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
                        else if (request.Operation == "display_resolution") displayStatus = display.ApplyResolution(request.Display);
                        else if (request.Operation == "display_refresh_rate") displayStatus = display.ApplyRefreshRate(request.Display);
                        else if (request.Operation == "display_preview") displayStatus = display.Preview(request.Display);
                        else
                        {
                            if (request.Display?.TrialToken == null || request.Display.SnapshotId != null || request.Display.Mode != null
                                || request.Display.ResolutionHeight != 0 || request.Display.RefreshRate != 0)
                                throw new ArgumentException("La decisión solo admite el identificador de su prueba de pantalla.");
                            displayStatus = display.Decide(request.Display.TrialToken, request.Operation == "display_confirm");
                        }
                        response = new Response { Success = (request.Operation != "display_resolution" && request.Operation != "display_refresh_rate") || displayStatus.Error == null,
                            Display = displayStatus, Message = displayStatus.Message }; break;
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
                    case "controller_startup_history":
                        if (!(startup is IControllerStartupHistoryControl historyControl)) throw new InvalidOperationException("Actualiza el helper para consultar el historial de inicio.");
                        if (request.Key != null || request.Values != null || request.Profile != null || request.RtssAppearance != null || request.Display != null || request.Ac)
                            throw new ArgumentException("La consulta del historial no admite cambios de configuración.");
                        response = new Response { Success = true, ControllerStartupHistory = historyControl.GetHistory(), Message = "Historial de inicio consultado; no se cambió el mando." }; break;
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
                    case "fps_keys_set":
                        var fpsKeys = shortcuts as IFpsButtonKeys;
                        if (fpsKeys == null) throw new InvalidOperationException("Actualiza el helper para usar los botones FPS.");
                        if (request.Values == null || request.Values.Length != 1 || request.Values[0] < 0 || request.Values[0] > 1
                            || request.Key != null || request.Profile != null || request.RtssAppearance != null || request.Display != null || request.Ac)
                            throw new ArgumentException("Los botones FPS solo admiten activar o desactivar Insert/Escape.");
                        var fpsControllerStatus = controller?.GetStatus();
                        if (request.Values[0] == 1 && (fpsControllerStatus?.Active == true || fpsControllerStatus?.PreparingPlayerOne == true))
                            throw new InvalidOperationException("Devuelve el mando físico antes de activar los botones FPS.");
                        fpsKeys.SetFpsKeys(request.Values[0] == 1);
                        response = new Response { Success = true, Message = request.Values[0] == 1
                            ? "Botones FPS solicitados. Suelta Y3/M3 y comprueba el estado." : "Botones FPS desactivados." }; break;
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
            response.Qam = qam?.GetStatus();
            response.SupportsOptiScalerMenu = optiScaler?.Supported == true;
            response.SupportsDisplay = display?.Supported == true;
            response.SupportsDisplayResolution = display?.Supported == true;
            response.SupportsDisplayRefreshRate = display?.Supported == true;
            response.SupportsLosslessScaling = losslessScaling?.Supported == true;
            // Ordinary polling is cache-only: no WMI, download or installer launch.
            response.PawnIo = pawnIo?.GetStatus();
            response.SupportsPawnIo = response.PawnIo?.Supported == true;
            response.SupportsAmdGraphics = amdGraphics?.Supported == true;
            response.SupportsRemapEditing = remap?.Supported == true;
            response.FpsY3Trial = remap?.TrialStatus;
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
        internal void Suspend() { try { qam?.Suspend(); } catch { } try { remap?.StopTrial(); } catch { } try { startup?.CancelPending(); } catch { } try { rtss?.Suspend(); } catch { } try { display?.CancelPending(); } catch { } }
        internal void Resume() { try { display?.Recover(); } catch { } try { qam?.Resume(); } catch { } }
        public void Dispose()
        {
            try { qam?.Dispose(); } catch { }
            try { remap?.Dispose(); } catch { }
            try { display?.Dispose(); } catch { }
            try { rtss?.Dispose(); } catch { }
            finally { Recorder.Dispose(); }
        }
    }
}
