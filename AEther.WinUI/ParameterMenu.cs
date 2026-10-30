using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

internal static class ParameterMenu
{
    public static void Attach(FrameworkElement row, Action reset)
    {
        var item = new MenuFlyoutItem { Text = "Reset to default", Icon = new SymbolIcon(Symbol.Undo) };
        item.Click += (_, _) => reset();
        var menu = new MenuFlyout();
        menu.Items.Add(item);
        row.ContextFlyout = menu;
    }
}
