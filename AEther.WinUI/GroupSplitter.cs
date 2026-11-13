using CommunityToolkit.WinUI.Controls;

namespace AEther.WinUI;

// Public so the WinUI XAML metadata provider can resolve the inherited Toolkit style.
public sealed partial class GroupSplitter : GridSplitter
{
    internal Action? ResizeStarting { get; init; }
    internal Func<double, bool>? ResizeRows { get; init; }

    protected override void OnDragStarting() => ResizeStarting?.Invoke();
    protected override bool OnDragVertical(double change) => ResizeRows?.Invoke(change) ?? false;
}
