using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

internal static class NumericEditor
{
    public static Grid Create(string name, NumberBox number, Slider slider)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition {
            Width = new GridLength(1, GridUnitType.Star), MinWidth = 96, MaxWidth = 160,
        });
        var label = new TextBlock {
            Text = name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        number.HorizontalAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(number, name);
        AutomationProperties.SetName(slider, name);
        Grid.SetColumn(number, 1);
        Grid.SetRow(slider, 1);
        Grid.SetColumnSpan(slider, 2);
        row.Children.Add(label);
        row.Children.Add(number);
        row.Children.Add(slider);
        return row;
    }
}
