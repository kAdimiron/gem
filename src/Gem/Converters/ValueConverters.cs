using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Gem.Helpers;
using Gem.Models;

namespace Gem.Converters;

public sealed class BytesToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            long l => FormatHelper.FormatBytes(l),
            int i => FormatHelper.FormatBytes(i),
            double d => FormatHelper.FormatBytes((long)d),
            _ => FormatHelper.FormatBytes(0)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SpeedToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            long l => FormatHelper.FormatSpeed(l),
            int i => FormatHelper.FormatSpeed(i),
            double d => FormatHelper.FormatSpeed((long)d),
            _ => FormatHelper.FormatSpeed(0)
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class EtaToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is TimeSpan ts
            ? FormatHelper.FormatEta(ts)
            : "∞";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusKindToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DownloadingBrush = CreateFrozen("#0078D4");
    private static readonly SolidColorBrush SeedingBrush = CreateFrozen("#7C3AED");
    private static readonly SolidColorBrush PausedBrush = CreateFrozen("#9D5D00");
    private static readonly SolidColorBrush CheckingBrush = CreateFrozen("#8764B8");
    private static readonly SolidColorBrush MetadataBrush = CreateFrozen("#038387");
    private static readonly SolidColorBrush ErrorBrush = CreateFrozen("#C42B1C");
    private static readonly SolidColorBrush StoppedBrush = CreateFrozen("#7A7A7A");

    private static SolidColorBrush CreateFrozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TorrentStatusKind kind)
            return StoppedBrush;

        if (kind == TorrentStatusKind.Downloading &&
            Application.Current?.TryFindResource("SystemAccentColorPrimaryBrush") is SolidColorBrush accentBrush)
        {
            return accentBrush;
        }

        return kind switch
        {
            TorrentStatusKind.Downloading => DownloadingBrush,
            TorrentStatusKind.Seeding => SeedingBrush,
            TorrentStatusKind.Paused => PausedBrush,
            TorrentStatusKind.Checking => CheckingBrush,
            TorrentStatusKind.Metadata => MetadataBrush,
            TorrentStatusKind.Error => ErrorBrush,
            _ => StoppedBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value switch
        {
            bool b => b,
            int i => i > 0,
            string s => !string.IsNullOrWhiteSpace(s),
            null => false,
            _ => true
        };

        if (Invert)
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNotNull = value is not null;
        if (Invert)
            isNotNull = !isNotNull;

        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
