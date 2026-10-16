// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private ProfileDefinition profileDraftBaseline;
        private string profileSelectionId;
        private bool profileDraftTracking, profileDraftUpdating, profileSelectionUpdating, profileDraftDialogOpen;

        // Called after the dynamically generated mapping controls exist. Subscriptions
        // survive reloads; assigning a profile is suppressed separately from user edits.
        private void InitializeProfileDraftTracking()
        {
            if (profileDraftTracking) return;
            profileDraftTracking = true;
            ProfileName.TextChanged += (s, e) => UpdateProfileDraftNotice();
            foreach (var box in new[] { ProfileMode, ProfileBoostAC, ProfileBoostDC, ProfileWindows, ProfileControllerMode }.Concat(mappingEditors))
                box.SelectionChanged += (s, e) => UpdateProfileDraftNotice();
            foreach (var slider in new[] { ProfileSpl, ProfileSppt, ProfileFppt, ProfileEppAC, ProfileEppDC,
                ProfileLeftDeadzone, ProfileRightDeadzone, ProfileRumble })
                slider.ValueChanged += (s, e) => UpdateProfileDraftNotice();
            foreach (var check in new[] { ProfileEppACIncluded, ProfileEppDCIncluded, ProfileSwapSticks,
                ProfileInvertLeft, ProfileInvertRight, ProfileSwapTriggers })
            {
                check.Checked += (s, e) => UpdateProfileDraftNotice();
                check.Unchecked += (s, e) => UpdateProfileDraftNotice();
                check.Indeterminate += (s, e) => UpdateProfileDraftNotice();
            }
        }

        private bool HasProfileDraftChanges() => profileDraftBaseline != null
            && !ProfileDraft.Equivalent(profileDraftBaseline, ReadProfileDraft());

        private ProfileDefinition SelectedSavedProfile() => catalog.Profiles.FirstOrDefault(x => x.Id == profileSelectionId);

        private void UpdateProfileDraftNotice()
        {
            if (profileDraftUpdating || profileDraftBaseline == null) return;
            var saved = SelectedSavedProfile();
            bool dirty = HasProfileDraftChanges();
            ProfileDraftNotice.Text = dirty ? "Hay cambios sin guardar. " : "Sin cambios pendientes. ";
            ProfileDraftNotice.Text += saved == null ? "Guarda el perfil nuevo para poder aplicarlo."
                : "Aplicar usa la versión guardada de «" + saved.Name + "»" + (dirty ? ", sin los cambios del editor." : ".");
        }

        private void SetProfileSelection(ProfileDefinition profile)
        {
            profileSelectionUpdating = true;
            try { Profiles.SelectedItem = profile; }
            finally { profileSelectionUpdating = false; }
        }

        private async Task<bool> ConfirmDiscardProfileDraft(string action)
        {
            if (!HasProfileDraftChanges()) return true;
            if (profileDraftDialogOpen) return false;
            profileDraftDialogOpen = true;
            try
            {
                var dialog = new ContentDialog { Title = "Hay cambios sin guardar",
                    Content = "Para " + action + " se descartarán los cambios del editor. Los perfiles guardados se conservan.",
                    PrimaryButtonText = "Descartar y continuar", CloseButtonText = "Seguir editando",
                    DefaultButton = ContentDialogButton.Close };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            finally { profileDraftDialogOpen = false; }
        }

        private string DraftDiscardWarning() => HasProfileDraftChanges()
            ? "\n\nTambién se descartarán los cambios sin guardar del editor." : "";
    }
}
