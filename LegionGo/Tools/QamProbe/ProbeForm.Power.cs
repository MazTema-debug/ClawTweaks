// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading;
using LegionTweaks.Widget;

namespace LegionTweaks.QamProbe
{
    internal sealed partial class ProbeForm
    {
        private readonly QamModeClient modeClient = new QamModeClient();
        private readonly QamModeDraft modeDraft = new QamModeDraft();
        private string modeMessage = "Consulta la potencia para comprobar el modo y la conexión de cambios.";
        private bool PowerPage => tabs.Current == ShellSection.Power;
        private string PowerRowLabel(int row) => row == 1 ? "‹ " + (modeDraft.Selected.HasValue ? TelemetryValue.ModeName(modeDraft.Selected) : "Elegir modo") + " ›"
            : row == 2 && modeDraft.Busy ? "Aplicando…" : Rows[row];
        private void ChooseMode(int direction)
        {
            if (modeDraft.Busy) return;
            modeDraft.Move(direction);
            Interlocked.Increment(ref inputEpoch);
            modeMessage = "Selección: " + TelemetryValue.ModeName(modeDraft.Selected) + ". Pulsa Aplicar modo para cambiarla.";
            Invalidate();
            Log("mode-draft " + modeDraft.Selected);
        }
        private void InvokePowerRow(int row)
        {
            if (modeDraft.Busy || queryCancellation != null) return;
            if (row == 0) QueryHelper(QamQuery.Power);
            else if (row == 1) ChooseMode(1);
            else if (row == 2) ApplyPowerMode();
        }
        private async void ApplyPowerMode()
        {
            var request = modeDraft.BeginApply();
            if (request == null) {
                modeMessage = modeDraft.Available ? "Elige otro modo antes de aplicar." : "Consulta la potencia. El cambio necesita el helper actualizado y el QAM instalado.";
                Invalidate(); return;
            }
            modeMessage = "Comprobando el modo actual…";
            int applyEpoch = inputEpoch;
            Invalidate();
            QamModeResponse result = null;
            try {
                // This refresh belongs to an explicit Apply, not to selecting or opening the page.
                var fresh = await modeClient.Send(new QamModeRequest { Operation = "query" }, CancellationToken.None);
                if (stopping || IsDisposed || !Visible || !PowerPage || SelectedRow != 2 || inputEpoch != applyEpoch || !TargetIsForeground()) {
                    result = new QamModeResponse { Message = "Aplicación cancelada antes de enviar el cambio." };
                }
                else if (!fresh.Success || !fresh.CanApply) result = fresh;
                else if (QamModeService.CurrentMode(fresh.State) == request.Mode) {
                    result = fresh; result.Message = "El modo elegido ya está activo; lectura verificada.";
                }
                else {
                    Log("mode-apply " + request.Mode + " id=" + request.RequestId);
                    result = await modeClient.Send(request, CancellationToken.None);
                }
                modeMessage = result.Message;
                Log("mode-result success=" + result.Success + " mode=" + QamModeService.CurrentMode(result.State));
            }
            catch (Exception ex) {
                modeMessage = ex.Message;
                Log("mode-unconfirmed " + ex.Message);
            }
            finally {
                modeDraft.Complete(result);
                int power = (int)ShellSection.Power;
                queryResponses[power] = result?.State == null ? null : new Response { Success = result.Success, State = result.State, Message = result.Message };
                queryFreshness[power] = new TelemetryFreshness();
                queryFreshness[power].TryAccept(result?.State?.CapturedAtUtc, 0, out DateTime _);
                queryMessages[power] = result == null ? "Modo sin confirmar. Vuelve a consultar." : null;
                queryScroll[power] = 0;
                if (!stopping && !IsDisposed) Invalidate();
            }
        }
    }
}
