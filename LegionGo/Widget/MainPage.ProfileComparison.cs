// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private void UpdateProfileComparisonAction()
        {
            ProfileCompare.IsEnabled = !profileSaving && !profileDraftDialogOpen
                && SelectedSavedProfile() != null && catalog.Profiles.Count > 1;
        }

        private async void ProfileCompare_Click(object sender, RoutedEventArgs e)
        {
            if (profileSaving || profileDraftDialogOpen) return;
            var saved = SelectedSavedProfile();
            if (saved == null) { ProfileNotice.Text = "Selecciona un perfil guardado para comparar."; return; }
            profileSaving = true; profileDraftDialogOpen = true; UpdateProfileActions();
            try
            {
                // Snapshots remain separate from both the catalog and the editor.
                // Opening or changing this dialog never selects another editor profile.
                catalog.Validate();
                var initial = saved.Copy();
                var others = catalog.Profiles.Where(x => !string.Equals(x.Id, initial.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Copy()).ToList();
                if (others.Count == 0) { ProfileNotice.Text = "Guarda al menos dos perfiles para compararlos."; return; }
                var choice = new ComboBox { Header = "Comparar «" + initial.Name + "» con", DisplayMemberPath = "Name",
                    ItemsSource = others, HorizontalAlignment = HorizontalAlignment.Stretch };
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Se usan las versiones guardadas. Tu borrador permanecerá en el editor.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
                panel.Children.Add(choice);
                panel.Children.Add(new ScrollViewer { Content = text, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 12, 0, 0) });
                choice.SelectionChanged += (s, change) => {
                    var other = choice.SelectedItem as ProfileDefinition;
                    if (other != null) text.Text = ProfileComparison.Describe(initial, other);
                };
                choice.SelectedIndex = 0;
                await new ContentDialog { Title = "Comparar perfiles guardados", Content = panel,
                    CloseButtonText = "Volver al editor", DefaultButton = ContentDialogButton.Close }.ShowAsync();
            }
            catch (Exception ex) { ProfileNotice.Text = "No se pudieron comparar los perfiles: " + ex.Message; }
            finally { profileDraftDialogOpen = false; profileSaving = false; UpdateProfileActions(); }
        }
    }
}
