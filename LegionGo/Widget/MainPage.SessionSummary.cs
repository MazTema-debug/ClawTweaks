// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool sessionSummaryReading, sessionSummaryUserCancelled;
        private int sessionSummaryGeneration;
        private CancellationTokenSource sessionSummaryCancellation;

        private async void SessionSummaryOpen_Click(object sender, RoutedEventArgs e)
        {
            if (sessionSummaryReading) return;
            sessionSummaryReading = true;
            int generation = sessionSummaryGeneration;
            SessionSummaryOpen.IsEnabled = false;
            SessionSummaryCancel.IsEnabled = false;
            CancellationTokenSource cancellation = null;
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
                picker.FileTypeFilter.Add(".csv");
                var file = await picker.PickSingleFileAsync();
                if (!pageLoaded || generation != sessionSummaryGeneration) return;
                if (file == null) return;
                sessionSummaryUserCancelled = false;
                cancellation = new CancellationTokenSource();
                sessionSummaryCancellation = cancellation;
                cancellation.CancelAfter(TimeSpan.FromSeconds(SessionSummary.MaximumSeconds));
                SessionSummaryCancel.IsEnabled = true;
                SessionSummaryText.Text = "Leyendo el CSV seleccionado… El análisis se hace localmente.";
                var token = cancellation.Token;
                var report = await Task.Run(async () =>
                {
                    var properties = await file.GetBasicPropertiesAsync().AsTask(token);
                    if (properties.Size > (ulong)SessionSummary.MaximumBytes) throw new InvalidDataException("El CSV supera 64 MiB.");
                    token.ThrowIfCancellationRequested();
                    using (var input = await file.OpenSequentialReadAsync().AsTask(token))
                    using (var stream = input.AsStreamForRead())
                    using (token.Register(() => { try { stream.Dispose(); } catch { } }))
                    using (var bounded = new SessionSummaryReadStream(stream))
                    using (var reader = new StreamReader(bounded, new UTF8Encoding(false, true), false, 4096))
                        return SessionSummary.Read(reader, token);
                }, token);
                if (!pageLoaded || generation != sessionSummaryGeneration) return;
                token.ThrowIfCancellationRequested();
                SessionSummaryText.Text = file.Name + "\n\n" + SessionSummary.Describe(report);
            }
            catch (Exception ex)
            {
                if (!pageLoaded || generation != sessionSummaryGeneration) return;
                if (cancellation?.IsCancellationRequested == true)
                    SessionSummaryText.Text = sessionSummaryUserCancelled ? "Resumen cancelado." : "El análisis superó 30 segundos. Usa una sesión más corta o un archivo local.";
                else if (ex is InvalidDataException) SessionSummaryText.Text = "No se generó el resumen: " + ex.Message;
                else if (ex is DecoderFallbackException) SessionSummaryText.Text = "No se generó el resumen: el CSV debe contener texto UTF-8 válido.";
                else SessionSummaryText.Text = "No se pudo leer el CSV. Si el selector no aparece en Game Bar, abre LegionTweaks desde Inicio.";
            }
            finally
            {
                if (ReferenceEquals(sessionSummaryCancellation, cancellation)) sessionSummaryCancellation = null;
                cancellation?.Dispose();
                sessionSummaryReading = false;
                if (pageLoaded)
                {
                    SessionSummaryOpen.IsEnabled = true;
                    SessionSummaryCancel.IsEnabled = false;
                }
            }
        }

        private void SessionSummaryCancel_Click(object sender, RoutedEventArgs e)
        {
            sessionSummaryUserCancelled = true;
            try { sessionSummaryCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Called by the page/window lifecycle. An old picker or task may finish
        // after navigation, but its result cannot replace the current screen.
        private void CancelSessionSummary()
        {
            sessionSummaryGeneration++;
            try { sessionSummaryCancellation?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // Check the stream again after the property lookup: a recording can grow
        // while it is being selected. Never copy the entire selected file.
        private sealed class SessionSummaryReadStream : Stream
        {
            private readonly Stream input;
            private long read;
            internal SessionSummaryReadStream(Stream input) { this.input = input; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                if (count == 0) return 0;
                if (read > SessionSummary.MaximumBytes) throw new InvalidDataException("El CSV supera 64 MiB.");
                int boundedCount = (int)Math.Min(count, SessionSummary.MaximumBytes - read + 1);
                int actual = input.Read(buffer, offset, boundedCount);
                read += actual;
                if (read > SessionSummary.MaximumBytes) throw new InvalidDataException("El CSV supera 64 MiB.");
                return actual;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            // The outer using owns the stream (and the cancellation callback may
            // close it early); disposing this counting adapter need not close it again.
            protected override void Dispose(bool disposing) { base.Dispose(disposing); }
        }
    }
}
