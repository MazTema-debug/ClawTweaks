// SPDX-License-Identifier: AGPL-3.0-only
// GoTweaks navigation/focus design adapted from corando98/GoTweaks at 99ed5f5.
// Original MIT notices are retained in LICENSE.GoTweaks; see SOURCES.md.
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace LegionTweaks.Widget
{
    public sealed partial class MainPage
    {
        private readonly ShellNavigation shell = new ShellNavigation();
        private bool shellReady, renderingShell, shellStatusDialogOpen;
        private VirtualKey? shellHeldTrigger;
        private Border focusedShellCard;
        private Brush originalShellBorder;
        private RadioButton[] ShellButtons => new[] { HomeNav, PowerNav, DisplayNav, ControllerNav, ProfilesNav, GamesNav, SettingsNav };
        private PivotItem[] ShellPages => new[] { HomeTab, PowerTab, DisplayTab, ControllerTab, ProfilesTab, GamesTab, SystemTab };
        private bool ShellModal => remapDialogBusy || pawnIoDialogBusy || profileSaving || profileDraftDialogOpen || shellStatusDialogOpen;
        private void InitializeShell()
        {
            foreach (var section in ShellNavigation.Sections)
                ShellSectionPicker.Items.Add(new ComboBoxItem { Content = ShellNavigation.Label(section), Tag = section });
            foreach (var button in ShellButtons)
                button.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Shell_NavKeyUp), true);
            shellReady = true;
            Controls.SelectionChanged += (s, e) => {
                if (e.OriginalSource != Controls || renderingShell) return;
                int index = Array.IndexOf(ShellPages, Controls.SelectedItem);
                if (index >= 0) { shell.Select((ShellSection)index); RenderShellNavigation(); }
            };
            ShellRoot.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Shell_KeyDown), true);
            ShellRoot.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Shell_KeyUp), true);
            ShellRoot.GotFocus += Shell_GotFocus;
            RenderShellNavigation();
            LayoutShell();
        }
        private void RenderShellNavigation()
        {
            if (!shellReady) return;
            renderingShell = true;
            try {
                int index = (int)shell.Current;
                ShellButtons[index].IsChecked = true;
                ShellSectionPicker.SelectedIndex = index;
                if (Controls.SelectedItem != ShellPages[index]) Controls.SelectedItem = ShellPages[index];
                ShellAreaText.Text = ShellNavigation.Label(shell.Current);
            }
            finally { renderingShell = false; }
        }
        private void Shell_NavChecked(object sender, RoutedEventArgs e)
        {
            if (!shellReady || renderingShell) return;
            if (working || ShellModal) { RenderShellNavigation(); return; }
            if (sender is RadioButton button && shell.Select(button.Tag as string)) RenderShellNavigation();
        }
        private void Shell_SectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!shellReady || renderingShell) return;
            if (working || ShellModal) { RenderShellNavigation(); return; }
            if (ShellSectionPicker.SelectedItem is ComboBoxItem item && item.Tag is ShellSection section && shell.Select(section))
                RenderShellNavigation();
        }
        private async void Shell_NavKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (!shellReady || working || ShellModal || (e.Key != VirtualKey.Enter && e.Key != VirtualKey.Space && e.Key != VirtualKey.GamepadA)) return;
            e.Handled = true;
            // Transfer focus after release, so A/Enter cannot activate a newly focused setting.
            await FocusShellContent();
        }
        private void Shell_StepClick(object sender, RoutedEventArgs e)
        {
            if (!shellReady || working || ShellModal) return;
            int direction = (sender as Button)?.Tag as string == "previous" ? -1 : 1;
            if (shell.Move(direction)) { RenderShellNavigation(); FocusShellNavigation(); }
        }
        private async void Shell_TileClick(object sender, RoutedEventArgs e)
        {
            if (!shellReady || working || ShellModal) return;
            string tag = (sender as Button)?.Tag as string;
            DetailsCard detail = null;
            if (tag == "Overlay") { shell.Select(ShellSection.Display); detail = OverlayDetails; }
            else if (tag == "Cooling") { shell.Select(ShellSection.Power); detail = CoolingDetails; }
            else if (tag == "Recording") { shell.Select(ShellSection.Settings); detail = RecordingDetails; }
            else if (!shell.Select(tag) && !Enum.IsDefined(typeof(ShellSection), tag ?? "")) return;
            RenderShellNavigation();
            if (detail != null) detail.IsExpanded = true;
            await FocusShellContent(detail?.Body as FrameworkElement);
        }
        private async Task FocusShellContent(FrameworkElement target = null)
        {
            if (!pageLoaded || working || ShellModal) return;
            int view = viewGeneration;
            var destination = shell.Current;
            try {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => {
                    if (!pageLoaded || view != viewGeneration || shell.Current != destination || working || ShellModal) return;
                    var root = target ?? ShellPages[(int)destination];
                    root.UpdateLayout();
                    var focus = FirstShellControl(root);
                    if (focus != null) { focus.Focus(FocusState.Keyboard); focus.StartBringIntoView(); }
                });
            }
            catch (Exception) { /* A closing view has no focus destination. */ }
        }
        private static Control FirstShellControl(DependencyObject root)
        {
            if (root is UIElement element && element.Visibility != Visibility.Visible) return null;
            if (root is Control control && control.IsEnabled && control.IsTabStop
                && (control is Button || control is ComboBox || control is Slider || control is TextBox || control is ToggleSwitch || control is CheckBox)) return control;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
                var found = FirstShellControl(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }
        private void FocusShellNavigation()
        {
            Control destination = ShellSectionPicker.Visibility == Visibility.Visible ? (Control)ShellSectionPicker : ShellButtons[(int)shell.Current];
            destination.Focus(FocusState.Keyboard);
        }
        private void Shell_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (shellHeldTrigger == e.Key) shellHeldTrigger = null;
        }
        private void Shell_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!shellReady || (e.Key != VirtualKey.GamepadLeftTrigger && e.Key != VirtualKey.GamepadRightTrigger)) return;
            if (shellHeldTrigger.HasValue) { e.Handled = true; return; }
            var context = ShellFocusContext.Content;
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node)) {
                if (node is ContentDialog) { context = ShellFocusContext.Dialog; break; }
                if (node is TextBox || node is PasswordBox || node is RichEditBox) { context = ShellFocusContext.TextEditor; break; }
                if (node is ComboBox box) { context = box == ShellSectionPicker && !box.IsDropDownOpen ? ShellFocusContext.Navigation : ShellFocusContext.ComboBox; break; }
                if (node is Slider) { context = ShellFocusContext.Slider; break; }
                if (node is RadioButton) context = ShellFocusContext.Navigation;
            }
            if (!ShellNavigation.AllowsShortcut(context, working, ShellModal)) return;
            if (shell.Move(e.Key == VirtualKey.GamepadLeftTrigger ? -1 : 1)) {
                shellHeldTrigger = e.Key;
                RenderShellNavigation(); FocusShellNavigation(); e.Handled = true;
            }
        }
        private void Shell_GotFocus(object sender, RoutedEventArgs e)
        {
            Border card = null;
            for (var node = e.OriginalSource as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node)) {
                if (node is Border border && border.Tag as string == "ShellCard") { card = border; break; }
                if (node == ShellRoot) break;
            }
            if (card != focusedShellCard) {
                if (focusedShellCard != null) focusedShellCard.BorderBrush = originalShellBorder;
                focusedShellCard = card;
                if (card != null) { originalShellBorder = card.BorderBrush; card.BorderBrush = (Brush)Resources["CardFocusBorderBrush"]; }
            }
            (e.OriginalSource as FrameworkElement)?.StartBringIntoView();
        }
        private void Shell_SizeChanged(object sender, SizeChangedEventArgs e) { if (shellReady) LayoutShell(); }
        private void LayoutShell()
        {
            bool compact = ShellNavigation.CompactNavigation(ShellNavigationHost.ActualWidth);
            bool pickerFocused = ShellSectionPicker == FocusManager.GetFocusedElement();
            bool radioFocused = ShellButtons.Contains(FocusManager.GetFocusedElement());
            ShellNavigationGrid.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ShellSectionPicker.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            ArrangeShellGrid(ShellNavigationGrid, ShellButtons.Cast<FrameworkElement>().ToArray(), 7);
            ArrangeShellGrid(HomeTilesGrid, HomeTilesGrid.Children.Cast<FrameworkElement>().ToArray(), ShellNavigation.TileColumns(HomeTilesGrid.ActualWidth));
            if ((compact && radioFocused) || (!compact && pickerFocused)) FocusShellNavigation();
        }
        private static void ArrangeShellGrid(Grid grid, FrameworkElement[] children, int columns)
        {
            if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count == (children.Length + columns - 1) / columns) return;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < (children.Length + columns - 1) / columns; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i % columns); Grid.SetRow(children[i], i / columns); }
        }
        private async void Shell_StatusDetails(object sender, RoutedEventArgs e)
        {
            if (!pageLoaded || ShellModal) return;
            shellStatusDialogOpen = true;
            try { await new ContentDialog { Title = "Último mensaje", Content = new ScrollViewer { Content = new TextBlock { Text = StatusText.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, CloseButtonText = "Cerrar" }.ShowAsync(); }
            catch (Exception) { /* Another view/dialog may have closed while showing the message. */ }
            finally { shellStatusDialogOpen = false; }
        }
    }
}
