using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Gem.Helpers;
using Gem.Models;
using Gem.Views;

namespace Gem.Tests;

public sealed class SmoothScrollHelperTests
{
    [Fact]
    public void ComputeScrollDelta_RespectsDirectionLinesAndPageMode()
    {
        // Wheel up (+120) decreases offset; wheel down (-120) increases offset
        double downStep = SmoothScrollHelper.ComputeScrollDelta(-120, wheelScrollLines: 3, viewportSize: 600);
        double upStep = SmoothScrollHelper.ComputeScrollDelta(120, wheelScrollLines: 3, viewportSize: 600);
        Assert.Equal(96.0, downStep);
        Assert.Equal(-96.0, upStep);

        // High-res touchpad delta scales proportionally
        double smallStep = SmoothScrollHelper.ComputeScrollDelta(-30, wheelScrollLines: 3, viewportSize: 600);
        Assert.Equal(24.0, smallStep);

        // Page-scroll mode (-1) scrolls ~85% of viewport
        double pageStep = SmoothScrollHelper.ComputeScrollDelta(-120, wheelScrollLines: -1, viewportSize: 600);
        Assert.Equal(510.0, pageStep, precision: 3);
    }

    [Fact]
    public void StepExponentialSmoothing_ConvergesAndSnapsToTarget()
    {
        double current = 0;
        double target = 240;
        bool finished = false;

        for (int frame = 0; frame < 60 && !finished; frame++)
        {
            double prev = current;
            (current, finished) = SmoothScrollHelper.StepExponentialSmoothing(current, target, dtSeconds: 1.0 / 60.0);
            Assert.True(current > prev && current <= target);
        }

        Assert.True(finished);
        Assert.Equal(target, current);
    }

    [Fact]
    public void ClampOffset_ClampsBetweenZeroAndScrollableMax()
    {
        Assert.Equal(0, SmoothScrollHelper.ClampOffset(-40, 500));
        Assert.Equal(500, SmoothScrollHelper.ClampOffset(900, 500));
        Assert.Equal(0, SmoothScrollHelper.ClampOffset(100, 0));
    }

    [Fact]
    public void RealWpfControls_WithWpfUiTheme_InterceptMouseWheelAndScrollSmoothly()
    {
        Exception? threadEx = null;
        string diagnostics = string.Empty;

        var thread = new Thread(() =>
        {
            try
            {
                SmoothScrollHelper.RegisterGlobalSmoothScrolling();

                var items = Enumerable.Range(1, 30)
                    .Select(i => new TorrentItemModel { InfoHash = i.ToString(), Name = $"Torrent {i}" })
                    .ToList();

                var lv = new ListView
                {
                    ItemsSource = items,
                    Height = 300,
                    Width = 500
                };
                ScrollViewer.SetCanContentScroll(lv, false);

                var sv = new ScrollViewer
                {
                    Height = 300,
                    Width = 500,
                    Content = new StackPanel { Height = 1200 }
                };

                var panel = new StackPanel();
                panel.Children.Add(sv);
                panel.Children.Add(lv);

                var win = new Window
                {
                    Width = 600,
                    Height = 700,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = false,
                    Left = -10000,
                    Top = -10000,
                    Content = panel
                };
                win.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                win.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                win.Show();
                win.UpdateLayout();

                ScrollViewer? lvSv = FindVisualChild<ScrollViewer>(lv);

                var wheelSv = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = sv.Content
                };
                ((UIElement)sv.Content).RaiseEvent(wheelSv);

                bool lvWheelHandled = false;
                var offsets = new List<double>();
                var lvOffsets = new List<double>();

                if (lvSv is not null)
                {
                    DependencyObject targetEl = FindVisualChild<Border>(lvSv) as DependencyObject ?? lvSv;
                    var wheelLv = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    {
                        RoutedEvent = UIElement.PreviewMouseWheelEvent,
                        Source = targetEl
                    };
                    ((UIElement)targetEl).RaiseEvent(wheelLv);
                    lvWheelHandled = wheelLv.Handled;
                }

                var frame = new DispatcherFrame();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                EventHandler? renderHook = null;
                renderHook = (_, _) =>
                {
                    offsets.Add(Math.Round(sv.VerticalOffset, 2));
                    if (lvSv is not null) lvOffsets.Add(Math.Round(lvSv.VerticalOffset, 2));
                    if (sw.ElapsedMilliseconds >= 450)
                    {
                        CompositionTarget.Rendering -= renderHook;
                        frame.Continue = false;
                    }
                };
                CompositionTarget.Rendering += renderHook;
                Dispatcher.PushFrame(frame);

                diagnostics =
                    $"sv.Handled={wheelSv.Handled} sv.Frames={offsets.Count} sv.Offsets=[{string.Join(",", offsets)}] | " +
                    $"lvSv.Handled={lvWheelHandled} lv.Frames={lvOffsets.Count} lv.Offsets=[{string.Join(",", lvOffsets)}]";

                win.Close();

                Assert.True(wheelSv.Handled, diagnostics);
                Assert.True(lvWheelHandled, diagnostics);
                Assert.True(offsets.Distinct().Count() >= 4, $"Expected progressive smooth offsets for ScrollViewer: {diagnostics}");
                Assert.True(lvOffsets.Distinct().Count() >= 4, $"Expected progressive smooth offsets for ListView: {diagnostics}");
                Assert.True(offsets.Last() > 60, $"Expected ScrollViewer to reach near target offset: {diagnostics}");
                Assert.True(lvOffsets.Last() > 60, $"Expected ListView to reach near target offset: {diagnostics}");
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(threadEx);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }
}
