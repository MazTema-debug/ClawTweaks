// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private bool controllerPreparing, playerOneDialogOpen;
        private async void ControllerRefresh_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "recording_status" });
        private async void ControllerPlayerOne_Click(object sender, RoutedEventArgs e) => await StartPlayerOne(true);
        private async void ControllerPlayerOneFps_Click(object sender, RoutedEventArgs e) => await StartPlayerOne(false);
        private async Task StartPlayerOne(bool automatic)
        {
            if (working || controllerPreparing || playerOneDialogOpen || profileSaving) return;
            if (automatic && lastControllerStatus?.SupportsModePlayerOnePreparation != true)
            { StatusText.Text = "Actualiza el helper a 0.1.22 y consulta su estado antes de preparar automáticamente el jugador 1."; return; }
            var selected = Profiles.SelectedItem as ProfileDefinition;
            if (selected?.Controller?.Enabled != true)
            { StatusText.Text = "Selecciona en Perfiles un perfil guardado que active el mando virtual."; return; }
            var saved = selected.Copy(); // Draft edits and power values are not applied by this operation.
            if (!CanUseLegionMappings(saved.Controller))
            { StatusText.Text = "Actualiza o reconecta el helper antes de activar las asignaciones Legion de este perfil."; return; }
            playerOneDialogOpen = true;
            try
            {
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap,
                    Text = "Se usará la distribución guardada de «" + saved.Name + "», sin aplicar ajustes de energía.\n\n"
                        + (automatic ? "Cierra los juegos. Steam puede permanecer abierto con el helper 0.1.24. Empieza con FPS en OFF. "
                            : "Cierra los juegos y sal de Steam. Empieza con FPS en OFF. ")
                        + (automatic ? "Si nuestro virtual ya está en jugador 1, se conservará su conexión.\n\n" : "Se desconectará nuestro virtual actual.\n\n")
                        + (automatic
                            ? "Si el físico ocupa el puesto 1, se cambiará brevemente su modo para conectar el virtual en ese puesto y después se recuperará su modo original. No muevas el interruptor FPS.\n\nLa preparación tiene un límite de 30 segundos. La aplicación comprobará el puesto y recuperará el físico si no puede completar la conexión."
                            : "La pestaña Mando te pedirá activar FPS y después volver a OFF si hace falta liberar el puesto 1. Tienes 90 segundos; puedes cancelar.\n\nAl cancelar o terminar, deja siempre el interruptor en OFF.") };
                var content = new StackPanel { Spacing = 12 }; content.Children.Add(text);
                var dialog = new ContentDialog { Title = automatic ? "Jugador 1 con reconexión automática" : "Jugador 1 con interruptor FPS", PrimaryButtonText = "Iniciar", CloseButtonText = "Cancelar",
                    DefaultButton = ContentDialogButton.Close, Content = new ScrollViewer { Content = content, MaxHeight = 350,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await Send(new Request { Operation = automatic ? "controller_player_one_mode" : "controller_player_one", Profile = saved });
            }
            catch (Exception ex) { StatusText.Text = "No se inició la preparación: " + ex.Message; }
            finally { playerOneDialogOpen = false; }
        }
    }
}
