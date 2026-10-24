// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Globalization;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private RtssOverlayStatus rtssStatus;

        private async void RtssEnable_Click(object sender, RoutedEventArgs e)
        {
            if (working || rtssAppearanceSaving || rtssAppearanceDialogOpen || rtssAppearanceLoadError != null
                || rtssStatus?.Supports != true || rtssStatus.Enabled || rtssStatus.CleanupPending) return;
            rtssAppearanceDialogOpen = true;
            try {
                if (HasRtssDraftChanges()) {
                    var dialog = new ContentDialog { Title = "Hay cambios de RTSS sin guardar",
                        Content = "Borrador: " + ReadRtssDraft().Describe() + "\nGuardada: " + rtssSavedAppearance.Describe()
                            + "\n\nElige qué apariencia quieres publicar.",
                        PrimaryButtonText = "Guardar y activar", SecondaryButtonText = "Usar guardada", CloseButtonText = "Cancelar",
                        DefaultButton = ContentDialogButton.Close };
                    var choice = await dialog.ShowAsync();
                    if (choice == ContentDialogResult.None) return;
                    if (choice == ContentDialogResult.Primary && !await SaveRtssAppearance()) return;
                }
                if (working || rtssStatus?.Enabled == true || rtssStatus?.CleanupPending == true) return;
                await Send(new Request { Operation = "rtss_set", Values = new[] { 1 },
                    RtssAppearance = rtssStatus.SupportsAppearance ? rtssSavedAppearance.Copy() : null });
                UpdateRtssDraft();
            }
            catch (Exception ex) { RtssAppearanceText.Text = "No se activó el overlay: " + ex.Message; }
            finally { rtssAppearanceDialogOpen = false; }
        }

        private async void RtssDisable_Click(object sender, RoutedEventArgs e)
        {
            if (working || rtssStatus?.Supports != true || (!rtssStatus.Enabled && !rtssStatus.CleanupPending)) return;
            await Send(new Request { Operation = "rtss_set", Values = new[] { 0 } });
        }

        private void RenderRtss(RtssOverlayStatus value)
        {
            rtssStatus = value;
            bool supported = value?.Supports == true;
            RtssSaveAppearance.IsEnabled = value?.SupportsAppearance == true;
            RtssEnable.IsEnabled = supported && !value.Enabled && !value.CleanupPending;
            RtssDisable.IsEnabled = supported && (value.Enabled || value.CleanupPending);
            if (rtssAppearanceReady) UpdateRtssDraft();
            if (!supported)
            {
                RtssText.Text = "Overlay RTSS sin confirmar. Comprueba la conexión y actualiza el helper.";
                return;
            }

            string description;
            switch (value.State)
            {
                case "published": description = "Publicando datos en RTSS. Esto no confirma que el overlay sea visible en el juego."; break;
                case "missing": description = "No se detectó la memoria compartida de RTSS. Abre RTSS por separado para usar este overlay."; break;
                case "busy": description = "La memoria de RTSS está ocupada. No se confirmó esta actualización."; break;
                case "full": description = "RTSS no tiene espacios libres. Los overlays de otras aplicaciones se conservan."; break;
                case "incompatible": description = "La memoria compartida de RTSS no es compatible. No se escriben datos."; break;
                case "stale": description = value.Publishing
                    ? "Se envió N/D a RTSS por telemetría vencida o no válida, sin etiquetas de FPS y frametime. La visibilidad en el juego no está verificada."
                    : "La última telemetría está vencida o no es válida; aún no se confirmó la retirada del contenido anterior."; break;
                case "suspended": description = "Overlay RTSS detenido por suspensión. Actívalo de nuevo manualmente cuando quieras continuar."; break;
                case "released": description = "Overlay RTSS desactivado; espacio liberado."; break;
                case "off": description = "Overlay RTSS desactivado. La activación es manual."; break;
                case "error": description = "No se confirmó la actualización del overlay RTSS."; break;
                default: description = value.Enabled ? "Overlay RTSS activado; publicación sin confirmar." : "Overlay RTSS desactivado."; break;
            }
            if (value.Publishing && value.State != "published" && value.State != "stale") description += "\nSe enviaron datos a RTSS; la visibilidad en el juego no está verificada.";
            if (value.CleanupPending) description += "\nLimpieza pendiente de nuestro overlay. Pulsa Desactivar para reintentar.";
            if (!string.IsNullOrEmpty(value.Error)) description += "\n" + RtssStatusText(value.Error);
            DateTime sample, published;
            if (DateTime.TryParse(value.LastSampleAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out sample))
                description += "\nÚltima muestra: " + sample.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            if (DateTime.TryParse(value.LastPublishedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out published))
                description += "\nÚltimo envío: " + published.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                    + " · Actualizaciones: " + Math.Max(0, value.Updates).ToString(CultureInfo.CurrentCulture);
            RtssText.Text = description;
        }

        private static string RtssStatusText(string value)
        {
            // Status is ordinary UI text, never interpolated into RTSS markup.
            return value.Length > 512 ? value.Substring(0, 512) : value;
        }
    }
}
