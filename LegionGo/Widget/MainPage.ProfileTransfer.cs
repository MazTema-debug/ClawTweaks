// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private async void ProfileExport_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving) return;
            profileSaving = true; UpdateProfileActions();
            try
            {
                string json = ProfileTransfer.Export(catalog);
                var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "LegionTweaks-perfiles-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
                picker.FileTypeChoices.Add("Perfiles de LegionTweaks", new List<string> { ".json" });
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                CachedFileManager.DeferUpdates(file);
                await FileIO.WriteTextAsync(file, json);
                var result = await CachedFileManager.CompleteUpdatesAsync(file);
                if (result != FileUpdateStatus.Complete && result != FileUpdateStatus.CompleteAndRenamed)
                    throw new InvalidOperationException("El proveedor de archivos no confirmó la exportación.");
                ProfileNotice.Text = "Perfiles guardados en " + file.Name + ". No se aplicaron ajustes.";
            }
            catch (Exception ex) { ProfileNotice.Text = "No se pudo exportar: " + ex.Message + " Si el selector no aparece en Game Bar, abre LegionTweaks desde Inicio."; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private async void ProfileImport_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving) return;
            bool committed = false;
            profileSaving = true; UpdateProfileActions();
            try
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                if ((await file.GetBasicPropertiesAsync()).Size > ProfileTransfer.MaxBytes)
                    throw new InvalidOperationException("El archivo supera 64 KiB.");
                var imported = ProfileTransfer.Parse(await FileIO.ReadTextAsync(file));
                if (imported.Profiles.Count == 0) { ProfileNotice.Text = "El archivo no contiene perfiles. No se cambió el catálogo."; return; }
                var next = ProfileTransfer.MergeCopies(catalog, imported);
                var dialog = new ContentDialog { Title = "Importar " + imported.Profiles.Count + " perfiles como copias",
                    Content = "Se conservarán los " + catalog.Profiles.Count + " perfiles actuales y se guardará un respaldo antes de importar. Los nombres repetidos recibirán un sufijo.\n\n"
                        + string.Join("\n", next.Profiles.Skip(catalog.Profiles.Count).Select(x => x.Name)) + "\n\nNo se aplicará ningún ajuste al equipo.",
                    PrimaryButtonText = "Importar copias", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                string backup = await StoreCatalog(next, backupBeforeImport: true);
                committed = true;
                string firstImported = next.Profiles[catalog.Profiles.Count].Id;
                catalog = next; RefreshProfileList(firstImported);
                ProfileNotice.Text = "Importación completada. Respaldo local: " + backup + ". Aplica un perfil solo cuando lo decidas.";
            }
            catch (Exception ex) { ProfileNotice.Text = (committed ? "La importación se guardó, pero no se pudo actualizar el editor: " : "No se importaron perfiles: ") + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private async void ProfileReload_Click(object sender, RoutedEventArgs e)
        {
            if (profileSaving) return;
            profileSaving = true; UpdateProfileActions();
            try
            {
                var dialog = new ContentDialog { Title = "Recargar perfiles", Content = "Se descartarán los cambios del editor que todavía no has guardado.",
                    PrimaryButtonText = "Recargar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await LoadProfiles();
            }
            catch (Exception ex) { ProfileNotice.Text = "No se recargaron los perfiles: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private async void ProfileNameEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving) return;
            profileSaving = true; UpdateProfileActions();
            try
            {
                var text = new TextBox { Text = ProfileName.Text, Header = "Nombre del perfil", MaxLength = 40,
                    IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled };
                var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
                // A panel rather than an OS keyboard: touch/gamepad buttons edit the draft directly.
                var keys = new GridView { ItemsPanel = MakeKeyboardPanel(), IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.None,
                    HorizontalAlignment = HorizontalAlignment.Stretch, IsSwipeEnabled = false };
                var keyContainer = new Style { TargetType = typeof(GridViewItem) };
                keyContainer.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
                keyContainer.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
                keyContainer.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
                keyContainer.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
                keys.ItemContainerStyle = keyContainer;
                var letters = new List<Button>();
                bool upper = false;
                Action<ProfileNameEdit> apply = edit => { text.Text = edit.Text; text.SelectionStart = edit.Caret; text.SelectionLength = 0; };
                foreach (char character in "1234567890qwertyuiopasdfghjklñzxcvbnm._-áéíóúü()")
                {
                    string value = character.ToString();
                    var button = new Button { Content = value, Tag = value, MinWidth = 28, Width = 30, Height = 34, Padding = new Thickness(0), Margin = new Thickness(1) };
                    AutomationProperties.SetName(button, "Escribir " + value);
                    button.Click += (s, args) => apply(ProfileNameEdit.Insert(text.Text, text.SelectionStart, text.SelectionLength, upper ? value.ToUpperInvariant() : value));
                    letters.Add(button); keys.Items.Add(button);
                }
                var caps = new CheckBox { Content = "Mayúsculas" };
                RoutedEventHandler changeCase = (s, args) => { upper = caps.IsChecked == true; foreach (var button in letters) button.Content = upper ? ((string)button.Tag).ToUpperInvariant() : (string)button.Tag; };
                caps.Checked += changeCase; caps.Unchecked += changeCase;
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var space = new Button { Content = "Espacio", Margin = new Thickness(1) };
                space.Click += (s, args) => apply(ProfileNameEdit.Insert(text.Text, text.SelectionStart, text.SelectionLength, " "));
                var erase = new Button { Content = "Borrar", Margin = new Thickness(1) };
                erase.Click += (s, args) => apply(ProfileNameEdit.Backspace(text.Text, text.SelectionStart, text.SelectionLength));
                var clear = new Button { Content = "Vaciar", Margin = new Thickness(1) };
                clear.Click += (s, args) => apply(ProfileNameEdit.Insert(text.Text, 0, text.Text.Length, ""));
                actions.Children.Add(space); actions.Children.Add(erase); actions.Children.Add(clear);
                var panel = new StackPanel();
                panel.Children.Add(text); panel.Children.Add(notice); panel.Children.Add(caps); panel.Children.Add(keys); panel.Children.Add(actions);
                var dialog = new ContentDialog { Title = "Editar nombre", Content = new ScrollViewer { Content = panel,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    PrimaryButtonText = "Usar nombre", CloseButtonText = "Cancelar" };
                Action validate = () => {
                    string name = text.Text.Trim();
                    bool duplicate = catalog.Profiles.Any(x => x.Id != editingId && string.Equals(x.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                    dialog.IsPrimaryButtonEnabled = name.Length > 0 && name.Length <= 40 && !name.Any(char.IsControl) && !duplicate;
                    notice.Text = duplicate ? "Ya existe un perfil con ese nombre." : text.Text.Length + " / 40 · Después pulsa Guardar cambios del perfil.";
                };
                text.TextChanged += (s, args) => validate(); validate();
                text.SelectionStart = text.Text.Length;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) ProfileName.Text = text.Text.Trim();
            }
            catch (Exception ex) { ProfileNotice.Text = "No se pudo editar el nombre: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private static ItemsPanelTemplate MakeKeyboardPanel()
        {
            return (ItemsPanelTemplate)Windows.UI.Xaml.Markup.XamlReader.Load(
                "<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' MaximumRowsOrColumns='10'/></ItemsPanelTemplate>");
        }
    }
}
