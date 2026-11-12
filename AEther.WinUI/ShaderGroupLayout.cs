using System.Collections.ObjectModel;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

// Keep the existing editor instances and bindings while the toolkit places cards.
internal sealed class ShaderGroupLayout : UserControl
{
    internal ObservableCollection<UIElement> Children { get; } = [];

    public ShaderGroupLayout()
    {
        Content = new ItemsRepeater
        {
            ItemsSource = Children,
            Layout = new StaggeredLayout
            {
                DesiredColumnWidth = 420,
                ColumnSpacing = 8,
                RowSpacing = 8,
                ItemsStretch = StaggeredLayoutItemsStretch.Fill,
            },
        };
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }
}