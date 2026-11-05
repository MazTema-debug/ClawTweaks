// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using LegionTweaks.Widget;

namespace LegionTweaks.QamProbe
{
    internal sealed partial class ProbeForm
    {
        private readonly QamReadClient readClient = new QamReadClient();
        private readonly Response[] queryResponses = new Response[ShellNavigation.Sections.Length];
        private readonly TelemetryFreshness[] queryFreshness = new TelemetryFreshness[ShellNavigation.Sections.Length];
        private readonly string[] queryMessages = new string[ShellNavigation.Sections.Length];
        private readonly int[] queryScroll = new int[ShellNavigation.Sections.Length];
        private CancellationTokenSource queryCancellation;
        private int queryEpoch, querySection, readingDragY;
        private bool readingDrag;
        private long nextReadingAge;

        private QamQuery? CurrentQuery => tabs.Current == ShellSection.Power ? QamQuery.Power
            : tabs.Current == ShellSection.Display ? QamQuery.Display
            : tabs.Current == ShellSection.Games ? (QamQuery?)QamQuery.Lossless : null;
        private int FooterTop => CurrentQuery.HasValue ? ClientSize.Height - Pixels(50)
            : RowBounds(Rows.Length - 1).Bottom + Pixels(8);
        private Rectangle ReadingBounds => Rectangle.FromLTRB(Pixels(16), RowBounds(Rows.Length - 1).Bottom + Pixels(8),
            Math.Max(Pixels(16), ClientSize.Width - Pixels(16)), Math.Max(RowBounds(Rows.Length - 1).Bottom + Pixels(8), FooterTop - Pixels(8)));
        private string ReadingText => ReadingBody + (PowerPage ? "\n\n" + modeMessage : DisplayPage ? "\n\n" + displayMessage : "");
        private string ReadingBody => queryResponses[(int)tabs.Current] != null
            ? QamReadText.Render(CurrentQuery.Value, queryResponses[(int)tabs.Current], DateTime.UtcNow,
                queryFreshness[(int)tabs.Current]?.IsCurrent != true)
            : queryMessages[(int)tabs.Current] ?? "Pulsa A o toca Consultar para leer el helper.\nLos resultados incluyen la hora de captura.";
        private const TextFormatFlags ReadingFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private int ReadingHeight => TextRenderer.MeasureText(ReadingText, Font,
            new Size(Math.Max(1, ReadingBounds.Width), int.MaxValue), ReadingFlags).Height;
        private int ClampScroll(int value) => Math.Max(0, Math.Min(value, Math.Max(0, ReadingHeight - ReadingBounds.Height)));
        private void ScrollReading(int delta)
        {
            if (!CurrentQuery.HasValue || ReadingBounds.IsEmpty) return;
            queryScroll[(int)tabs.Current] = ClampScroll(queryScroll[(int)tabs.Current] + delta);
            Invalidate(ReadingBounds);
        }
        private void PaintReading(Graphics graphics)
        {
            if (!CurrentQuery.HasValue || ReadingBounds.Width <= 0 || ReadingBounds.Height <= 0) return;
            int index = (int)tabs.Current;
            queryScroll[index] = ClampScroll(queryScroll[index]);
            Rectangle bounds = ReadingBounds;
            var saved = graphics.Save();
            try {
                graphics.SetClip(bounds);
                var textBounds = new Rectangle(bounds.X, bounds.Y - queryScroll[index], bounds.Width, Math.Max(bounds.Height, ReadingHeight));
                TextRenderer.DrawText(graphics, ReadingText, Font, textBounds, Color.Gainsboro,
                    ReadingFlags | TextFormatFlags.PreserveGraphicsClipping);
            }
            finally { graphics.Restore(saved); }
        }
        private bool ReadingPointer(int message, Point point)
        {
            if (message == 0x0202 && readingDrag) { readingDrag = false; return true; }
            if (!CurrentQuery.HasValue || !Visible || !ProbeNative.InteractiveDesktop()
                || (targetProcess != 0 && !TargetIsForeground())) { readingDrag = false; return false; }
            if (message == 0x0201 && ReadingBounds.Contains(point)) {
                pressedRow = pressedTab = -1;
                readingDrag = true; readingDragY = point.Y;
                return true;
            }
            if (message == 0x0200 && readingDrag) {
                ScrollReading(readingDragY - point.Y); readingDragY = point.Y;
                return true;
            }
            return false;
        }
        private async void QueryHelper(QamQuery query, bool automatic = false)
        {
            if (queryCancellation != null) return;
            int epoch = ++queryEpoch, section = (int)tabs.Current;
            var cancellation = new CancellationTokenSource();
            queryCancellation = cancellation; querySection = section;
            if (!automatic) { queryResponses[section] = null; queryScroll[section] = 0; }
            if (query == QamQuery.Display) { refreshControls.Invalidate(); nextDisplayRead = clock.ElapsedMilliseconds + 5000; }
            queryMessages[section] = "Consultando al helper…";
            Invalidate();
            Log("read-start " + QamReadClient.Operation(query));
            try {
                Response response;
                if (query == QamQuery.Power) {
                    try {
                        var mode = await modeClient.Send(new QamModeRequest { Operation = "query" }, cancellation.Token);
                        if (epoch == queryEpoch && !stopping) {
                            modeDraft.Observe(mode); modeMessage = mode.Message;
                        }
                        response = new Response { Success = mode.Success, State = mode.State, Message = mode.Message };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        if (epoch == queryEpoch && !stopping) { modeDraft.Invalidate(); modeMessage = ex.Message; }
                        response = await readClient.Read(query, cancellation.Token);
                    }
                }
                else if (query == QamQuery.Display) {
                    try {
                        var display = await displayClient.Send(new QamDisplayRequest { Operation = "query" }, cancellation.Token);
                        if (epoch == queryEpoch && !stopping) { refreshControls.Observe(display); displayMessage = display.Message; }
                        response = DisplayReading(display);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) {
                        if (epoch == queryEpoch && !stopping) { refreshControls.Invalidate(); displayMessage = ex.Message; }
                        response = await readClient.Read(query, cancellation.Token);
                    }
                }
                else response = await readClient.Read(query, cancellation.Token);
                if (stopping || epoch != queryEpoch || section != (int)tabs.Current || !Visible) return;
                queryResponses[section] = response;
                queryFreshness[section] = new TelemetryFreshness();
                queryFreshness[section].TryAccept(QamReadText.CapturedAt(query, response), 0, out DateTime _);
                queryMessages[section] = null;
                Log("read-result " + QamReadClient.Operation(query) + " success=" + response.Success);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) {
                if (stopping || epoch != queryEpoch) return;
                queryResponses[section] = null;
                queryMessages[section] = "Lectura sin confirmar.\n" + ex.Message;
                Log("read-unavailable " + QamReadClient.Operation(query) + " " + ex.Message);
            }
            finally {
                if (ReferenceEquals(queryCancellation, cancellation)) queryCancellation = null;
                cancellation.Dispose();
                if (!stopping && !IsDisposed && epoch == queryEpoch) Invalidate();
            }
        }
        private void CancelQuery()
        {
            readingDrag = false;
            ++queryEpoch;
            if (queryCancellation == null) return;
            queryMessages[querySection] = "Consulta cancelada. Pulsa Consultar para renovarla.";
            var previous = queryCancellation;
            queryCancellation = null;
            previous.Cancel(); // The owning async continuation disposes it.
        }
        private void RefreshReadingAge(long now)
        {
            if (!CurrentQuery.HasValue || now < nextReadingAge) return;
            nextReadingAge = now + 1000;
            if (queryResponses[(int)tabs.Current] != null) { if (DisplayPage) Invalidate(); else Invalidate(ReadingBounds); }
        }
    }
}
