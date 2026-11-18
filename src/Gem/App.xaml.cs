using System.Windows;
using Gem.Helpers;
using Gem.Services;
using Gem.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Gem;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FluentMotion.RegisterGlobalButtonMotion();
        SmoothScrollHelper.RegisterGlobalSmoothScrolling();

        var services = new ServiceCollection();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<LocalizationService>(_ => LocalizationService.Instance);
        services.AddSingleton<ThemeService>();
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<IUpdateService>(sp => new GitHubUpdateService(sp.GetRequiredService<SettingsService>()));
        services.AddSingleton<ITorrentEngineService, LibtorrentEngineService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var initialSettings = _serviceProvider.GetRequiredService<SettingsService>().LoadSettings();
        LocalizationService.Instance.SetLanguage(initialSettings.Language);
        _serviceProvider.GetRequiredService<ThemeService>().ApplyFixedAccent();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();

        if (e.Args.Length > 0)
        {
            var vm = _serviceProvider.GetRequiredService<MainViewModel>();
            _ = HandleStartupArgsAsync(vm, e.Args);
        }
    }

    private static async Task HandleStartupArgsAsync(MainViewModel vm, string[] args)
    {
        await Task.Delay(500);
        await vm.HandleDroppedFilesAsync(args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
