// SPDX-License-Identifier: AGPL-3.0-only
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private async void ProfileDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving || profileDraftDialogOpen) return;
            var saved = SelectedSavedProfile();
            if (saved == null) { ProfileNotice.Text = "Selecciona un perfil guardado para duplicarlo."; return; }
            profileSaving = true; UpdateProfileActions();
            try
            {
                if (!await ConfirmDiscardProfileDraft("duplicar la versión guardada de «" + saved.Name + "»")) return;
                var copy = ProfileEditing.DuplicateSaved(catalog, saved.Id);
                SetProfileSelection(null);
                EditProfile(copy);
                // This has no saved identity yet. Compare with the original so the
                // unique copy name represents a pending edit rather than a save.
                profileDraftBaseline = saved.Copy();
                UpdateProfileDraftNotice();
                ProfileNotice.Text = "Copia de la versión guardada en el editor. Revisa y pulsa Guardar cambios del perfil; todavía no se guardó ni se aplicó.";
            }
            catch (Exception ex) { ProfileNotice.Text = "No se duplicó el perfil: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }

        private async void ProfileControllerReset_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving || profileDraftDialogOpen) return;
            profileSaving = true; UpdateProfileActions();
            try
            {
                var dialog = new ContentDialog { Title = "Restablecer ajustes del mando",
                    Content = "El borrador volverá a los botones, zonas muertas, inversiones, intercambios y vibración iniciales. Se conservarán el nombre, los ajustes de potencia y la selección Conservar / Físico / Virtual.",
                    PrimaryButtonText = "Restablecer borrador", CloseButtonText = "Seguir editando", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                EditProfile(ProfileEditing.ResetController(ReadProfileDraft()), resetBaseline: false);
                ProfileNotice.Text = "Ajustes del mando restablecidos solo en el borrador. Revisa y guarda para conservarlos; el mando activo no cambió.";
            }
            catch (Exception ex) { ProfileNotice.Text = "No se restableció el borrador: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
    }
}
