using System.Windows;
using System.Windows.Media;
using Gem.Models;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Gem.Services;

public sealed class ThemeService
{
    // Fixed Gem brand accent (#018574) so button and accent colors stay unchanged regardless of Windows personalization.
    public static readonly Color FixedAccentColor = Color.FromRgb(0x01, 0x85, 0x74);

    private bool _eventsSubscribed;
    private bool _isApplyingAccent;

    public AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;

    public bool IsDarkActive => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public Color CurrentAccentColor { get; private set; } = FixedAccentColor;

    public void ApplyTheme(AppThemeMode mode, Window? window = null)
    {
        CurrentMode = mode;
        EnsureThemeEventsSubscribed();

        switch (mode)
        {
            case AppThemeMode.Dark:
                if (window is not null)
                    SystemThemeWatcher.UnWatch(window);
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, false);
                ApplyFixedAccent(ApplicationTheme.Dark);
                break;

            case AppThemeMode.Light:
                if (window is not null)
                    SystemThemeWatcher.UnWatch(window);
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, false);
                ApplyFixedAccent(ApplicationTheme.Light);
                break;

            default:
                var systemTheme = ApplicationThemeManager.IsMatchedDark()
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
                ApplicationThemeManager.Apply(systemTheme, WindowBackdropType.Mica, false);
                if (window is not null)
                {
                    SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, false);
                }
                ApplyFixedAccent(systemTheme);
                break;
        }
    }

    public AppThemeMode ToggleLightDark(Window? window = null)
    {
        var next = IsDarkActive ? AppThemeMode.Light : AppThemeMode.Dark;
        ApplyTheme(next, window);
        return next;
    }

    public void ApplyFixedAccent(ApplicationTheme? activeTheme = null)
    {
        if (_isApplyingAccent)
            return;

        _isApplyingAccent = true;
        try
        {
            ApplicationTheme resolvedTheme = activeTheme ?? ApplicationThemeManager.GetAppTheme();
            if (resolvedTheme == ApplicationTheme.Unknown)
                resolvedTheme = ApplicationTheme.Dark;

            Color accent = FixedAccentColor;
            CurrentAccentColor = accent;

            if (Application.Current is null)
                return;

            ApplicationAccentColorManager.Apply(
                accent,
                resolvedTheme,
                systemGlassColor: false,
                systemAccentColor: false);

            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();

            var secondaryBrush = CreateAdjustedBrush(accent, resolvedTheme == ApplicationTheme.Dark ? 0.12 : -0.10);
            var tertiaryBrush = CreateAdjustedBrush(accent, resolvedTheme == ApplicationTheme.Dark ? 0.20 : -0.18);

            Application.Current.Resources["SystemAccentColor"] = accent;
            Application.Current.Resources["SystemAccentColorPrimary"] = accent;
            Application.Current.Resources["SystemAccentColorSecondary"] = secondaryBrush.Color;
            Application.Current.Resources["SystemAccentColorTertiary"] = tertiaryBrush.Color;
            Application.Current.Resources["SystemAccentColorPrimaryBrush"] = accentBrush;
            Application.Current.Resources["SystemAccentColorSecondaryBrush"] = secondaryBrush;
            Application.Current.Resources["SystemAccentColorTertiaryBrush"] = tertiaryBrush;
            Application.Current.Resources["AccentFillColorDefaultBrush"] = accentBrush;
            Application.Current.Resources["AccentFillColorSecondaryBrush"] = secondaryBrush;
            Application.Current.Resources["AccentFillColorTertiaryBrush"] = tertiaryBrush;
            Application.Current.Resources["AccentTextFillColorPrimaryBrush"] = resolvedTheme == ApplicationTheme.Dark ? tertiaryBrush : accentBrush;
            Application.Current.Resources["AccentTextFillColorSecondaryBrush"] = secondaryBrush;
            Application.Current.Resources["AccentTextFillColorTertiaryBrush"] = accentBrush;

            var onAccentForeground = IsColorLight(accent) ? Colors.Black : Colors.White;
            var fgBrush = new SolidColorBrush(onAccentForeground);
            fgBrush.Freeze();
            Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] = fgBrush;
            Application.Current.Resources["AccentButtonForeground"] = fgBrush;
            Application.Current.Resources["AccentButtonForegroundPointerOver"] = fgBrush;
            Application.Current.Resources["AccentButtonForegroundPressed"] = fgBrush;
            Application.Current.Resources["AccentButtonBackground"] = accentBrush;
            Application.Current.Resources["AccentButtonBackgroundPointerOver"] = secondaryBrush;
            Application.Current.Resources["AccentButtonBackgroundPressed"] = tertiaryBrush;
        }
        finally
        {
            _isApplyingAccent = false;
        }
    }

    private void EnsureThemeEventsSubscribed()
    {
        if (_eventsSubscribed || Application.Current is null)
            return;

        _eventsSubscribed = true;
        ApplicationThemeManager.Changed += OnApplicationThemeChanged;
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
        Application.Current.Exit += OnApplicationExit;
    }

    private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
    {
        ApplyFixedAccent(currentApplicationTheme);
    }

    private void OnSystemUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (Application.Current is null)
            return;

        // Re-apply fixed accent asynchronously on UI thread if Windows personalization colors change.
        Application.Current.Dispatcher.BeginInvoke(() => ApplyFixedAccent(ApplicationThemeManager.GetAppTheme()));
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e)
    {
        if (!_eventsSubscribed)
            return;

        _eventsSubscribed = false;
        ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        if (Application.Current is not null)
            Application.Current.Exit -= OnApplicationExit;
    }

    private static SolidColorBrush CreateAdjustedBrush(Color baseColor, double delta)
    {
        byte Adjust(byte c)
        {
            int v = delta >= 0
                ? (int)(c + (255 - c) * delta)
                : (int)(c * (1.0 + delta));
            return (byte)Math.Clamp(v, 0, 255);
        }

        var brush = new SolidColorBrush(Color.FromRgb(Adjust(baseColor.R), Adjust(baseColor.G), Adjust(baseColor.B)));
        brush.Freeze();
        return brush;
    }

    private static bool IsColorLight(Color c)
    {
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return luminance > 0.65;
    }
}
