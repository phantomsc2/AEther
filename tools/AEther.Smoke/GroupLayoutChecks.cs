using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AEther.Smoke;
using AEther.WinUI;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static partial class Program
{
    static void CheckGroupLayout()
    {
        Exception? failure = null;
        bool finished = false;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new WindowCheckApplication();
            app.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.Exception);
                failure = e.Exception;
                e.Handled = true;
                app.Exit();
            };
            app.StartChecks = async () =>
            {
                var window = new Window();
                var host = new GroupHost { Width = 800, Height = 800 };
                var cards = new[]
                {
                    host.AddGroup("Monitor", new Border()),
                    host.AddGroup("Settings", new Border { Height = 80 }, GroupSizing.Auto),
                    host.AddGroup("Parameters", new Border()),
                    host.AddGroup("More", new Border()),
                };
                window.Content = host;
                window.Activate();
                async Task Settle()
                {
                    await Task.Delay(180);
                    host.UpdateLayout();
                }
                double Top(FrameworkElement item) => item.TransformToVisual(host).TransformPoint(default).Y;
                void Assert(bool condition, string message)
                {
                    if (!condition) throw new Exception(message);
                }
                void Drag(GridSplitter splitter, double delta)
                {
                    var type = splitter.GetType();
                    type.GetMethod("OnDragStarting", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(splitter, null);
                    type.GetMethod("OnDragVertical", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(splitter, [delta]);
                }
                try
                {
                    // Every combination includes the formerly broken single collapse cases.
                    for (int mask = 0; mask < 16; mask++)
                    {
                        for (int i = 0; i < cards.Length; i++) cards[i].IsExpanded = (mask & (1 << i)) != 0;
                        await Settle();
                        var expanded = cards.Count(c => c.IsExpanded);
                        Assert(host.Children.OfType<GridSplitter>().Count(s => s.Visibility == Visibility.Visible)
                            == Math.Max(0, expanded - 1), $"Wrong splitter count for {mask}.");
                        for (int i = 0; i < cards.Length; i++)
                        {
                            Assert(Math.Abs(cards[i].ActualWidth - host.ActualWidth) < 1, "Group lost full width.");
                            if (!cards[i].IsExpanded) Assert(cards[i].ActualHeight < 64, $"Collapsed group reserves space for {mask}.");
                            if (i > 0) Assert(Math.Abs(Top(cards[i]) - Top(cards[i - 1]) - cards[i - 1].ActualHeight - 8) < 1,
                                $"Gap between groups for {mask}.");
                        }
                        if ((mask & 13) != 0)
                            Assert(Math.Abs(Top(cards[^1]) + cards[^1].ActualHeight - host.ActualHeight) < 1,
                                $"Expanded Fill groups did not consume viewport for {mask}.");
                    }
                    foreach (var card in cards) card.IsExpanded = true;
                    await Settle();
                    double original = cards[0].ActualHeight;
                    Drag(host.Children.OfType<GridSplitter>().First(s => s.Visibility == Visibility.Visible), 20);
                    await Settle();
                    Assert(Math.Abs(cards[0].ActualHeight - original - 20) < 1, "Auto/Fill splitter did not move by requested amount.");
                    double resized = cards[0].ActualHeight;
                    cards[0].IsExpanded = false;
                    await Settle();
                    cards[0].IsExpanded = true;
                    await Settle();
                    Assert(Math.Abs(cards[0].ActualHeight - resized) < 1, "Collapse/reopen lost resized allocation.");
                    cards[1].IsExpanded = false;
                    await Settle();
                    original = cards[0].ActualHeight;
                    Drag(host.Children.OfType<GridSplitter>().First(s => s.Visibility == Visibility.Visible), 30);
                    await Settle();
                    Assert(Math.Abs(cards[0].ActualHeight - original - 30) < 1, "Splitter did not skip collapsed group.");
                    host.Height = 650;
                    host.Width = 440;
                    await Settle();
                    Assert(Math.Abs(Top(cards[^1]) + cards[^1].ActualHeight - host.ActualHeight) < 1, "Resize left unused space.");
                    finished = true;
                }
                catch (Exception error) { Console.Error.WriteLine(error); failure = error; }
                finally { window.Close(); app.Exit(); }
            };
        });
        if (failure != null) throw new Exception("Group layout checks failed.", failure);
        if (!finished) throw new Exception("Group layout checks did not finish.");
        Console.WriteLine("Group layout: all 16 collapse combinations, full width, space redistribution, Auto/Fill and nonadjacent splitters, reopen and resize passed.");
    }
}
