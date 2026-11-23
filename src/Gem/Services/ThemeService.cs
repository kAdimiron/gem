using System.Windows;
using System.Windows.Media;
using Gem.Models;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Gem.Services;

public sealed class ThemeService
{
    // Fixed Gem Mint brand defaults (#018574 download / #109E4E seeding) independent of Windows personalization.
    public static readonly Color FixedAccentColor = Color.FromRgb(0x01, 0x85, 0x74);
    public static readonly Color DefaultSeedingColor = Color.FromRgb(0x10, 0x9E, 0x4E);

    private bool _eventsSubscribed;
    private bool _isApplyingAccent;

    public AppThemeMode CurrentMode { get; private set; } = AppThemeMode.System;

    public AppColorTheme CurrentColorTheme { get; private set; } = AppColorTheme.Mint;

    public bool IsDarkActive => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public Color CurrentAccentColor { get; private set; } = FixedAccentColor;

    public Color CurrentSeedingAccentColor { get; private set; } = DefaultSeedingColor;

    public event Action? ThemeChanged;

    public static (Color DownloadColor, Color SeedingColor) GetPalette(AppColorTheme colorTheme) => colorTheme switch
    {
        AppColorTheme.Tomato => (Color.FromRgb(0xDC, 0x3E, 0x26), Color.FromRgb(0xEA, 0x73, 0x17)),
        AppColorTheme.Purple => (Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x5B, 0x21, 0xB6)),
        AppColorTheme.Sky => (Color.FromRgb(0x8D, 0xBC, 0xD8), Color.FromRgb(0x1D, 0x6F, 0x96)),
        AppColorTheme.Mocha => (Color.FromRgb(0x8B, 0x5E, 0x3C), Color.FromRgb(0x5D, 0x3A, 0x1A)),
        AppColorTheme.Indigo => (Color.FromRgb(0x4F, 0x46, 0xE5), Color.FromRgb(0x0E, 0xA5, 0xE9)),
        AppColorTheme.Coral => (Color.FromRgb(0xFF, 0x6F, 0x61), Color.FromRgb(0xC2, 0x18, 0x5B)),
        AppColorTheme.Sakura => (Color.FromRgb(0xF4, 0x8F, 0xB1), Color.FromRgb(0xAD, 0x14, 0x57)),
        AppColorTheme.Cobalt => (Color.FromRgb(0x25, 0x63, 0xEB), Color.FromRgb(0x1E, 0x40, 0xAF)),
        AppColorTheme.Cyan => (Color.FromRgb(0x08, 0x91, 0xB2), Color.FromRgb(0x0E, 0x74, 0x90)),
        AppColorTheme.Amber => (Color.FromRgb(0xD9, 0x77, 0x06), Color.FromRgb(0xB4, 0x53, 0x09)),
        AppColorTheme.Rose => (Color.FromRgb(0xE1, 0x1D, 0x48), Color.FromRgb(0x9F, 0x12, 0x39)),
        AppColorTheme.Orchid => (Color.FromRgb(0xC0, 0x26, 0xD3), Color.FromRgb(0x86, 0x19, 0x8F)),
        AppColorTheme.Forest => (Color.FromRgb(0x16, 0xA3, 0x4A), Color.FromRgb(0x15, 0x80, 0x3D)),
        AppColorTheme.Gold => (Color.FromRgb(0xEA, 0xB3, 0x08), Color.FromRgb(0xA1, 0x62, 0x07)),
        AppColorTheme.Slate => (Color.FromRgb(0x64, 0x74, 0x8B), Color.FromRgb(0x33, 0x41, 0x55)),
        _ => (FixedAccentColor, DefaultSeedingColor)
    };

    public void ApplyTheme(AppThemeMode mode, Window? window = null) =>
        ApplyTheme(mode, CurrentColorTheme, window);

    public void ApplyTheme(AppThemeMode mode, AppColorTheme colorTheme, Window? window = null)
    {
        CurrentMode = mode;
        CurrentColorTheme = colorTheme;
        EnsureThemeEventsSubscribed();

        switch (mode)
        {
            case AppThemeMode.Dark:
                if (window is not null)
                    SystemThemeWatcher.UnWatch(window);
                if (ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Dark)
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, false);
                ApplyFixedAccent(ApplicationTheme.Dark, colorTheme);
                break;

            case AppThemeMode.Light:
                if (window is not null)
                    SystemThemeWatcher.UnWatch(window);
                if (ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light)
                    ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, false);
                ApplyFixedAccent(ApplicationTheme.Light, colorTheme);
                break;

            default:
                var systemTheme = ApplicationThemeManager.IsMatchedDark()
                    ? ApplicationTheme.Dark
                    : ApplicationTheme.Light;
                if (ApplicationThemeManager.GetAppTheme() != systemTheme)
                    ApplicationThemeManager.Apply(systemTheme, WindowBackdropType.Mica, false);
                if (window is not null)
                {
                    SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, false);
                }
                ApplyFixedAccent(systemTheme, colorTheme);
                break;
        }
    }

    public AppThemeMode ToggleLightDark(Window? window = null)
    {
        var next = IsDarkActive ? AppThemeMode.Light : AppThemeMode.Dark;
        ApplyTheme(next, CurrentColorTheme, window);
        return next;
    }

    public void ApplyFixedAccent(ApplicationTheme? activeTheme = null, AppColorTheme? colorTheme = null)
    {
        if (_isApplyingAccent)
            return;

        _isApplyingAccent = true;
        try
        {
            ApplicationTheme resolvedTheme = activeTheme ?? ApplicationThemeManager.GetAppTheme();
            if (resolvedTheme == ApplicationTheme.Unknown)
                resolvedTheme = ApplicationTheme.Dark;

            AppColorTheme resolvedPalette = colorTheme ?? CurrentColorTheme;
            CurrentColorTheme = resolvedPalette;

            (Color accent, Color seedingAccent) = GetPalette(resolvedPalette);
            CurrentAccentColor = accent;
            CurrentSeedingAccentColor = seedingAccent;

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
            var lightAccentTextBrush = resolvedTheme == ApplicationTheme.Light && IsColorLight(accent)
                ? CreateAdjustedBrush(accent, -0.38)
                : accentBrush;

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
            Application.Current.Resources["AccentTextFillColorPrimaryBrush"] = resolvedTheme == ApplicationTheme.Dark ? tertiaryBrush : lightAccentTextBrush;
            Application.Current.Resources["AccentTextFillColorSecondaryBrush"] = secondaryBrush;
            Application.Current.Resources["AccentTextFillColorTertiaryBrush"] = lightAccentTextBrush;

            var seedingBrush = new SolidColorBrush(seedingAccent);
            seedingBrush.Freeze();
            var seedingTextBrush = resolvedTheme == ApplicationTheme.Dark
                ? CreateAdjustedBrush(seedingAccent, 0.16)
                : (IsColorLight(seedingAccent) ? CreateAdjustedBrush(seedingAccent, -0.35) : seedingBrush);
            var seedingTintBrush = new SolidColorBrush(Color.FromArgb(0x2A, seedingAccent.R, seedingAccent.G, seedingAccent.B));
            seedingTintBrush.Freeze();

            Application.Current.Resources["GemSeedingColor"] = seedingAccent;
            Application.Current.Resources["GemSeedingBrush"] = seedingBrush;
            Application.Current.Resources["GemSeedingTextBrush"] = seedingTextBrush;
            Application.Current.Resources["GemSeedingTintBrush"] = seedingTintBrush;

            var onAccentForeground = IsColorLight(accent) ? Colors.Black : Colors.White;
            var fgBrush = new SolidColorBrush(onAccentForeground);
            fgBrush.Freeze();
            var disabledTint = new SolidColorBrush(Color.FromArgb(0x66, accent.R, accent.G, accent.B));
            disabledTint.Freeze();

            Application.Current.Resources["SystemAccentColorBrush"] = accentBrush;
            Application.Current.Resources["AccentFillColorDefault"] = accentBrush.Color;
            Application.Current.Resources["AccentFillColorSecondary"] = secondaryBrush.Color;
            Application.Current.Resources["AccentFillColorTertiary"] = tertiaryBrush.Color;
            Application.Current.Resources["AccentFillColorSelectedTextBackgroundBrush"] = accentBrush;
            Application.Current.Resources["TextOnAccentFillColorPrimary"] = onAccentForeground;
            Application.Current.Resources["TextOnAccentFillColorSecondary"] = onAccentForeground;
            Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"] = fgBrush;
            Application.Current.Resources["TextOnAccentFillColorSecondaryBrush"] = fgBrush;
            Application.Current.Resources["AccentButtonForeground"] = fgBrush;
            Application.Current.Resources["AccentButtonForegroundPointerOver"] = fgBrush;
            Application.Current.Resources["AccentButtonForegroundPressed"] = fgBrush;
            Application.Current.Resources["AccentButtonBackground"] = accentBrush;
            Application.Current.Resources["AccentButtonBackgroundPointerOver"] = secondaryBrush;
            Application.Current.Resources["AccentButtonBackgroundPressed"] = tertiaryBrush;
            Application.Current.Resources["AccentButtonBorderBrush"] = accentBrush;
            Application.Current.Resources["AccentButtonBorderBrushPointerOver"] = secondaryBrush;
            Application.Current.Resources["AccentButtonBorderBrushPressed"] = tertiaryBrush;

            // Update control-specific accent brushes cached by Wpf.Ui ThemesDictionary
            Application.Current.Resources["ToggleSwitchFillOn"] = accentBrush;
            Application.Current.Resources["ToggleSwitchFillOnPointerOver"] = secondaryBrush;
            Application.Current.Resources["ToggleSwitchFillOnPressed"] = tertiaryBrush;
            Application.Current.Resources["ToggleSwitchFillOnDisabled"] = disabledTint;
            Application.Current.Resources["ToggleSwitchStrokeOn"] = accentBrush;
            Application.Current.Resources["ToggleSwitchStrokeOnPointerOver"] = secondaryBrush;
            Application.Current.Resources["ToggleSwitchStrokeOnPressed"] = tertiaryBrush;
            Application.Current.Resources["ToggleSwitchStrokeOnDisabled"] = disabledTint;
            Application.Current.Resources["ToggleSwitchKnobFillOn"] = fgBrush;
            Application.Current.Resources["ToggleSwitchKnobFillOnPointerOver"] = fgBrush;
            Application.Current.Resources["ToggleSwitchKnobFillOnPressed"] = fgBrush;
            Application.Current.Resources["ToggleSwitchKnobFillOnDisabled"] = fgBrush;

            Application.Current.Resources["CheckBoxCheckBackgroundFillChecked"] = accentBrush;
            Application.Current.Resources["CheckBoxCheckBackgroundFillCheckedPointerOver"] = secondaryBrush;
            Application.Current.Resources["CheckBoxCheckBackgroundFillCheckedPressed"] = tertiaryBrush;
            Application.Current.Resources["CheckBoxCheckBorderBrush"] = accentBrush;
        }
        finally
        {
            _isApplyingAccent = false;
        }

        ThemeChanged?.Invoke();
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
