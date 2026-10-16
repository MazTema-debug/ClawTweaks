// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Reflection;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.UI.Xaml;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private DiagnosticReport diagnosticReport;
        private bool exportingDiagnostic;
        private static string DiagnosticWidgetVersion
        {
            get { var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision; }
        }
        private static string DiagnosticWidgetAssemblyVersion => typeof(MainPage).GetTypeInfo().Assembly.GetName().Version?.ToString();
        private void RenderDiagnostics(DiagnosticReport value)
        {
            try { diagnosticReport = value == null ? null : Diagnostics.SafeCopy(value, DiagnosticWidgetVersion, DiagnosticWidgetAssemblyVersion); DiagnosticText.Text = Diagnostics.Describe(diagnosticReport); }
            catch { diagnosticReport = null; DiagnosticText.Text = "No se recibió un diagnóstico compatible. Actualiza el helper y vuelve a consultar."; }
        }
        private async void DiagnosticRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (working || exportingDiagnostic) return;
            RenderDiagnostics(null);
            await Send(new Request { Operation = "diagnostics" });
            if (diagnosticReport == null) DiagnosticText.Text = "No se pudo obtener la captura. Comprueba la conexión y la versión del helper.";
        }
        private void DiagnosticCopy_Click(object sender, RoutedEventArgs e)
        {
            if (diagnosticReport == null) { DiagnosticText.Text = "Actualiza el diagnóstico antes de copiarlo."; return; }
            try
            {
                var data = new DataPackage(); data.SetText(Diagnostics.Export(diagnosticReport, DiagnosticWidgetVersion)); Clipboard.SetContent(data);
                StatusText.Text = "Diagnóstico copiado. Puedes compartirlo manualmente.";
            }
            catch { StatusText.Text = "No se pudo copiar el diagnóstico al portapapeles."; }
        }
        private async void DiagnosticExport_Click(object sender, RoutedEventArgs e)
        {
            if (exportingDiagnostic) return;
            if (diagnosticReport == null) { DiagnosticText.Text = "Actualiza el diagnóstico antes de exportarlo."; return; }
            exportingDiagnostic = true;
            try
            {
                // Capture this bounded DTO before the picker; a later refresh cannot
                // alter the snapshot selected for export. No automatic upload occurs.
                string json = Diagnostics.Export(diagnosticReport, DiagnosticWidgetVersion);
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "LegionTweaks-diagnostico-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
                picker.FileTypeChoices.Add("Diagnóstico de LegionTweaks", new List<string> { ".json" });
                var file = await picker.PickSaveFileAsync(); if (file == null) return;
                CachedFileManager.DeferUpdates(file); await FileIO.WriteTextAsync(file, json);
                var result = await CachedFileManager.CompleteUpdatesAsync(file);
                if (result != FileUpdateStatus.Complete && result != FileUpdateStatus.CompleteAndRenamed)
                    throw new InvalidOperationException();
                StatusText.Text = "Diagnóstico guardado. Puedes compartir el archivo manualmente.";
            }
            catch { StatusText.Text = "No se pudo exportar el diagnóstico. Si el selector no aparece en Game Bar, abre LegionTweaks desde Inicio."; }
            finally { exportingDiagnostic = false; }
        }
    }
}
