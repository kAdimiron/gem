using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Gem.Helpers;

public static class FluentMotion
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(FluentMotion),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    public static void RegisterGlobalButtonMotion()
    {
        EventManager.RegisterClassHandler(
            typeof(Wpf.Ui.Controls.Button),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is DependencyObject obj && !GetIsEnabled(obj))
                {
                    SetIsEnabled(obj, true);
                }
            }));
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.NewValue)
        {
            element.MouseEnter += OnMouseEnter;
            element.MouseLeave += OnMouseLeave;
            element.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
            element.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
        }
        else
        {
            element.MouseEnter -= OnMouseEnter;
            element.MouseLeave -= OnMouseLeave;
            element.PreviewMouseLeftButtonDown -= OnPreviewMouseDown;
            element.PreviewMouseLeftButtonUp -= OnPreviewMouseUp;
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el && el.IsEnabled)
            AnimateScale(el, 1.04, 120);
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement el)
            AnimateScale(el, 1.0, 140);
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.IsEnabled)
            AnimateScale(el, 0.94, 65);
    }

    private static void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.IsEnabled)
            AnimateScale(el, el.IsMouseOver ? 1.04 : 1.0, 110);
    }

    private static void AnimateScale(FrameworkElement element, double targetScale, int durationMs)
    {
        var scale = EnsureScaleTransform(element);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(durationMs);

        var animX = new DoubleAnimation(targetScale, duration) { EasingFunction = ease };
        var animY = new DoubleAnimation(targetScale, duration) { EasingFunction = ease };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animX, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animY, HandoffBehavior.SnapshotAndReplace);
    }

    private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
    {
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        if (element.RenderTransform is ScaleTransform existing && !existing.IsFrozen)
            return existing;

        var scale = new ScaleTransform(1.0, 1.0);
        element.RenderTransform = scale;
        return scale;
    }
}
