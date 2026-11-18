using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Gem.Helpers;

/// <summary>
/// Provides monitor-refresh-synced exponential smoothing for mouse wheel and touchpad scrolling
/// across all pixel-based <see cref="ScrollViewer"/> controls in the application.
/// </summary>
public static class SmoothScrollHelper
{
    private const double PixelsPerLine = 32.0;
    private const double SmoothingRate = 11.5;
    private const double SnapThreshold = 0.25;

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollState> States = new();
    private static readonly HashSet<ScrollViewer> ActiveScrollViewers = [];
    private static bool _isRegistered;
    private static bool _isRenderingHooked;
    private static long _lastTimestamp;

    public static void RegisterGlobalSmoothScrolling()
    {
        if (_isRegistered)
            return;

        _isRegistered = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel),
            handledEventsToo: false);
    }

    internal static double ComputeScrollDelta(int wheelDelta, int wheelScrollLines, double viewportSize)
    {
        if (wheelDelta == 0)
            return 0;

        double notches = wheelDelta / 120.0;
        double stepPerNotch = wheelScrollLines < 0
            ? Math.Max(48.0, viewportSize * 0.85)
            : Math.Max(1, wheelScrollLines) * PixelsPerLine;

        // WheelDelta > 0 means scrolling up/left (decreasing offset).
        return -notches * stepPerNotch;
    }

    internal static (double NextOffset, bool Finished) StepExponentialSmoothing(
        double currentOffset,
        double targetOffset,
        double dtSeconds,
        double rate = SmoothingRate)
    {
        double clampedDt = Math.Clamp(dtSeconds, 0.001, 0.05);
        double alpha = 1.0 - Math.Exp(-clampedDt * rate);
        double next = currentOffset + (targetOffset - currentOffset) * alpha;

        if (Math.Abs(targetOffset - next) <= SnapThreshold)
            return (targetOffset, true);

        return (next, false);
    }

    internal static double ClampOffset(double offset, double scrollableSize) =>
        scrollableSize <= 0 ? 0 : Math.Clamp(offset, 0, scrollableSize);

    private static void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer sv)
            return;

        bool horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && sv.ScrollableWidth > 0;

        // Let only the innermost ScrollViewer that can still scroll in this direction handle the wheel event.
        var target = FindTargetScrollViewer(e.OriginalSource as DependencyObject, e.Delta, horizontal);
        if (!ReferenceEquals(target, sv))
            return;

        double scrollable = horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
        if (scrollable <= 0 || sv.CanContentScroll)
            return;

        var state = States.GetOrCreateValue(sv);
        var axis = horizontal ? state.Horizontal : state.Vertical;
        double currentActual = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;

        if (!axis.IsAnimating || IsExternallyScrolled(axis, currentActual))
        {
            axis.Current = currentActual;
            axis.Target = currentActual;
            axis.PreviousApplied = currentActual;
            axis.LastApplied = currentActual;
        }

        double viewport = horizontal ? sv.ViewportWidth : sv.ViewportHeight;
        double delta = ComputeScrollDelta(e.Delta, SystemParameters.WheelScrollLines, viewport);
        double nextTarget = ClampOffset(axis.Target + delta, scrollable);
        if (Math.Abs(nextTarget - axis.Current) < 0.01 && !axis.IsAnimating)
            return;

        axis.Target = nextTarget;
        axis.IsAnimating = true;

        e.Handled = true;
        EnsureRenderingHooked(sv);
    }

    private static bool IsExternallyScrolled(ScrollAxisState axis, double actualOffset) =>
        Math.Abs(actualOffset - axis.LastApplied) > 2.5 &&
        Math.Abs(actualOffset - axis.PreviousApplied) > 2.5;

    private static ScrollViewer? FindTargetScrollViewer(DependencyObject? origin, int wheelDelta, bool horizontal)
    {
        var current = origin;
        while (current is not null)
        {
            if (current is ScrollViewer candidate && candidate.IsEnabled && !candidate.CanContentScroll)
            {
                double scrollable = horizontal ? candidate.ScrollableWidth : candidate.ScrollableHeight;
                if (scrollable > 0.5)
                {
                    double effectiveOffset = GetEffectiveOffset(candidate, horizontal);
                    bool canScroll = wheelDelta > 0
                        ? effectiveOffset > 0.25
                        : effectiveOffset < scrollable - 0.25;

                    if (canScroll)
                        return candidate;
                }
            }

            current = GetVisualOrLogicalParent(current);
        }

        return null;
    }

    private static double GetEffectiveOffset(ScrollViewer sv, bool horizontal)
    {
        if (States.TryGetValue(sv, out var state))
        {
            var axis = horizontal ? state.Horizontal : state.Vertical;
            if (axis.IsAnimating)
                return axis.Target;
        }

        return horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject element)
    {
        if (element is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(element);
            if (visualParent is not null)
                return visualParent;
        }

        return LogicalTreeHelper.GetParent(element);
    }

    private static void EnsureRenderingHooked(ScrollViewer sv)
    {
        ActiveScrollViewers.Add(sv);
        if (_isRenderingHooked)
            return;

        _lastTimestamp = Stopwatch.GetTimestamp();
        _isRenderingHooked = true;
        CompositionTarget.Rendering += OnCompositionTargetRendering;
    }

    private static void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        _lastTimestamp = now;

        List<ScrollViewer>? completed = null;

        foreach (var sv in ActiveScrollViewers)
        {
            if (!sv.IsLoaded || !sv.IsVisible || !States.TryGetValue(sv, out var state))
            {
                (completed ??= []).Add(sv);
                continue;
            }

            StepAxis(sv, state.Vertical, sv.VerticalOffset, sv.ScrollableHeight, dt, horizontal: false);
            StepAxis(sv, state.Horizontal, sv.HorizontalOffset, sv.ScrollableWidth, dt, horizontal: true);

            if (!state.Vertical.IsAnimating && !state.Horizontal.IsAnimating)
                (completed ??= []).Add(sv);
        }

        if (completed is not null)
        {
            foreach (var sv in completed)
                ActiveScrollViewers.Remove(sv);
        }

        if (ActiveScrollViewers.Count == 0)
        {
            CompositionTarget.Rendering -= OnCompositionTargetRendering;
            _isRenderingHooked = false;
        }
    }

    private static void StepAxis(
        ScrollViewer sv,
        ScrollAxisState axis,
        double actualOffset,
        double maxScrollable,
        double dt,
        bool horizontal)
    {
        if (!axis.IsAnimating)
            return;

        if (IsExternallyScrolled(axis, actualOffset))
        {
            axis.IsAnimating = false;
            return;
        }

        double target = ClampOffset(axis.Target, maxScrollable);
        axis.Target = target;
        (double next, bool done) = StepExponentialSmoothing(axis.Current, target, dt);
        next = ClampOffset(next, maxScrollable);

        axis.PreviousApplied = axis.LastApplied;
        axis.Current = next;
        axis.LastApplied = next;

        if (horizontal)
            sv.ScrollToHorizontalOffset(next);
        else
            sv.ScrollToVerticalOffset(next);

        if (done)
            axis.IsAnimating = false;
    }

    private sealed class ScrollAxisState
    {
        public double Current { get; set; }
        public double Target { get; set; }
        public double PreviousApplied { get; set; }
        public double LastApplied { get; set; }
        public bool IsAnimating { get; set; }
    }

    private sealed class ScrollState
    {
        public ScrollAxisState Vertical { get; } = new();
        public ScrollAxisState Horizontal { get; } = new();
    }
}
