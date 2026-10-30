using AEther.NAudio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Serilog;

namespace AEther.WinUI;

public sealed class SettingsPanel : UserControl
{
    public const string Loopback = "System audio (loopback)";
    readonly ComboBox input = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox scenePicker = new() { Name = "Scene", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ShaderPanel shaders;
    bool refreshingScenes;
    internal Grid Columns { get; } = new() { Padding = new Thickness(12), ColumnSpacing = 4, RowSpacing = 4 };
    internal Grid AudioColumn { get; } = new() { RowSpacing = 8 };
    internal Grid SceneColumn { get; } = new() { RowSpacing = 8 };
    internal ScrollViewer AudioSettings { get; }
    internal EventLogView EventLog { get; } = new();
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public event EventHandler? InputChanged;
    public event Action<string>? SceneSelected;

    public SettingsPanel(AudioPanel audio, GraphicsPanel graphics, ShaderPanel shaders, AnalysisView analysis, FrameTimeView backlog,
        double previewAspectRatio)
    {
        this.shaders = shaders;
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(Columns);
        Grid.SetRow(EventLog, 1);
        layout.Children.Add(EventLog);
        Content = layout;
        SizeChanged += (_, _) => EventLog.SetAvailableHeight(ActualHeight);
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(384) });
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var previewRow = new RowDefinition { Height = new GridLength(384 / previewAspectRatio), MinHeight = 32 };
        Columns.RowDefinitions.Add(previewRow); // 0: live views
        Columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) }); // 1: grip
        var controlsRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        Columns.RowDefinitions.Add(controlsRow); // 2: controls

        var defaultPreviewSize = true;
        var updatingPreviewSize = false;
        void UpdatePreviewSize()
        {
            if (!defaultPreviewSize || analysis.ActualWidth <= 0) return;
            updatingPreviewSize = true;
            try { previewRow.Height = new GridLength(analysis.ActualWidth / previewAspectRatio); }
            finally { updatingPreviewSize = false; }
        }
        // Follow column resizing until the user explicitly chooses a row height.
        previewRow.RegisterPropertyChangedCallback(RowDefinition.HeightProperty, (_, _) =>
        {
            if (!updatingPreviewSize) defaultPreviewSize = false;
        });
        analysis.SizeChanged += (_, _) => UpdatePreviewSize();

        var gripIcon = new FontIcon { Glyph = "\uE81D", FontSize = 16, Opacity = 0.55 };
        void AddCrosshairLine(int row, int column, bool horizontal)
        {
            var hitArea = new ResizeArea(horizontal ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast)
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            var line = new Border
            {
                IsHitTestVisible = false, Opacity = 0.2,
                HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
                VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch,
                Width = horizontal ? double.NaN : 1,
                Height = horizontal ? 1 : double.NaN,
            };
            line.SetBinding(Border.BackgroundProperty, new Binding { Source = gripIcon, Path = new PropertyPath(nameof(FontIcon.Foreground)) });
            hitArea.Children.Add(line);
            Windows.Foundation.Point? start = null;
            double startWidth = 0, startHeight = 0;
            hitArea.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(hitArea).Properties.IsLeftButtonPressed) return;
                start = e.GetCurrentPoint(Columns).Position;
                startWidth = Columns.ColumnDefinitions[0].ActualWidth;
                startHeight = previewRow.ActualHeight;
                hitArea.CapturePointer(e.Pointer);
                e.Handled = true;
            };
            hitArea.PointerMoved += (_, e) =>
            {
                if (start is not { } origin) return;
                var point = e.GetCurrentPoint(Columns).Position;
                ResizePreview(startWidth + (horizontal ? 0 : point.X - origin.X),
                    startHeight + (horizontal ? point.Y - origin.Y : 0));
            };
            hitArea.PointerReleased += (_, e) => { start = null; hitArea.ReleasePointerCapture(e.Pointer); };
            hitArea.PointerCaptureLost += (_, _) => start = null;
            hitArea.PointerEntered += (_, _) => line.Opacity = 0.45;
            hitArea.PointerExited += (_, _) => line.Opacity = 0.2;
            Grid.SetRow(hitArea, row);
            Grid.SetColumn(hitArea, column);
            Columns.Children.Add(hitArea);
        }
        AddCrosshairLine(1, 0, true);
        AddCrosshairLine(1, 2, true);
        AddCrosshairLine(0, 1, false);
        AddCrosshairLine(2, 1, false);
        var grip = new ResizeGrip
        {
            Width = 32, Height = 32, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = gripIcon,
            Template = (ControlTemplate)XamlReader.Load("""
                <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                    <Border Background="Transparent">
                        <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                </ControlTemplate>
                """),
        };
        grip.PointerEntered += (_, _) => gripIcon.Opacity = 1;
        grip.PointerExited += (_, _) => gripIcon.Opacity = 0.55;
        AutomationProperties.SetName(grip, "Resize spectrogram");
        ToolTipService.SetToolTip(grip, "Drag or use arrow keys to resize. Double-click to reset.");
        void ResizePreview(double width, double height)
        {
            defaultPreviewSize = false;
            var maxWidth = Math.Max(32, Columns.ActualWidth - Columns.Padding.Left - Columns.Padding.Right - 40 - 120);
            var maxHeight = Math.Max(32, Columns.ActualHeight - Columns.Padding.Top - Columns.Padding.Bottom - 40 - 80);
            Columns.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(width, 32, maxWidth));
            previewRow.Height = new GridLength(Math.Clamp(height, 32, maxHeight));
        }
        Windows.Foundation.Point? dragStart = null;
        double startWidth = 0, startHeight = 0;
        grip.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            dragStart = e.GetCurrentPoint(Columns).Position;
            startWidth = Columns.ColumnDefinitions[0].ActualWidth;
            startHeight = previewRow.ActualHeight;
            grip.Focus(FocusState.Pointer);
            grip.CapturePointer(e.Pointer);
        }), true);
        grip.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (dragStart is not { } start) return;
            var point = e.GetCurrentPoint(Columns).Position;
            ResizePreview(startWidth + point.X - start.X, startHeight + point.Y - start.Y);
        }), true);
        grip.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => { dragStart = null; grip.ReleasePointerCapture(e.Pointer); }), true);
        grip.PointerCaptureLost += (_, _) => dragStart = null;
        grip.KeyDown += (_, e) =>
        {
            var (dx, dy) = e.Key switch
            {
                VirtualKey.Left => (-8, 0), VirtualKey.Right => (8, 0),
                VirtualKey.Up => (0, -8), VirtualKey.Down => (0, 8), _ => (0, 0),
            };
            if (dx == 0 && dy == 0) return;
            ResizePreview(Columns.ColumnDefinitions[0].ActualWidth + dx, previewRow.ActualHeight + dy);
            e.Handled = true;
        };
        grip.DoubleTapped += (_, e) =>
        {
            Columns.ColumnDefinitions[0].Width = new GridLength(384);
            defaultPreviewSize = true;
            updatingPreviewSize = true;
            try { previewRow.Height = new GridLength(384 / previewAspectRatio); }
            finally { updatingPreviewSize = false; }
            e.Handled = true;
        };
        Grid.SetColumn(grip, 1);
        Grid.SetRow(grip, 1);
        Columns.Children.Add(grip);

        // Row 0: Audio analysis (left), frame time (right).
        analysis.MinHeight = 32;
        Grid.SetRow(analysis, 0);
        Columns.Children.Add(analysis);
        Grid.SetColumn(backlog, 2);
        Columns.Children.Add(backlog);

        // Row 2: Audio controls (left column)
        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: input
        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: AudioSettings
        AudioSettings = new ScrollViewer
        {
            Content = audio,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var inputRow = LabeledControl.Create("Input", input);
        Grid.SetRow(inputRow, 0); Grid.SetRow(AudioSettings, 1);
        AudioColumn.Children.Add(inputRow);
        AudioColumn.Children.Add(AudioSettings);
        Grid.SetRow(AudioColumn, 2);
        Columns.Children.Add(AudioColumn);

        // Row 2: Video controls (right column)
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: scenePicker
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: graphics
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: shaders
        AutomationProperties.SetName(scenePicker, "Scene");
        Grid.SetRow(graphics, 1);
        var sceneRow = LabeledControl.Create("Scene", scenePicker);
        Grid.SetRow(sceneRow, 0);
        Grid.SetRow(shaders, 2);
        SceneColumn.Children.Add(sceneRow);
        SceneColumn.Children.Add(graphics);
        SceneColumn.Children.Add(shaders);
        Grid.SetRow(SceneColumn, 2);
        Grid.SetColumn(SceneColumn, 2);
        Columns.Children.Add(SceneColumn);
        scenePicker.SelectionChanged += (_, _) =>
        {
            if (!refreshingScenes && scenePicker.SelectedItem is string name) SceneSelected?.Invoke(name);
        };
        input.Items.Add(Loopback);
        try { foreach (var name in Recorder.GetAvailableDeviceNames()) input.Items.Add(name); }
        catch (Exception error) { Log.ForContext<SettingsPanel>().Warning(error, "Could not enumerate audio input devices"); }
        input.SelectedIndex = 0;
        input.SelectionChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetScenes(IEnumerable<string> names, string? selected)
    {
        refreshingScenes = true;
        try
        {
            scenePicker.Items.Clear();
            foreach (var name in names.Order(StringComparer.Ordinal)) scenePicker.Items.Add(name);
            scenePicker.SelectedItem = selected;
            scenePicker.IsEnabled = scenePicker.Items.Count != 0;
            shaders.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { refreshingScenes = false; }
    }
}

sealed class ResizeArea : Grid
{
    public ResizeArea(InputSystemCursorShape cursor) => ProtectedCursor = InputSystemCursor.Create(cursor);
}

sealed class ResizeGrip : Button
{
    public ResizeGrip() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);
}

