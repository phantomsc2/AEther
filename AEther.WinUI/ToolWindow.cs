using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace AEther.WinUI;

public abstract partial class ToolWindow : Window
{
    bool destroying;
    protected Grid Layout { get; } = new() { Padding = new Thickness(16), RowSpacing = 12 };

    protected ToolWindow(string title, int width, int height)
    {
        Title = title;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        Content = Layout;
        Layout.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        AppWindow.Resize(new SizeInt32(width, height));
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, e) =>
        {
            if (destroying) return;
            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => destroying = true;
    }

    public void SetOwner(Window owner)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(handle, -8, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var position = owner.AppWindow.Position;
        AppWindow.Move(new PointInt32(position.X + 40, position.Y + 40));
    }

    public bool IsVisible => !destroying && AppWindow.IsVisible;
    public void Show() => Activate();
    public void Hide() => AppWindow.Hide();
    public void Destroy() { destroying = true; base.Close(); }

    internal static void AddShortcut(UIElement root, VirtualKey key, Action action)
    {
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var accelerator = new KeyboardAccelerator { Key = key };
        accelerator.Invoked += (_, e) => { e.Handled = true; action(); };
        root.KeyboardAccelerators.Add(accelerator);
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
