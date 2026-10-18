// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        private string catalogRevision;
        private ControllerStatus lastControllerStatus;
        private static readonly SemaphoreSlim ProfileStoreGate = new SemaphoreSlim(1, 1);
        private readonly List<ComboBox> mappingEditors = new List<ComboBox>();
        private const string ProfileFile = "profiles-v2.json";
        private async Task LoadProfiles(bool reload = false)
        {
            // Reattaching the page must not replace an editor draft. An explicit
            // reload is guarded by the discard dialog in ProfileReload_Click.
            if (!reload && profileDraftBaseline != null) return;
            if (mappingEditors.Count == 0)
                for (int i = 0; i < ControllerLayout.Buttons.Length; i++)
                {
                    var box = new ComboBox { Header = ControllerLayout.Labels[i] + " →", Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
                    box.Items.Add("Sin función"); foreach (string label in ControllerLayout.Labels) box.Items.Add(label);
                    box.SelectedIndex = i + 1; mappingEditors.Add(box); ButtonMappings.Children.Add(box);
                }
            InitializeLegionMappings();
            InitializeProfileDraftTracking();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(ProfileFile) as StorageFile;
                string revision = file == null ? null : await FileIO.ReadTextAsync(file);
                ProfileCatalog next;
                if (file != null) next = Protocol.Decode<ProfileCatalog>(revision);
                else
                {
                    var legacy = ApplicationData.Current.LocalSettings.Values["LegionPowerProfiles"] as string;
                    next = string.IsNullOrEmpty(legacy) ? new ProfileCatalog() : ProfileCatalog.Migrate(legacy);
                }
                next.Validate(); // A failed reload leaves both the existing catalog and editor intact.
                catalogRevision = revision;
                if (file == null) await StoreCatalog(next); // Retain the legacy settings as a migration backup.
                catalog = next; profileWritable = true; RefreshProfileList();
            }
            catch (Exception ex) { profileWritable = false; ProfileNotice.Text = "No se pudieron leer los perfiles. Se conservó el archivo: " + ex.Message; }
            UpdateProfileActions();
        }
        private void UpdateProfileActions()
        {
            ProfileSave.IsEnabled = ProfileImport.IsEnabled = ProfileNameEditButton.IsEnabled = profileWritable && !profileSaving;
            ProfileExport.IsEnabled = profileWritable && !profileSaving && catalog.Profiles.Count > 0;
            ProfileDuplicate.IsEnabled = profileWritable && !profileSaving && SelectedSavedProfile() != null;
            UpdateProfileComparisonAction();
            ProfileControllerReset.IsEnabled = profileWritable && !profileSaving;
            ProfileReload.IsEnabled = !profileSaving;
            Profiles.IsEnabled = !profileSaving;
            UpdateProfileDraftNotice();
        }
        private async Task<string> StoreCatalog(ProfileCatalog next, bool backupBeforeImport = false)
        {
            next.Validate();
            string json = Protocol.Encode(next);
            if (json.Length > Protocol.MaxMessageLength) throw new InvalidOperationException("El catálogo es demasiado grande.");
            await ProfileStoreGate.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var existing = await folder.TryGetItemAsync(ProfileFile) as StorageFile;
                string actual = existing == null ? null : await FileIO.ReadTextAsync(existing);
                if (!string.Equals(actual, catalogRevision, StringComparison.Ordinal))
                    throw new InvalidOperationException("Los perfiles cambiaron en otra ventana. Recarga los perfiles antes de guardar o importar.");
                string backupName = null;
                if (backupBeforeImport)
                {
                    backupName = "profiles-antes-importar-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json";
                    var backup = await folder.CreateFileAsync(backupName, CreationCollisionOption.FailIfExists);
                    await FileIO.WriteTextAsync(backup, actual ?? Protocol.Encode(catalog));
                }
                var temp = await folder.CreateFileAsync(ProfileFile + ".tmp", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(temp, json);
                if (existing == null) await temp.RenameAsync(ProfileFile, NameCollisionOption.FailIfExists);
                else await temp.MoveAndReplaceAsync(existing);
                catalogRevision = json;
                return backupName;
            }
            finally { ProfileStoreGate.Release(); }
        }
        private void RefreshProfileList(string selected = null)
        {
            var profile = catalog.Profiles.FirstOrDefault(x => x.Id == selected) ?? catalog.Profiles.FirstOrDefault();
            profileSelectionUpdating = true;
            try { Profiles.ItemsSource = null; Profiles.ItemsSource = catalog.Profiles; Profiles.SelectedItem = profile; }
            finally { profileSelectionUpdating = false; }
            EditProfile(profile ?? NewProfileDraft());
        }
        private async void ProfileSelection_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (profileSelectionUpdating) return;
            var requested = Profiles.SelectedItem as ProfileDefinition;
            // A ComboBox changes selection before this asynchronous handler runs.
            // Restore immediately, so Apply and reentrant input still see the editor's profile.
            SetProfileSelection(SelectedSavedProfile());
            if (requested == null || requested.Id == profileSelectionId || profileSaving || profileDraftDialogOpen) return;
            profileSaving = true; UpdateProfileActions();
            try
            {
                if (!await ConfirmDiscardProfileDraft("cambiar al perfil «" + requested.Name + "»")) return;
                SetProfileSelection(requested);
                EditProfile(requested);
            }
            catch (Exception ex) { ProfileNotice.Text = "No se cambió el perfil: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private static ProfileDefinition NewProfileDraft() => new ProfileDefinition
            { Id = Guid.NewGuid().ToString("D"), Name = "", Mode = 255, Limits = new[] { 15, 15, 15 } };
        private static int? BoostValue(ComboBox box) => box.SelectedIndex == 0 ? (int?)null : box.SelectedIndex == 1 ? 0 : 2;
        private static int BoostIndex(int? value) => !value.HasValue ? 0 : value == 0 ? 1 : value == 2 ? 2 : 0;
        private void EditProfile(ProfileDefinition profile, bool resetBaseline = true)
        {
            profileDraftUpdating = true;
            try
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
                LoadLegionMappings(map);
                for (int i = 0; i < mappingEditors.Count; i++) mappingEditors[i].SelectedIndex = Array.IndexOf(ControllerLayout.Buttons, map.Map[i]) + 1;
                ProfileLeftDeadzone.Value = map.LeftDeadzone; ProfileRightDeadzone.Value = map.RightDeadzone;
                ProfileSwapSticks.IsChecked = map.SwapSticks; ProfileInvertLeft.IsChecked = map.InvertLeftY; ProfileInvertRight.IsChecked = map.InvertRightY;
                ProfileSwapTriggers.IsChecked = map.SwapTriggers; ProfileRumble.Value = map.Rumble;
                ProfileNotice.Text = "Edición local. Guarda los cambios antes de aplicar el perfil.";
                if (resetBaseline)
                {
                    profileSelectionId = catalog.Profiles.Any(x => x.Id == profile.Id) ? profile.Id : null;
                    profileDraftBaseline = ReadProfileDraft().Copy();
                }
            }
            finally { profileDraftUpdating = false; }
            UpdateProfileDraftNotice();
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
                    LegionMap = ReadLegionMappings(),
                    Map = mappingEditors.Select(x => x.SelectedIndex <= 0 ? 0 : ControllerLayout.Buttons[x.SelectedIndex - 1]).ToArray(),
                    LeftDeadzone = (int)ProfileLeftDeadzone.Value, RightDeadzone = (int)ProfileRightDeadzone.Value,
                    SwapSticks = ProfileSwapSticks.IsChecked == true, InvertLeftY = ProfileInvertLeft.IsChecked == true, InvertRightY = ProfileInvertRight.IsChecked == true,
                    SwapTriggers = ProfileSwapTriggers.IsChecked == true, Rumble = (int)ProfileRumble.Value } };
        }
        private async void ProfileNew_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving || profileDraftDialogOpen) return;
            profileSaving = true; UpdateProfileActions();
            try
            {
                if (!await ConfirmDiscardProfileDraft("crear un perfil nuevo")) return;
                SetProfileSelection(null);
                EditProfile(NewProfileDraft());
            }
            catch (Exception ex) { ProfileNotice.Text = "No se creó el borrador: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private void ProfileCapture_Click(object sender, RoutedEventArgs e)
        {
            if (profileSaving) return;
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
            EditProfile(draft, resetBaseline: false); ProfileNotice.Text = "Lecturas copiadas al editor. Revisa y guarda; los valores no disponibles quedan Sin cambiar.";
        }
        private async void ProfileSave_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving) return;
            profileSaving = true;
            UpdateProfileActions();
            try { var next = catalog.Upsert(ReadProfileDraft()); await StoreCatalog(next); catalog = next; RefreshProfileList(editingId); ProfileNotice.Text = "Perfil guardado. Pulsa Aplicar perfil guardado para usarlo."; }
            catch (Exception ex) { ProfileNotice.Text = "No se guardó el perfil: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private async void ProfileDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!profileWritable || profileSaving || !(Profiles.SelectedItem is ProfileDefinition selected)) return;
            profileSaving = true;
            UpdateProfileActions();
            try {
                var dialog = new ContentDialog { Title = "Eliminar perfil", Content = selected.Name + DraftDiscardWarning(),
                    PrimaryButtonText = "Eliminar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                var next = Protocol.Decode<ProfileCatalog>(Protocol.Encode(catalog)); next.Profiles.RemoveAll(x => x.Id == selected.Id);
                await StoreCatalog(next); catalog = next; RefreshProfileList(); }
            catch (Exception ex) { ProfileNotice.Text = "No se eliminó el perfil: " + ex.Message; }
            finally { profileSaving = false; UpdateProfileActions(); }
        }
        private async void ProfileApply_Click(object sender, RoutedEventArgs e)
        {
            if (profileSaving) return;
            if (controllerPreparing) { ProfileNotice.Text = "Termina o cancela la preparación del jugador 1 en Mando antes de aplicar un perfil."; return; }
            var profile = SelectedSavedProfile();
            if (profile == null) { ProfileNotice.Text = "Guarda y selecciona un perfil para aplicarlo."; return; }
            if (profile.Controller?.Enabled == true && !CanUseLegionMappings(profile.Controller))
            { ProfileNotice.Text = "Actualiza o reconecta el helper antes de aplicar las asignaciones Legion de este perfil."; return; }
            if (HasProfileDraftChanges())
            {
                profileSaving = true; UpdateProfileActions();
                try
                {
                    var dialog = new ContentDialog { Title = "Aplicar la versión guardada",
                        Content = ProfileDraft.DescribeSaved(profile) + "\n\nLos cambios sin guardar permanecerán en el editor y no se aplicarán.",
                        PrimaryButtonText = "Aplicar guardado", CloseButtonText = "Volver al editor", DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }
                catch (Exception ex) { ProfileNotice.Text = "No se aplicó el perfil: " + ex.Message; return; }
                finally { profileSaving = false; UpdateProfileActions(); }
            }
            await Send(new Request { Operation = "profile_apply", Profile = profile.Copy() });
        }
        private async void ControllerStop_Click(object sender, RoutedEventArgs e) => await Send(new Request { Operation = "controller_stop" });
        private void RenderController(ControllerStatus value)
        {
            lastControllerStatus = value;
            controllerPreparing = value?.PreparingPlayerOne == true;
            ControllerPlayerOne.IsEnabled = value?.SupportsAutomaticPlayerOnePreparation == true && !controllerPreparing;
            ControllerPlayerOneFps.IsEnabled = value?.SupportsPlayerOnePreparation == true && !controllerPreparing;
            ProfileApplyButton.IsEnabled = !controllerPreparing;
            ControllerStopButton.Content = controllerPreparing ? "Cancelar preparación y devolver físico" : "Devolver mando físico";
            if (value == null) { ControllerText.Text = "Estado del mando sin confirmar. Comprueba que el helper sea 0.1.20."; ControllerInputText.Text = ControllerOutputText.Text = ""; return; }
            ControllerText.Text = value.Message + (value.Active ? "\nInformes enviados: " + value.Frames : "")
                + (value.VirtualSlot.HasValue ? "\nJugador virtual: " + (value.VirtualSlot.Value + 1) : "")
                + (value.Active && value.VirtualSlot.HasValue && value.VirtualSlot != 0 ? "\nEl virtual ocupa otro puesto. Algunos juegos solo usan el jugador 1; este dato no confirma por sí solo la causa." : "")
                + (value.Active && !value.VirtualSlot.HasValue ? "\nJugador virtual: sin confirmar." : "")
                + (string.IsNullOrEmpty(value.SlotError) ? "" : "\n" + value.SlotError)
                + (string.IsNullOrEmpty(value.Error) ? "" : "\n" + value.Error);
            if (DateTime.TryParse(value.SlotCheckedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var slotTime))
                ControllerText.Text += "\nEstado del puesto leído: " + slotTime.ToLocalTime().ToString("HH:mm:ss");
            ControllerInputText.Text = value.Active ? "Entrada física: " + FrameText(value.Input) : "Entrada física: sin lectura durante la preparación o con el virtual desactivado.";
            ControllerOutputText.Text = value.Active ? "Salida virtual: " + FrameText(value.Output) : "Salida de juego: todavía no activa.";
            if (!value.SupportsPlayerOnePreparation) ControllerText.Text += "\nActualiza el helper a 0.1.10 para usar la preparación asistida.";
            if (!value.SupportsAutomaticPlayerOnePreparation) ControllerText.Text += "\nLa reconexión automática requiere el helper 0.1.11.";
        }
        private static string FrameText(GamepadFrame f)
        {
            var pressed = ControllerLayout.Labels.Where((x, i) => (f.Buttons & ControllerLayout.Buttons[i]) != 0);
            var legion = ControllerLayout.LegionLabels.Where((x, i) => (f.LegionButtons & (1 << i)) != 0);
            string extra = string.Join(" + ", legion);
            return string.Join(" + ", pressed) + (extra.Length == 0 ? "" : "\nEspeciales para remapear: " + extra)
                + "\nLT " + f.LT + " / RT " + f.RT + " · LS " + f.LX + ", " + f.LY + " · RS " + f.RX + ", " + f.RY;
        }
    }
}
