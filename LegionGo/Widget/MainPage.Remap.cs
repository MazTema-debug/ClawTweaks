// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private sealed class PipeRemapEditorService : IRemapEditorService
        {
            public Task<Response> Send(Request request) => PipeClient.Send(request);
        }
        private sealed class RemapDraftChoice
        {
            public string Id { get; set; }
            public string Label { get; set; }
        }
        private readonly RemapEditor remapEditor = new RemapEditor(new PipeRemapEditorService());
        private bool renderingRemap, remapInitialized, remapDialogBusy;
        private string remapLocalNotice;
        private RemapForm EditingRemap => remapEditor.Current.Get(Chosen<RemapInput>(RemapInputBox));
        private static T Chosen<T>(ComboBox box) => box?.SelectedItem is ComboBoxItem item && item.Tag is T value ? value : default(T);
        private static void RemapOptions<T>(ComboBox box, System.Collections.Generic.IEnumerable<T> values, Func<T, string> label, T selected)
        {
            var items = values.Select(value => new ComboBoxItem { Content = label(value), Tag = value }).ToArray();
            box.ItemsSource = items;
            box.SelectedItem = items.FirstOrDefault(item => Equals(item.Tag, selected)) ?? items.FirstOrDefault();
        }
        private void InitializeRemap()
        {
            renderingRemap = true;
            RemapOptions(RemapKeyPicker, Enumerable.Range(0, 256).Where(RemapCatalog.IsKeyboardKey), RemapText.Key, 65);
            renderingRemap = false;
            RenderRemap();
            remapInitialized = true;
            Controls.SelectionChanged += async (s, e) => {
                if (e.OriginalSource == Controls && Controls.SelectedItem == ControllerTab && pageLoaded && !remapDialogBusy)
                    await RemapRun(remapEditor.Refresh);
            };
        }
        private void RenderRemapCapability(bool supported, FpsY3TrialStatus trial = null)
        {
            remapEditor.ObserveY3(trial);
            if (!supported) remapEditor.Invalidate("El helper no anuncia edición de distribuciones. Puedes preparar un borrador; para guardarlo hace falta un helper compatible.");
            RenderRemapState();
        }
        private void RenderRemap()
        {
            renderingRemap = true;
            try {
                var draft = remapEditor.Current;
                RemapOptions(RemapModeBox, new[] { RemapMode.Gamepad, RemapMode.Fps, RemapMode.Split }, RemapText.Mode, draft.Mode);
                var choices = remapEditor.Choices.Select(d => new RemapDraftChoice { Id = d.Id, Label = d.Name
                    + (d.Dirty ? " · borrador" : "") + (!remapEditor.Catalog.Profiles.Any(p => p.Id == d.Id) ? " · sin guardar" : "") }).ToArray();
                RemapSavedBox.ItemsSource = choices;
                RemapSavedBox.SelectedItem = choices.FirstOrDefault(c => c.Id == draft.Id);
                RemapNameBox.Text = draft.Name;
                var groupNames = new[] { "Frontal y gatillos", "Traseros y especiales", "Stick izquierdo", "Stick derecho", "Rueda", "Sensor FPS", "Panel táctil" };
                int group = Chosen<int>(RemapGroupBox);
                if (group == 5 && draft.Mode != RemapMode.Fps) group = 0;
                RemapOptions(RemapGroupBox, Enumerable.Range(0, groupNames.Length).Where(g => g != 5 || draft.Mode == RemapMode.Fps), g => groupNames[g], group);
                RenderRemapInputs();
            }
            finally { renderingRemap = false; }
            RenderRemapState();
        }
        private void RenderRemapInputs()
        {
            var selected = Chosen<RemapInput>(RemapInputBox);
            RemapOptions(RemapInputBox, RemapCatalog.Inputs.Where(i => RemapText.Group(i) == Chosen<int>(RemapGroupBox)), RemapText.Input, selected);
            RenderRemapForm();
        }
        private static void RemapVisible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        private void RenderRemapForm()
        {
            var form = EditingRemap;
            RemapControlText.Text = RemapText.Input(form.Input);
            RemapOptions(RemapKindBox, (RemapActionKind[])Enum.GetValues(typeof(RemapActionKind)), RemapText.Kind, form.Kind);
            RemapVisible(RemapKeyboardPanel, form.Kind == RemapActionKind.Keyboard);
            RemapKeysBox.Text = form.Keys;
            RemapVisible(RemapMouseButtonBox, form.Kind == RemapActionKind.MouseButton);
            RemapOptions(RemapMouseButtonBox, new[] { RemapMouseButton.Left, RemapMouseButton.Right, RemapMouseButton.Middle, RemapMouseButton.Back, RemapMouseButton.Forward }, RemapText.MouseButton, form.MouseButton);
            RemapVisible(RemapDirectionBox, form.Kind == RemapActionKind.MouseMove);
            int direction = form.X < 0 ? 0 : form.X > 0 ? 1 : form.Y < 0 ? 2 : 3;
            RemapOptions(RemapDirectionBox, new[] { 0, 1, 2, 3 }, d => new[] { "Izquierda ←", "Derecha →", "Arriba ↑", "Abajo ↓" }[d], direction);
            RemapVisible(RemapWheelBox, form.Kind == RemapActionKind.MouseWheel);
            RemapOptions(RemapWheelBox, new[] { 1, -1 }, d => d == 1 ? "Arriba ↑" : "Abajo ↓", form.Y);
            RemapVisible(RemapGamepadButtonBox, form.Kind == RemapActionKind.GamepadButton);
            RemapOptions(RemapGamepadButtonBox, ControllerLayout.Buttons, b => ControllerLayout.Labels[Array.IndexOf(ControllerLayout.Buttons, b)], form.Button);
            bool axis = form.Kind == RemapActionKind.GamepadAxis;
            RemapVisible(RemapAxisBox, axis); RemapVisible(RemapAxisDirectionBox, axis);
            RemapOptions(RemapAxisBox, new[] { RemapAxis.LX, RemapAxis.LY, RemapAxis.RX, RemapAxis.RY, RemapAxis.LT, RemapAxis.RT }, a => a.ToString(), form.Axis);
            RemapOptions(RemapAxisDirectionBox, form.Axis == RemapAxis.LT || form.Axis == RemapAxis.RT ? new[] { 1 } : new[] { 1, -1 }, d => d == 1 ? "Positivo (+)" : "Negativo (−)", form.Direction);
            RemapVisible(RemapPlayerBox, axis || form.Kind == RemapActionKind.GamepadButton);
            RemapOptions(RemapPlayerBox, remapEditor.Current.Mode == RemapMode.Split ? new[] { 1, 2 } : new[] { 1 }, p => "Jugador " + p, form.Player);
            RemapVisible(RemapFunctionBox, form.Kind == RemapActionKind.Function);
            RemapOptions(RemapFunctionBox, new[] { RemapFunction.GameBar, RemapFunction.PauseRemap, RemapFunction.ReturnOriginal }, RemapText.Function, form.Function);
            RemapOptions(RemapBehaviorBox, form.Behaviors, RemapText.Behavior, form.Behavior);
            RemapBehaviorBox.IsEnabled = form.Behaviors.Length > 1;
            bool digital = form.Kind == RemapActionKind.Keyboard || form.Kind == RemapActionKind.MouseButton || form.Kind == RemapActionKind.GamepadButton || form.Kind == RemapActionKind.Function;
            RemapVisible(RemapThresholdSlider, RemapCatalog.IsAnalog(form.Input) && digital);
            RemapVisible(RemapAmountSlider, axis || form.Kind == RemapActionKind.MouseMove || form.Kind == RemapActionKind.MouseWheel);
            RemapThresholdSlider.Value = form.Threshold; RemapAmountSlider.Value = form.Amount;
        }
        private void RenderRemapState()
        {
            if (RemapNotice == null) return;
            bool ready = !remapEditor.IsBusy && !remapDialogBusy;
            RemapEditingSurface.IsEnabled = ready;
            RemapSaveButton.IsEnabled = remapEditor.CanSave && remapEditor.Current.Dirty;
            RemapDeleteButton.IsEnabled = remapEditor.CanSave && remapEditor.SavedCurrent != null;
            RemapRestoreButton.IsEnabled = remapEditor.SavedCurrent != null && remapEditor.Current.Dirty;
            RemapY3TestButton.IsEnabled = ready && remapEditor.CanTestY3;
            RemapY3StopButton.IsEnabled = ready && remapEditor.CanStopY3;
            var trial = remapEditor.Y3Trial;
            RemapY3Status.Text = trial?.Message ?? "Prueba Y3: requiere un helper compatible. Usa FPS, Y3 → Teclado en Pulso, una letra/número/F1–F24/Insert/Escape y el resto Original.";
            if (trial?.Active == true) RemapY3Status.Text += "\nPulsos enviados: " + trial.PulsesSent + " · quedan " + (trial.RemainingMilliseconds / 1000) + " s.";
            if (!string.IsNullOrEmpty(trial?.Error)) RemapY3Status.Text += "\n" + trial.Error;
            RemapProgress.Visibility = remapEditor.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            RemapNotice.Text = remapLocalNotice ?? remapEditor.Message;
            RemapDraftText.Text = "Borrador: " + (remapEditor.Current.Dirty ? "cambios sin guardar" : "sin cambios") + " · " + RemapText.Mode(remapEditor.Current.Mode);
            RemapSavedText.Text = "Guardada: " + (remapEditor.SavedCurrent?.Name ?? "ninguna para este borrador")
                + (remapEditor.ReloadRequired ? " · catálogo sin confirmar" : " · catálogo consultado");
            RemapSummaryText.Text = string.Join("\n", remapEditor.Current.Forms.Values.Where(f => f.Kind != RemapActionKind.Original).OrderBy(f => f.Input).Select(f => RemapText.Input(f.Input) + " → " + f.Summary));
            if (RemapSummaryText.Text.Length == 0) RemapSummaryText.Text = "Todos los controles conservan Original.";
            if (RemapInputBox.SelectedItem != null) {
                var form = EditingRemap;
                try {
                    form.Build(remapEditor.Current.Mode);
                    RemapBindingText.Text = form.Summary
                        + (form.Kind == RemapActionKind.Original || form.Kind == RemapActionKind.None ? "" : " · " + RemapText.Behavior(form.Behavior))
                        + (RemapThresholdSlider.Visibility == Visibility.Visible ? "\nUmbral: " + form.Threshold + " %" : "")
                        + (RemapAmountSlider.Visibility == Visibility.Visible ? "\nIntensidad: " + form.Amount + " %" : "");
                }
                catch (ArgumentException ex) { RemapBindingText.Text = "Asignación incompleta: " + ex.Message; }
            }
        }
        private async void RemapY3Test_Click(object sender, RoutedEventArgs e) => await RemapRun(remapEditor.TestY3);
        private async void RemapY3Stop_Click(object sender, RoutedEventArgs e) => await RemapRun(remapEditor.StopY3);
        private void RemapChanged(bool redrawForm = false)
        {
            remapEditor.Current.Touch(); remapLocalNotice = null;
            if (redrawForm) { renderingRemap = true; try { RenderRemapForm(); } finally { renderingRemap = false; } }
            RenderRemapState();
        }
        private bool CanEditRemap => remapInitialized && !renderingRemap && !remapEditor.IsBusy && !remapDialogBusy;
        private async Task RemapRun(Func<Task<bool>> action)
        {
            if (!pageLoaded || remapEditor.IsBusy || remapDialogBusy) return;
            int view = viewGeneration;
            remapLocalNotice = null;
            var pending = action(); RenderRemapState();
            await pending;
            if (pageLoaded && view == viewGeneration) RenderRemap();
        }
        private async void RemapReload_Click(object sender, RoutedEventArgs e) => await RemapRun(remapEditor.Refresh);
        private async void RemapSave_Click(object sender, RoutedEventArgs e) => await RemapRun(remapEditor.Save);
        private void RemapNew_Click(object sender, RoutedEventArgs e)
        { if (CanEditRemap) { remapEditor.New(remapEditor.Current.Mode); remapLocalNotice = null; RenderRemap(); } }
        private void RemapMode_Changed(object sender, SelectionChangedEventArgs e)
        { if (CanEditRemap) { remapEditor.SelectMode(Chosen<RemapMode>(RemapModeBox)); RenderRemap(); } }
        private void RemapSaved_Changed(object sender, SelectionChangedEventArgs e)
        { if (CanEditRemap && RemapSavedBox.SelectedItem is RemapDraftChoice choice) { remapEditor.Select(choice.Id); RenderRemap(); } }
        private void RemapName_Changed(object sender, TextChangedEventArgs e)
        { if (CanEditRemap) { remapEditor.Current.Name = RemapNameBox.Text; RemapChanged(); } }
        private void RemapGroup_Changed(object sender, SelectionChangedEventArgs e)
        { if (CanEditRemap) { renderingRemap = true; try { RenderRemapInputs(); } finally { renderingRemap = false; } RenderRemapState(); } }
        private void RemapInput_Changed(object sender, SelectionChangedEventArgs e)
        { if (CanEditRemap) { renderingRemap = true; try { RenderRemapForm(); } finally { renderingRemap = false; } RenderRemapState(); } }
        private void RemapKind_Changed(object sender, SelectionChangedEventArgs e)
        { if (CanEditRemap) { EditingRemap.Kind = Chosen<RemapActionKind>(RemapKindBox); EditingRemap.Normalize(); RemapChanged(true); } }
        private void RemapKeys_Changed(object sender, TextChangedEventArgs e)
        { if (CanEditRemap) { EditingRemap.Keys = RemapKeysBox.Text; RemapChanged(); } }
        private void RemapKeyAdd_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditRemap) return;
            string key = RemapText.Key(Chosen<int>(RemapKeyPicker));
            string next = string.IsNullOrWhiteSpace(EditingRemap.Keys) ? key : EditingRemap.Keys + " + " + key;
            try { RemapText.ParseKeys(next); RemapKeysBox.Text = next; }
            catch (ArgumentException ex) { remapLocalNotice = ex.Message; RenderRemapState(); }
        }
        private void RemapKeyClear_Click(object sender, RoutedEventArgs e) { if (CanEditRemap) RemapKeysBox.Text = ""; }
        private void RemapAxis_Changed(object sender, SelectionChangedEventArgs e)
        { if (CanEditRemap) { EditingRemap.Axis = Chosen<RemapAxis>(RemapAxisBox); EditingRemap.Normalize(); RemapChanged(true); } }
        private void RemapValue_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!CanEditRemap) return;
            var form = EditingRemap;
            if (sender == RemapMouseButtonBox) form.MouseButton = Chosen<RemapMouseButton>(RemapMouseButtonBox);
            else if (sender == RemapDirectionBox) {
                int d = Chosen<int>(RemapDirectionBox); form.X = d == 0 ? -1 : d == 1 ? 1 : 0; form.Y = d == 2 ? -1 : d == 3 ? 1 : 0;
            }
            else if (sender == RemapWheelBox) form.Y = Chosen<int>(RemapWheelBox);
            else if (sender == RemapGamepadButtonBox) form.Button = Chosen<int>(RemapGamepadButtonBox);
            else if (sender == RemapAxisDirectionBox) form.Direction = Chosen<int>(RemapAxisDirectionBox);
            else if (sender == RemapPlayerBox) form.Player = Chosen<int>(RemapPlayerBox);
            else if (sender == RemapFunctionBox) form.Function = Chosen<RemapFunction>(RemapFunctionBox);
            else if (sender == RemapBehaviorBox) form.Behavior = Chosen<RemapBehavior>(RemapBehaviorBox);
            RemapChanged();
        }
        private void RemapSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!CanEditRemap) return;
            if (sender == RemapThresholdSlider) EditingRemap.Threshold = (int)e.NewValue;
            else EditingRemap.Amount = (int)e.NewValue;
            RemapChanged();
        }
        private void RemapOriginal_Click(object sender, RoutedEventArgs e)
        { if (CanEditRemap) { EditingRemap.Kind = RemapActionKind.Original; EditingRemap.Normalize(); RemapChanged(true); } }
        private void RemapLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (RemapSecondColumn == null || RemapActionCard == null) return;
            bool wide = e.NewSize.Width >= 740;
            RemapSecondColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(RemapActionCard, wide ? 1 : 0); Grid.SetRow(RemapActionCard, wide ? 0 : 1);
            RemapActionCard.Margin = wide ? new Thickness(12, 0, 0, 12) : new Thickness(0, 0, 0, 12);
        }
        private async Task<bool> ConfirmRemap(string title, string content, string action)
        {
            int view = viewGeneration;
            remapDialogBusy = true; RenderRemapState();
            try {
                var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = action,
                    CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                return await dialog.ShowAsync() == ContentDialogResult.Primary && pageLoaded && view == viewGeneration;
            }
            catch (Exception ex) { if (pageLoaded && view == viewGeneration) remapLocalNotice = ex.Message; return false; }
            finally { if (view == viewGeneration) { remapDialogBusy = false; if (pageLoaded) RenderRemapState(); } }
        }
        private async void RemapRestore_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditRemap || remapEditor.SavedCurrent == null) return;
            if (await ConfirmRemap("Recuperar copia guardada", "Se descartarán los cambios de este borrador y se recuperará la última copia consultada. El mando no cambia.", "Recuperar")) {
                remapEditor.RestoreSaved(); remapLocalNotice = null; RenderRemap();
            }
        }
        private async void RemapDelete_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditRemap || !remapEditor.CanSave || remapEditor.SavedCurrent == null) return;
            string id = remapEditor.Current.Id, name = remapEditor.SavedCurrent.Name;
            if (await ConfirmRemap("Eliminar copia guardada", "Eliminar «" + name + "» del catálogo. Si tienes cambios locales, su borrador se conserva.", "Eliminar"))
                await RemapRun(() => remapEditor.Delete(id));
        }
        private async void RemapName_Click(object sender, RoutedEventArgs e)
        {
            if (!CanEditRemap) return;
            int view = viewGeneration;
            remapDialogBusy = true; RenderRemapState();
            try {
                var text = new TextBox { Text = remapEditor.Current.Name, Header = "Nombre de la distribución", MaxLength = 40,
                    IsSpellCheckEnabled = false, IsTextPredictionEnabled = false };
                var keys = new GridView { IsSwipeEnabled = false, SelectionMode = ListViewSelectionMode.None,
                    ItemsPanel = (ItemsPanelTemplate)Windows.UI.Xaml.Markup.XamlReader.Load("<ItemsPanelTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><ItemsWrapGrid Orientation='Horizontal' ItemWidth='46' ItemHeight='46'/></ItemsPanelTemplate>") };
                var style = new Style { TargetType = typeof(GridViewItem) };
                style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
                style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
                keys.ItemContainerStyle = style;
                var caps = new CheckBox { Content = "Mayúsculas" };
                foreach (char character in "1234567890qwertyuiopasdfghjklñzxcvbnm._-áéíóúü()") {
                    string value = character.ToString();
                    var button = new Button { Content = value, MinWidth = 0, Width = 44, Height = 44, Margin = new Thickness(0), Padding = new Thickness(0) };
                    Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, "Escribir " + value);
                    button.Click += (s, args) => {
                        var edit = ProfileNameEdit.Insert(text.Text, text.SelectionStart, text.SelectionLength, caps.IsChecked == true ? value.ToUpperInvariant() : value);
                        text.Text = edit.Text; text.SelectionStart = edit.Caret; text.SelectionLength = 0;
                    };
                    keys.Items.Add(button);
                }
                var space = new Button { Content = "Espacio" };
                space.Click += (s, args) => { var edit = ProfileNameEdit.Insert(text.Text, text.SelectionStart, text.SelectionLength, " "); text.Text = edit.Text; text.SelectionStart = edit.Caret; };
                var erase = new Button { Content = "Borrar" };
                erase.Click += (s, args) => { var edit = ProfileNameEdit.Backspace(text.Text, text.SelectionStart, text.SelectionLength); text.Text = edit.Text; text.SelectionStart = edit.Caret; };
                var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(erase, 1); actions.Children.Add(space); actions.Children.Add(erase);
                var panel = new StackPanel(); panel.Children.Add(text); panel.Children.Add(caps); panel.Children.Add(keys); panel.Children.Add(actions);
                var dialog = new ContentDialog { Title = "Editar nombre con mando", Content = new ScrollViewer { Content = panel,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    PrimaryButtonText = "Usar nombre", CloseButtonText = "Cancelar", IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(text.Text) };
                text.TextChanged += (s, args) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(text.Text);
                text.SelectionStart = text.Text.Length;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && pageLoaded && view == viewGeneration) {
                    remapEditor.Current.Name = text.Text.Trim(); remapEditor.Current.Touch(); remapLocalNotice = null; RenderRemap();
                }
            }
            catch (Exception ex) { if (pageLoaded && view == viewGeneration) remapLocalNotice = "No se pudo editar el nombre: " + ex.Message; }
            finally { if (view == viewGeneration) { remapDialogBusy = false; if (pageLoaded) RenderRemapState(); } }
        }
    }
}
