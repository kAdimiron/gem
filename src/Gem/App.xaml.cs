using System.Windows;
using Gem.Helpers;
using Gem.Services;
using Gem.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Gem;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private SingleInstanceService? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquirePrimary())
        {
            _singleInstance.NotifyPrimaryInstance(e.Args);
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        FluentMotion.RegisterGlobalButtonMotion();
        SmoothScrollHelper.RegisterGlobalSmoothScrolling();

        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LocalizationService>(_ => LocalizationService.Instance);
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IStartupService, WindowsStartupService>();
        services.AddSingleton<ISystemPowerService, WindowsSystemPowerService>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<IUpdateService>(sp => new GitHubUpdateService(sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<ITorrentEngineService, LibtorrentEngineService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var initialSettings = _serviceProvider.GetRequiredService<SettingsService>().LoadSettings();
        LocalizationService.Instance.SetLanguage(initialSettings.Language);
        _serviceProvider.GetRequiredService<ThemeService>().ApplyFixedAccent(colorTheme: initialSettings.ColorTheme);

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        var vm = _serviceProvider.GetRequiredService<MainViewModel>();
        if (initialSettings.StartMinimized)
        {
            mainWindow.PrepareStartMinimized(initialSettings.MinimizeToTrayOnClose);
        }
        mainWindow.Show();

        _singleInstance.StartListening(args => ForwardArgsToRunningWindow(mainWindow, vm, args));

        if (e.Args.Length > 0)
        {
            _ = HandleStartupArgsAsync(vm, e.Args);
        }
    }

    private void ForwardArgsToRunningWindow(MainWindow mainWindow, MainViewModel vm, string[] args)
    {
        if (Dispatcher.HasShutdownStarted)
            return;

        Dispatcher.BeginInvoke(async () =>
        {
            ActivateMainWindow(mainWindow);
            if (args.Length > 0)
            {
                await vm.WaitForInitializedAsync();
                await vm.HandleActivationArgsAsync(args);
            }
        });
    }

    private static void ActivateMainWindow(Window window)
    {
        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    }

    private static async Task HandleStartupArgsAsync(MainViewModel vm, string[] args)
    {
        await vm.WaitForInitializedAsync();
        await vm.HandleActivationArgsAsync(args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        _singleInstance = null;

        if (_serviceProvider is not null)
        {
            var vm = _serviceProvider.GetService<MainViewModel>();
            if (vm is not null)
            {
                try
                {
                    Task.Run(() => vm.ShutdownAsync()).Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            _serviceProvider.GetService<INotificationService>()?.Dispose();
            try
            {
                Task.Run(() => _serviceProvider.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        base.OnExit(e);
    }
}
