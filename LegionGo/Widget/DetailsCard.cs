// SPDX-License-Identifier: AGPL-3.0-only
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;

namespace LegionTweaks.Widget
{
    // Native UWP disclosure. Collapsing changes only presentation and preserves the actual editor controls.
    [ContentProperty(Name = "Body")]
    public sealed class DetailsCard : ContentControl
    {
        private readonly Button header;
        private readonly TextBlock title, description, indicator;
        private readonly ContentControl body;
        private bool expanded;
        public DetailsCard()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            IsTabStop = false;
            title = new TextBlock { FontSize = 17, FontWeight = Windows.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            description = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 192, 198, 207)), Margin = new Thickness(0, 4, 0, 0) };
            indicator = new TextBlock { Text = "+", FontSize = 22, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var labels = new StackPanel(); labels.Children.Add(title); labels.Children.Add(description);
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(indicator, 1); grid.Children.Add(labels); grid.Children.Add(indicator);
            header = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                BorderThickness = new Thickness(0), Margin = new Thickness(0), Padding = new Thickness(0, 4, 0, 4), MinHeight = 44 };
            header.Click += (s, e) => IsExpanded = !IsExpanded;
            body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false,
                Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
            var panel = new StackPanel(); panel.Children.Add(header); panel.Children.Add(body); Content = panel;
        }
        public string Title { get => title.Text; set { title.Text = value ?? ""; DescribeHeader(); } }
        public string Description
        {
            get => description.Text;
            set { description.Text = value ?? ""; AutomationProperties.SetHelpText(header, description.Text); }
        }
        public UIElement Body { get => body.Content as UIElement; set => body.Content = value; }
        public bool IsExpanded
        {
            get => expanded;
            set {
                expanded = value; body.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                indicator.Text = value ? "−" : "+"; DescribeHeader();
            }
        }
        private void DescribeHeader() => AutomationProperties.SetName(header, (expanded ? "Contraer " : "Abrir ") + title.Text);
    }
}
