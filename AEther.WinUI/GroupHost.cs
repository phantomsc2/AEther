using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

internal enum GroupSizing
{
    Auto,
    Fill
}

/// <summary>A vertical set of native Expanders sharing a bounded viewport.</summary>
internal sealed class GroupHost : Grid
{
    readonly List<Group> groups = [];
    readonly RowDefinition unusedSpace = new() { Height = new GridLength(1, GridUnitType.Star) };

    internal GroupHost() => RowDefinitions.Add(unusedSpace);

    internal Expander AddGroup(string title, UIElement content, GroupSizing sizing = GroupSizing.Fill)
    {
        var card = new Expander
        {
            Header = title, Content = content, IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var group = new Group(card, sizing);
        int row = groups.Count * 2;
        RowDefinitions.Insert(row, group.Gap);
        RowDefinitions.Insert(row + 1, group.Row);
        SetRow(card, row + 1);
        Children.Add(card);
        var resize = new GroupResize(this, group);
        var splitter = new GroupSplitter
        {
            Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridSplitter.GridResizeDirection.Rows,
            ResizeStarting = resize.Start,
            ResizeRows = resize.Resize,
        };
        group.Resize = resize;
        group.Splitter = splitter;
        SetRow(splitter, row);
        Children.Add(splitter);
        groups.Add(group);
        card.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, (_, _) => UpdateGroups());
        UpdateGroups();
        return card;
    }

    void UpdateGroups()
    {
        Group? previous = null;
        foreach (var group in groups)
        {
            bool expanded = group.Card.IsExpanded;
            group.Row.MinHeight = expanded && group.Sizing == GroupSizing.Fill ? 96 : 0;
            group.Row.Height = !expanded ? GridLength.Auto
                : group.Sizing == GroupSizing.Fill ? new GridLength(group.Weight, GridUnitType.Star)
                : group.UserHeight is { } height ? new GridLength(height) : GridLength.Auto;
            group.Resize!.Upper = expanded ? previous : null;
            group.Splitter!.Visibility = expanded && previous != null ? Visibility.Visible : Visibility.Collapsed;
            group.Gap.Height = new GridLength(groups[0] == group ? 0 : 8);
            AutomationProperties.SetName(group.Splitter, previous == null
                ? "Resize groups"
                : $"Resize {previous.Card.Header} and {group.Card.Header}");
            if (expanded) previous = group;
        }

        unusedSpace.Height = groups.Any(g => g.Card.IsExpanded && g.Sizing == GroupSizing.Fill)
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
    }

    sealed class Group(Expander card, GroupSizing sizing)
    {
        internal readonly Expander Card = card;
        internal readonly GroupSizing Sizing = sizing;
        internal readonly RowDefinition Row = new();
        internal readonly RowDefinition Gap = new();
        internal GroupSplitter? Splitter;
        internal GroupResize? Resize;
        internal double Weight = 1;
        internal double? UserHeight;
    }

    // Keep Toolkit styling, pointer capture, keyboard increments and accessibility.
    // Only row selection/allocation differs: collapsed groups must be skipped.
    sealed class GroupResize(GroupHost host, Group lower)
    {
        internal Group? Upper;
        Group? activeUpper;
        double upperHeight, lowerHeight, weightPerPixel;

        internal void Start()
        {
            activeUpper = Upper;
            upperHeight = activeUpper?.Row.ActualHeight ?? 0;
            lowerHeight = lower.Row.ActualHeight;
            var fills = host.groups.Where(g => g.Card.IsExpanded && g.Sizing == GroupSizing.Fill).ToArray();
            weightPerPixel = fills.Sum(g => g.Weight) / Math.Max(1, fills.Sum(g => g.Row.ActualHeight));
        }

        internal bool Resize(double change)
        {
            if (activeUpper is not { } upper || !upper.Card.IsExpanded || !lower.Card.IsExpanded) return false;
            double total = upperHeight + lowerHeight;
            double upperMin = Math.Max(96, upper.Row.MinHeight);
            double lowerMin = Math.Max(96, lower.Row.MinHeight);
            if (total < upperMin + lowerMin) return false;
            double next = Math.Clamp(upperHeight + change, upperMin, total - lowerMin);
            SetHeight(upper, next);
            SetHeight(lower, total - next);
            return true;
        }

        void SetHeight(Group group, double height)
        {
            if (group.Sizing == GroupSizing.Fill)
            {
                group.Weight = Math.Max(.001, height * weightPerPixel);
                group.Row.Height = new GridLength(group.Weight, GridUnitType.Star);
            }
            else
            {
                group.UserHeight = height;
                group.Row.Height = new GridLength(height);
            }
        }
    }
}