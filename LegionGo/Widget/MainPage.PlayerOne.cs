// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool controllerPreparing, playerOneDialogOpen;
        private async void ControllerRefresh_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "recording_status" });
        private async void ControllerPlayerOne_Click(object sender, RoutedEventArgs e)
        {
            if (working || controllerPreparing || playerOneDialogOpen || profileSaving) return;
            var selected = Profiles.SelectedItem as ProfileDefinition;
            if (selected?.Controller?.Enabled != true)
            { StatusText.Text = "Selecciona en Perfiles un perfil guardado que active el mando virtual."; return; }
            var saved = selected.Copy(); // Draft edits and power values are not applied by this operation.
            playerOneDialogOpen = true;
            try
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = "Se usará la distribución guardada de «" + saved.Name + "», sin aplicar ajustes de energía.\n\n"
                        + "Cierra los juegos y sal de Steam. Empieza con FPS en OFF. Se desconectará nuestro virtual actual.\n\n"
                        + "Si hace falta liberar el puesto 1, la pestaña Mando te pedirá activar FPS y después volver a OFF. Tienes 90 segundos; puedes cancelar.\n\n"
                        + "Al cancelar o terminar, deja siempre el interruptor en OFF." };
                var dialog = new ContentDialog { Title = "Activar virtual como jugador 1", PrimaryButtonText = "Iniciar asistente", CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close, Content = new ScrollViewer { Content = text, MaxHeight = 280,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await Send(new Request { Operation = "controller_player_one", Profile = saved });
            }
            catch (Exception ex) { StatusText.Text = "No se inició la preparación: " + ex.Message; }
            finally { playerOneDialogOpen = false; }
        }
    }
}
