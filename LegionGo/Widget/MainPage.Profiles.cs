// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private ProfileCatalog catalog = new ProfileCatalog();
        private string editingId;
        private bool profileWritable, profileSaving;
        private readonly List<ComboBox> mappingEditors = new List<ComboBox>();
        private const string ProfileFile = "profiles-v2.json";
        private async Task LoadProfiles()
        {
            if (mappingEditors.Count == 0)
                for (int i = 0; i < ControllerLayout.Buttons.Length; i++)
                {
                    var box = new ComboBox { Header = ControllerLayout.Labels[i] + " →", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                    box.Items.Add("Sin función"); foreach (string label in ControllerLayout.Labels) box.Items.Add(label);
                    box.SelectedIndex = i + 1; mappingEditors.Add(box); ButtonMappings.Children.Add(box);
                }
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(ProfileFile) as StorageFile;
                if (file != null) catalog = Protocol.Decode<ProfileCatalog>(await FileIO.ReadTextAsync(file));
                else
                {
                    var legacy = ApplicationData.Current.LocalSettings.Values["LegionPowerProfiles"] as string;
                    catalog = string.IsNullOrEmpty(legacy) ? new ProfileCatalog() : ProfileCatalog.Migrate(legacy);
                    await StoreCatalog(catalog); // The old settings entry is retained as a migration backup.
                }
                catalog.Validate(); profileWritable = true; RefreshProfileList();
                if (catalog.Profiles.Count == 0) EditProfile(new ProfileDefinition { Id = Guid.NewGuid().ToString("D"), Name = "" });
            }
            catch (Exception ex) { profileWritable = false; ProfileNotice.Text = "No se pudieron leer los perfiles. Se conservó el archivo: " + ex.Message; }
            ProfileSave.IsEnabled = profileWritable;
        }
        private async Task StoreCatalog(ProfileCatalog next)
        {
            next.Validate();
            var folder = ApplicationData.Current.LocalFolder;
            var temp = await folder.CreateFileAsync(ProfileFile + ".tmp", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(temp, Protocol.Encode(next));
            var existing = await folder.TryGetItemAsync(ProfileFile) as StorageFile;
            if (existing == null) await temp.RenameAsync(ProfileFile, NameCollisionOption.FailIfExists);
            else await temp.MoveAndReplaceAsync(existing);
        }
        private void RefreshProfileList(string selected = null)
        {
            Profiles.ItemsSource = null; Profiles.ItemsSource = catalog.Profiles;
            if (catalog.Profiles.Count > 0) Profiles.SelectedItem = catalog.Profiles.FirstOrDefault(x => x.Id == selected) ?? catalog.Profiles[0];
        }
        private void ProfileSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (Profiles.SelectedItem is ProfileDefinition profile) EditProfile(profile);
        }
        private static int? BoostValue(ComboBox box) => box.SelectedIndex == 0 ? (int?)null : box.SelectedIndex == 1 ? 0 : 2;
        private static int BoostIndex(int? value) => !value.HasValue ? 0 : value == 0 ? 1 : value == 2 ? 2 : 0;
        private void EditProfile(ProfileDefinition profile)
        {
            editingId = profile.Id; ProfileName.Text = profile.Name;
            foreach (ComboBoxItem item in ProfileMode.Items) if (Convert.ToInt32(item.Tag) == (profile.Mode ?? -1)) ProfileMode.SelectedItem = item;
            var limits = profile.Limits ?? new[] { 15, 15, 15 };
            ProfileSpl.Value = limits[0]; ProfileSppt.Value = limits[1]; ProfileFppt.Value = limits[2];
            ProfileBoostAC.SelectedIndex = BoostIndex(profile.BoostAC); ProfileBoostDC.SelectedIndex = BoostIndex(profile.BoostDC);
            ProfileEppACIncluded.IsChecked = profile.EppAC.HasValue; ProfileEppDCIncluded.IsChecked = profile.EppDC.HasValue;
            ProfileEppAC.Value = profile.EppAC ?? 30; ProfileEppDC.Value = profile.EppDC ?? 30;
            ProfileWindows.SelectedIndex = (profile.WindowsMode ?? -1) + 1;
            ProfileControllerMode.SelectedIndex = profile.Controller == null ? 0 : profile.Controller.Enabled ? 2 : 1;
            var map = profile.Controller ?? new ControllerLayout();
            for (int i = 0; i < mappingEditors.Count; i++) mappingEditors[i].SelectedIndex = Array.IndexOf(ControllerLayout.Buttons, map.Map[i]) + 1;
            ProfileLeftDeadzone.Value = map.LeftDeadzone; ProfileRightDeadzone.Value = map.RightDeadzone;
            ProfileSwapSticks.IsChecked = map.SwapSticks; ProfileInvertLeft.IsChecked = map.InvertLeftY; ProfileInvertRight.IsChecked = map.InvertRightY;
            ProfileSwapTriggers.IsChecked = map.SwapTriggers; ProfileRumble.Value = map.Rumble;
            ProfileNotice.Text = "Edición local. Guarda los cambios antes de aplicar el perfil.";
        }
        private ProfileDefinition ReadProfileDraft()
        {
            int mode = Convert.ToInt32((ProfileMode.SelectedItem as ComboBoxItem)?.Tag ?? "-1");
            return new ProfileDefinition { Id = editingId ?? Guid.NewGuid().ToString("D"), Name = ProfileName.Text.Trim(),
                Mode = mode < 0 ? (int?)null : mode, Limits = mode == 255 ? new[] { (int)ProfileSpl.Value, (int)ProfileSppt.Value, (int)ProfileFppt.Value } : null,
                BoostAC = BoostValue(ProfileBoostAC), BoostDC = BoostValue(ProfileBoostDC),
                EppAC = ProfileEppACIncluded.IsChecked == true ? (int?)ProfileEppAC.Value : null,
                EppDC = ProfileEppDCIncluded.IsChecked == true ? (int?)ProfileEppDC.Value : null,
                WindowsMode = ProfileWindows.SelectedIndex == 0 ? (int?)null : ProfileWindows.SelectedIndex - 1,
                Controller = ProfileControllerMode.SelectedIndex == 0 ? null : new ControllerLayout {
                    Enabled = ProfileControllerMode.SelectedIndex == 2,
                    Map = mappingEditors.Select(x => x.SelectedIndex <= 0 ? 0 : ControllerLayout.Buttons[x.SelectedIndex - 1]).ToArray(),
                    LeftDeadzone = (int)ProfileLeftDeadzone.Value, RightDeadzone = (int)ProfileRightDeadzone.Value,
                    SwapSticks = ProfileSwapSticks.IsChecked == true, InvertLeftY = ProfileInvertLeft.IsChecked == true, InvertRightY = ProfileInvertRight.IsChecked == true,
                    SwapTriggers = ProfileSwapTriggers.IsChecked == true, Rumble = (int)ProfileRumble.Value } };
        }
        private void ProfileNew_Click(object sender, RoutedEventArgs e)
        {
            Profiles.SelectedItem = null;
            EditProfile(new ProfileDefinition { Id = Guid.NewGuid().ToString("D"), Name = "", Mode = 255, Limits = new[] { 15, 15, 15 } });
        }
        private void ProfileCapture_Click(object sender, RoutedEventArgs e)
        {
            var draft = ReadProfileDraft(); draft.Mode = Value("Mode");
            if (draft.Mode == 255)
            {
                if (!Value("Spl").HasValue || !Value("Sppt").HasValue || !Value("Fppt").HasValue) { ProfileNotice.Text = "Actualiza las lecturas antes de capturar el perfil."; return; }
                draft.Limits = new[] { Value("Spl").Value, Value("Sppt").Value, Value("Fppt").Value };
                try { ProfileDefinition.ValidateLimits(draft.Limits); } catch (Exception ex) { ProfileNotice.Text = ex.Message; return; }
            }
            else draft.Limits = null;
            draft.BoostAC = Value("CpuBoostAC"); draft.BoostDC = Value("CpuBoostDC");
            if (draft.BoostAC != 0 && draft.BoostAC != 2) draft.BoostAC = null;
            if (draft.BoostDC != 0 && draft.BoostDC != 2) draft.BoostDC = null;
            draft.EppAC = Value("EppAC"); draft.EppDC = Value("EppDC"); draft.WindowsMode = Value("OsPowerMode");
            EditProfile(draft); ProfileNotice.Text = "Lecturas copiadas al editor. Revisa y guarda; los valores no disponibles quedan Sin cambiar.";
        }
        private async void ProfileSave_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving) return;
            profileSaving = true;
            try { var next = catalog.Upsert(ReadProfileDraft()); await StoreCatalog(next); catalog = next; RefreshProfileList(editingId); ProfileNotice.Text = "Perfil guardado. Pulsa Aplicar perfil guardado para usarlo."; }
            catch (Exception ex) { ProfileNotice.Text = "No se guardó el perfil: " + ex.Message; }
            finally { profileSaving = false; }
        }
        private async void ProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving || !(Profiles.SelectedItem is ProfileDefinition selected)) return;
            var dialog = new ContentDialog { Title = "Eliminar perfil", Content = selected.Name, PrimaryButtonText = "Eliminar", CloseButtonText = "Cancelar" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            profileSaving = true;
            try { var next = Protocol.Decode<ProfileCatalog>(Protocol.Encode(catalog)); next.Profiles.RemoveAll(x => x.Id == selected.Id);
                await StoreCatalog(next); catalog = next; RefreshProfileList(); if (next.Profiles.Count == 0) ProfileNew_Click(null, null); }
            catch (Exception ex) { ProfileNotice.Text = "No se eliminó el perfil: " + ex.Message; }
            finally { profileSaving = false; }
        }
        private async void ProfileApply_Click(object sender, RoutedEventArgs e)
        {
            if (Profiles.SelectedItem is ProfileDefinition profile) await Send(new Request { Operation = "profile_apply", Profile = profile.Copy() });
            else ProfileNotice.Text = "Guarda y selecciona un perfil para aplicarlo.";
        }
        private async void ControllerStop_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "controller_stop" });
        private void RenderController(ControllerStatus value)
        {
            if (value == null) { ControllerText.Text = "Estado del mando sin confirmar. Comprueba que el helper sea 0.1.9."; ControllerInputText.Text = ControllerOutputText.Text = ""; return; }
            ControllerText.Text = value.Message + (value.Active ? "\nInformes enviados: " + value.Frames : "")
                + (value.VirtualSlot.HasValue ? "\nJugador virtual: " + (value.VirtualSlot.Value + 1) : "")
                + (value.Active && value.VirtualSlot.HasValue && value.VirtualSlot != 0 ? "\nEl virtual ocupa otro puesto. Algunos juegos solo usan el jugador 1; este dato no confirma por sí solo la causa." : "")
                + (value.Active && !value.VirtualSlot.HasValue ? "\nJugador virtual: sin confirmar." : "")
                + (string.IsNullOrEmpty(value.SlotError) ? "" : "\n" + value.SlotError)
                + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
            if (DateTime.TryParse(value.SlotCheckedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var slotTime))
                ControllerText.Text += "\nPuesto consultado: " + slotTime.ToLocalTime().ToString("HH:mm:ss");
            ControllerInputText.Text = "Entrada física: " + FrameText(value.Input);
            ControllerOutputText.Text = "Salida virtual: " + FrameText(value.Output);
        }
        private static string FrameText(GamepadFrame f)
        {
            var pressed = ControllerLayout.Labels.Where((x, i) => (f.Buttons & ControllerLayout.Buttons[i]) != 0);
            return string.Join(" + ", pressed) + "\nLT " + f.LT + " / RT " + f.RT + " · LS " + f.LX + ", " + f.LY + " · RS " + f.RX + ", " + f.RY;
        }
    }
}
