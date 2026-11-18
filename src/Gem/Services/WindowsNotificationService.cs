using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Gem.Services;

public sealed class WindowsNotificationService : INotificationService
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _trayIcon;
    private bool _disposed;

    public event Action? ExitRequested;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void EnsureTrayIcon()
    {
        if (_disposed)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(EnsureNotifyIcon);
            return;
        }

        EnsureNotifyIcon();
    }

    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info)
    {
        if (_disposed)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ShowInternal(title, message, kind));
            return;
        }

        ShowInternal(title, message, kind);
    }

    private void ShowInternal(string title, string message, NotificationKind kind)
    {
        if (_disposed)
            return;

        EnsureNotifyIcon();
        if (_notifyIcon is null)
            return;

        var tipIcon = kind switch
        {
            NotificationKind.Warning => Forms.ToolTipIcon.Warning,
            NotificationKind.Error => Forms.ToolTipIcon.Error,
            _ => Forms.ToolTipIcon.Info
        };

        string safeTitle = string.IsNullOrWhiteSpace(title) ? "Gem" : title;
        string safeMessage = message ?? string.Empty;

        _notifyIcon.ShowBalloonTip(5000, safeTitle, safeMessage, tipIcon);
    }

    private void EnsureNotifyIcon()
    {
        if (_notifyIcon is not null)
            return;

        _trayMenu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem(LocalizationService.Instance["TrayOpenGem"], null, (_, _) => ActivateMainWindow());
        var exitItem = new Forms.ToolStripMenuItem(LocalizationService.Instance["TrayExit"], null, (_, _) => ExitRequested?.Invoke());
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);
        _trayMenu.Opening += (_, _) =>
        {
            openItem.Text = LocalizationService.Instance["TrayOpenGem"];
            exitItem.Text = LocalizationService.Instance["TrayExit"];
        };

        _trayIcon = CreateAppIcon();
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Gem",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _notifyIcon.BalloonTipClicked += (_, _) => ActivateMainWindow();
        _notifyIcon.DoubleClick += (_, _) => ActivateMainWindow();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ActivateMainWindow();
        };
    }

    private static void ActivateMainWindow()
    {
        var window = Application.Current?.MainWindow;
        if (window is null)
            return;

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static Icon CreateAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/Gem.ico", UriKind.Absolute);
            var resourceInfo = Application.GetResourceStream(uri);
            if (resourceInfo?.Stream is not null)
            {
                using var stream = resourceInfo.Stream;
                return new Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            // Fall back to generated icon when pack URI resources are unavailable (e.g. headless tests).
        }

        return CreateFallbackAppIcon();
    }

    private static Icon CreateFallbackAppIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(0x4A, 0x78, 0xA4));
            using var path = new GraphicsPath();
            int r = 7;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(bgBrush, path);

            using var pen = new Pen(Color.White, 2.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawLine(pen, 16, 8, 16, 22);
            g.DrawLine(pen, 11, 17, 16, 22);
            g.DrawLine(pen, 21, 17, 16, 22);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;

        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
