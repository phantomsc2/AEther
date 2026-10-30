using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AEther.WinUI;

// Arrange existing editors in source order without reparenting them on resize.
internal sealed class ShaderGroupLayout : Panel
{
    const double MinimumColumnWidth = 420;
    const double Gap = 8;
    int columns = 1;
    double[] rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Max(0, availableSize.Width) : MinimumColumnWidth;
        columns = Math.Max(1, Math.Min(Math.Max(1, Children.Count), (int)((width + Gap) / (MinimumColumnWidth + Gap))));
        var columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        rowHeights = new double[(Children.Count + columns - 1) / columns];
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            rowHeights[i / columns] = Math.Max(rowHeights[i / columns], child.DesiredSize.Height);
        }
        return new Size(width, rowHeights.Sum() + Math.Max(0, rowHeights.Length - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        double y = 0;
        for (var row = 0; row < rowHeights.Length; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = row * columns + column;
                if (index >= Children.Count) break;
                Children[index].Arrange(new Rect(column * (columnWidth + Gap), y, columnWidth, rowHeights[row]));
            }
            y += rowHeights[row] + Gap;
        }
        return finalSize;
    }
}
