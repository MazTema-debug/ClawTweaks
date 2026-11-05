// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading;
using LegionTweaks.Widget;

namespace LegionTweaks.QamProbe
{
    internal sealed partial class ProbeForm
    {
        private readonly QamDisplayClient displayClient = new QamDisplayClient();
        private readonly QamRefreshControls refreshControls = new QamRefreshControls();
        private string displayMessage = "La frecuencia requiere el helper actualizado y el QAM instalado.";
        private long nextDisplayRead;
        private bool DisplayPage => tabs.Current == ShellSection.Display;
        private bool ControlPage => PowerPage || DisplayPage;
        private string DisplayRowLabel(int row)
        {
            if (row == 0) return Rows[row];
            int frequency = row == 1 ? 60 : 144;
            return refreshControls.Busy ? frequency + " Hz · aplicando…"
                : refreshControls.Current == frequency ? frequency + " Hz · actual"
                : "Aplicar " + frequency + " Hz" + (refreshControls.CanApply(frequency) ? "" : " · no disponible");
        }
        private void RefreshDisplay(bool force = false)
        {
            if (!Visible || !DisplayPage || stopping || refreshControls.Busy || queryCancellation != null) return;
            if (!force && clock.ElapsedMilliseconds < nextDisplayRead) return;
            nextDisplayRead = clock.ElapsedMilliseconds + 5000;
            QueryHelper(QamQuery.Display, true);
        }
        private void InvokeDisplayRow(int row)
        {
            if (refreshControls.Busy || queryCancellation != null) return;
            if (row == 0) QueryHelper(QamQuery.Display);
            else ApplyRefreshRate(row == 1 ? 60 : 144);
        }
        private async void ApplyRefreshRate(int frequency)
        {
            var request = refreshControls.BeginApply(frequency);
            if (request == null) {
                displayMessage = refreshControls.Current == frequency ? "Esa frecuencia ya está activa."
                    : "Frecuencia no disponible. La próxima consulta comprobará la conexión y los modos de Windows.";
                Invalidate(); return;
            }
            displayMessage = "Comprobando la pantalla actual…";
            int applyEpoch = inputEpoch, row = SelectedRow;
            Invalidate();
            QamDisplayResponse result = null;
            try {
                var fresh = await displayClient.Send(new QamDisplayRequest { Operation = "query" }, CancellationToken.None);
                if (stopping || IsDisposed || !Visible || !DisplayPage || SelectedRow != row || inputEpoch != applyEpoch || !TargetIsForeground())
                    result = new QamDisplayResponse { Message = "Cambio cancelado antes de enviarlo." };
                else if (!fresh.Success || !fresh.CanSetRefreshRate || !(frequency == 60 ? fresh.Can60 : fresh.Can144)) result = fresh;
                else if (fresh.Info?.Current?.Frequency == frequency) {
                    result = fresh; result.Message = "La frecuencia elegida ya está activa; lectura verificada.";
                }
                else {
                    Log("refresh-apply " + frequency + " id=" + request.RequestId);
                    result = await displayClient.Send(request, CancellationToken.None);
                }
                displayMessage = result.Message;
                Log("refresh-result success=" + result.Success + " hz=" + result.Info?.Current?.Frequency);
            }
            catch (Exception ex) {
                displayMessage = ex.Message;
                Log("refresh-unconfirmed " + ex.Message);
            }
            finally {
                refreshControls.Complete(result);
                int display = (int)ShellSection.Display;
                queryResponses[display] = result?.Info == null ? null : DisplayReading(result);
                queryFreshness[display] = new TelemetryFreshness();
                queryFreshness[display].TryAccept(result?.Info?.CapturedAtUtc, 0, out DateTime _);
                queryMessages[display] = result?.Info == null ? "Pantalla sin confirmar. Se renovará la lectura." : null;
                queryScroll[display] = 0;
                nextDisplayRead = clock.ElapsedMilliseconds + 5000;
                if (!stopping && !IsDisposed) {
                    if (Visible) PlaceOn(ProbeNative.MonitorBounds(target));
                    Invalidate();
                }
            }
        }
        private static Response DisplayReading(QamDisplayResponse result) => new Response {
            Success = result.Success, Display = new DisplayStatus { Supported = result.Supported, Info = result.Info }, Message = result.Message };
    }
}
