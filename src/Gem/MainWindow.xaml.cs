using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Gem.Services;
using Gem.ViewModels;
using Wpf.Ui.Controls;

namespace Gem;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly INotificationService? _notifications;
    private int _transitionGeneration;
    private bool _allowClose;
    private bool _hasShownTrayHint;
    private bool _startMinimizedToTray;

    public MainWindow(MainViewModel viewModel, INotificationService? notifications = null)
    {
        _viewModel = viewModel;
        _notifications = notifications;
        DataContext = _viewModel;

        InitializeComponent();
        _viewModel.ThemeTransitionRunner = RunThemeTransition;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;

        if (_notifications is not null)
            _notifications.ExitRequested += OnTrayExitRequested;

        if (Application.Current is not null)
            Application.Current.SessionEnding += OnApplicationSessionEnding;
    }

    public void PrepareStartMinimized(bool minimizeToTray)
    {
        if (minimizeToTray)
        {
            _startMinimizedToTray = true;
            _notifications?.EnsureTrayIcon();
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        else
        {
            _startMinimizedToTray = false;
            WindowState = WindowState.Minimized;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startMinimizedToTray)
        {
            _startMinimizedToTray = false;
            Hide();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
        }

        await _viewModel.InitializeAsync(this);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_viewModel.ShouldMinimizeToTrayOnClose)
            return;

        e.Cancel = true;
        _notifications?.EnsureTrayIcon();
        Hide();

        if (!_hasShownTrayHint)
        {
            _hasShownTrayHint = true;
            var loc = LocalizationService.Instance;
            _notifications?.Show(loc["TrayMinimizedTitle"], loc["TrayMinimizedMessage"]);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (_notifications is not null)
            _notifications.ExitRequested -= OnTrayExitRequested;

        if (Application.Current is not null)
            Application.Current.SessionEnding -= OnApplicationSessionEnding;
    }

    private void OnApplicationSessionEnding(object? sender, SessionEndingCancelEventArgs e)
    {
        _allowClose = true;
    }

    private void OnTrayExitRequested()
    {
        _allowClose = true;
        Dispatcher.BeginInvoke(Close);
    }

    private void RunThemeTransition(Action applyThemeAction)
    {
        int generation = ++_transitionGeneration;

        double width = RootContentGrid.ActualWidth;
        double height = RootContentGrid.ActualHeight;

        if (width <= 1 || height <= 1)
        {
            applyThemeAction();
            AnimateThemeIcon();
            return;
        }

        try
        {
            // Clear any previous wave before capturing snapshot
            ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
            ThemeTransitionOverlay.Clip = null;
            ThemeTransitionOverlay.Source = null;
            ThemeWaveRingPath.Visibility = Visibility.Collapsed;
            ThemeWaveRingPath.Data = null;

            bool wasDark = _viewModel.IsDarkTheme;
            var dpi = VisualTreeHelper.GetDpi(RootContentGrid);
            int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
            int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));

            var oldBackdropColor = wasDark
                ? Color.FromRgb(0x20, 0x20, 0x20)
                : Color.FromRgb(0xF3, 0xF3, 0xF3);
            var oldBackdropBrush = new SolidColorBrush(oldBackdropColor);
            oldBackdropBrush.Freeze();

            var bgVisual = new DrawingVisual();
            using (var dc = bgVisual.RenderOpen())
            {
                dc.DrawRectangle(oldBackdropBrush, null, new Rect(0, 0, width, height));
            }

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 96.0 * dpi.DpiScaleX, 96.0 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(bgVisual);
            rtb.Render(RootContentGrid);
            rtb.Freeze();

            // Step 1: Cover the entire window with the opaque old-theme snapshot first (no clip yet)
            ThemeTransitionOverlay.Source = rtb;
            ThemeTransitionOverlay.Opacity = 1.0;
            ThemeTransitionOverlay.Clip = null;
            ThemeTransitionOverlay.Visibility = Visibility.Visible;

            // Step 2: After Render commits the snapshot on screen, switch the theme invisibly underneath
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (generation != _transitionGeneration)
                    return;

                applyThemeAction();

                // Step 3: After the new theme finishes DataBind + Render, start the circular wave reveal
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (generation != _transitionGeneration)
                        return;

                    StartWaveReveal(width, height, generation);
                });
            });
        }
        catch
        {
            CleanupThemeOverlay();
            applyThemeAction();
            AnimateThemeIcon();
        }
    }

    private void StartWaveReveal(double width, double height, int generation)
    {
        Point origin = new(width - 160, 24);
        if (ThemeToggleButton.IsVisible && ThemeToggleButton.ActualWidth > 0)
        {
            origin = ThemeToggleButton.TranslatePoint(
                new Point(ThemeToggleButton.ActualWidth / 2.0, ThemeToggleButton.ActualHeight / 2.0),
                RootContentGrid);
        }

        double dx = Math.Max(origin.X, width - origin.X);
        double dy = Math.Max(origin.Y, height - origin.Y);
        double maxRadius = Math.Sqrt(dx * dx + dy * dy) + 24.0;

        var fullRect = new RectangleGeometry(new Rect(0, 0, width, height));
        var waveHole = new EllipseGeometry(origin, 0, 0);
        var invertedClip = new CombinedGeometry(GeometryCombineMode.Exclude, fullRect, waveHole);

        ThemeTransitionOverlay.Clip = invertedClip;

        bool nowDark = _viewModel.IsDarkTheme;
        Color waveColor = nowDark
            ? Color.FromRgb(0x18, 0x18, 0x1B)
            : Color.FromRgb(0xF7, 0xF9, 0xFC);

        var waveStroke = new SolidColorBrush(waveColor);
        waveStroke.Freeze();

        ThemeWaveRingPath.Data = waveHole;
        ThemeWaveRingPath.Stroke = waveStroke;
        ThemeWaveRingPath.StrokeThickness = 8;
        ThemeWaveRingPath.Opacity = 0.75;
        ThemeWaveRingPath.Visibility = Visibility.Visible;

        AnimateThemeIcon();

        var duration = TimeSpan.FromMilliseconds(460);
        var waveEase = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var radiusAnimX = new DoubleAnimation(0, maxRadius, duration) { EasingFunction = waveEase };
        var radiusAnimY = new DoubleAnimation(0, maxRadius, duration) { EasingFunction = waveEase };
        var ringFade = new DoubleAnimation(0.75, 0.0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };

        radiusAnimX.Completed += (_, _) =>
        {
            if (generation != _transitionGeneration)
                return;

            CleanupThemeOverlay();
        };

        waveHole.BeginAnimation(EllipseGeometry.RadiusXProperty, radiusAnimX);
        waveHole.BeginAnimation(EllipseGeometry.RadiusYProperty, radiusAnimY);
        ThemeWaveRingPath.BeginAnimation(OpacityProperty, ringFade);
    }

    private void CleanupThemeOverlay()
    {
        ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
        ThemeTransitionOverlay.Clip = null;
        ThemeTransitionOverlay.Source = null;

        ThemeWaveRingPath.Visibility = Visibility.Collapsed;
        ThemeWaveRingPath.Data = null;
    }

    private void AnimateThemeIcon()
    {
        var rotateAnim = new DoubleAnimation(-140, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var scaleEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        var scaleXAnim = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(380)) { EasingFunction = scaleEase };
        var scaleYAnim = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(380)) { EasingFunction = scaleEase };

        ThemeIconRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim);
        ThemeIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        ThemeIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files is not null && files.Any(f => f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)))
            {
                e.Effects = DragDropEffects.Copy;
                DragDropOverlay.Visibility = Visibility.Visible;
                e.Handled = true;
                return;
            }
        }

        e.Effects = DragDropEffects.None;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DragDropOverlay.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await _viewModel.HandleDroppedFilesAsync(files);
        }
    }
}
