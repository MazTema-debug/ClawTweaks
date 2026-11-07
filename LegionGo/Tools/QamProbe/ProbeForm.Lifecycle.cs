// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LegionTweaks.QamProbe
{
    internal sealed partial class ProbeForm
    {
        internal Task<QamInstanceResponse> ProcessInstanceRequest(QamInstanceRequest request, CancellationToken cancellation)
        {
            var result = new TaskCompletionSource<QamInstanceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action run = () => {
                try {
                    if (stopping || IsDisposed || cancellation.IsCancellationRequested) { result.TrySetCanceled(); return; }
                    bool success = true;
                    string message = "Instancia consultada; no se aplicaron ajustes.";
                    if (request.Operation == "open") {
                        success = ShowPanel("app de escritorio"); message = success ? "Menú rápido abierto." : "No se pudo abrir en el escritorio actual.";
                    }
                    else if (request.Operation == "toggle") {
                        TogglePanel("atajo del mando"); message = Visible ? "Menú rápido abierto." : "Menú rápido oculto.";
                    }
                    else if (request.Operation == "desktop_toggle") message = DesktopWindowControl.Toggle(request);
                    result.TrySetResult(new QamInstanceResponse { Success = success, Running = true, Visible = Visible,
                        ProcessId = Process.GetCurrentProcess().Id, SessionId = Process.GetCurrentProcess().SessionId, Message = message });
                }
                catch (Exception ex) { result.TrySetResult(new QamInstanceResponse { Running = true, ProcessId = Process.GetCurrentProcess().Id,
                    SessionId = Process.GetCurrentProcess().SessionId, Message = ex.Message }); }
            };
            try { if (InvokeRequired) BeginInvoke(run); else run(); }
            catch (InvalidOperationException) { result.TrySetCanceled(); }
            return result.Task;
        }
    }
}
