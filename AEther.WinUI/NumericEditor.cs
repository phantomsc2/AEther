using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

internal static class NumericEditor
{
    public static Grid Create(string name, NumberBox number, Slider slider)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        var label = new TextBlock {
            Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(label, name);
        number.HorizontalAlignment = HorizontalAlignment.Stretch;
        number.VerticalAlignment = VerticalAlignment.Center;
        slider.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(number, name);
        AutomationProperties.SetName(slider, name);
        Grid.SetColumn(number, 1);
        Grid.SetColumn(controls, 1);
        row.Children.Add(label);
        controls.Children.Add(slider);
        controls.Children.Add(number);
        row.Children.Add(controls);
        return row;
    }
}
