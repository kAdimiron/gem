namespace Gem.Services;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error
}

public interface INotificationService : IDisposable
{
    event Action? ExitRequested
    {
        add { }
        remove { }
    }

    void EnsureTrayIcon() { }

    void Show(string title, string message, NotificationKind kind = NotificationKind.Info);
}
