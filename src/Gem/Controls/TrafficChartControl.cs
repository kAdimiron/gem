using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Gem.Helpers;
using Gem.Models;

namespace Gem.Controls;

public sealed class TrafficChartControl : FrameworkElement
{
    private IReadOnlyList<DailyTrafficPoint> _days = [];

    private static readonly Color UploadColor = Color.FromRgb(0x10, 0x9E, 0x4E);

    public TrafficChartControl()
    {
        ClipToBounds = true;
    }

    public void UpdateSeries(IReadOnlyList<DailyTrafficPoint> days)
    {
        _days = days;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 40 || h <= 40)
            return;

        double padTop = 18;
        double padBottom = 24;
        double chartH = Math.Max(16, h - padTop - padBottom);

        int count = _days.Count;
        if (count == 0)
            return;

        long maxVal = 1024 * 1024; // minimum 1 MB scale
        foreach (var d in _days)
        {
            if (d.DownloadedBytes > maxVal) maxVal = d.DownloadedBytes;
            if (d.UploadedBytes > maxVal) maxVal = d.UploadedBytes;
        }

        double scaleMax = maxVal * 1.18;

        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)), 1);
        gridPen.Freeze();

        for (int i = 0; i <= 2; i++)
        {
            double y = padTop + (chartH * i / 2.0);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        var labelBrush = Application.Current?.TryFindResource("TextFillColorTertiaryBrush") as Brush
                         ?? Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        var peakText = new FormattedText(
            Services.LocalizationService.Instance.Format("ChartMaxPerDayFormat", FormatHelper.FormatBytes((long)scaleMax)),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            10.5,
            labelBrush,
            dpi);
        dc.DrawText(peakText, new Point(4, 1));

        Color downloadColor = (Application.Current?.TryFindResource("SystemAccentColorPrimaryBrush") as SolidColorBrush)?.Color
                              ?? Color.FromRgb(0x00, 0x78, 0xD4);

        var downBrush = new LinearGradientBrush(
            downloadColor,
            Color.FromArgb(190, downloadColor.R, downloadColor.G, downloadColor.B),
            new Point(0, 0),
            new Point(0, 1));
        downBrush.Freeze();

        var upBrush = new LinearGradientBrush(
            UploadColor,
            Color.FromArgb(190, UploadColor.R, UploadColor.G, UploadColor.B),
            new Point(0, 0),
            new Point(0, 1));
        upBrush.Freeze();

        var baselineBrush = new SolidColorBrush(Color.FromArgb(36, 140, 140, 140));
        baselineBrush.Freeze();

        double slotW = w / count;
        double barW = Math.Clamp(slotW * 0.28, 5, 22);
        double barGap = Math.Clamp(slotW * 0.06, 2, 5);
        double groupW = barW * 2 + barGap;
        double baselineY = padTop + chartH;

        for (int i = 0; i < count; i++)
        {
            var pt = _days[i];
            double groupX = i * slotW + (slotW - groupW) / 2.0;

            // Download bar
            double downH = Math.Clamp(pt.DownloadedBytes / scaleMax, 0, 1) * chartH;
            if (pt.DownloadedBytes > 0)
            {
                double hVal = Math.Max(3.0, downH);
                dc.DrawRoundedRectangle(downBrush, null, new Rect(groupX, baselineY - hVal, barW, hVal), 2.5, 2.5);
            }
            else
            {
                dc.DrawRoundedRectangle(baselineBrush, null, new Rect(groupX, baselineY - 2, barW, 2), 1, 1);
            }

            // Upload bar
            double upX = groupX + barW + barGap;
            double upH = Math.Clamp(pt.UploadedBytes / scaleMax, 0, 1) * chartH;
            if (pt.UploadedBytes > 0)
            {
                double hVal = Math.Max(3.0, upH);
                dc.DrawRoundedRectangle(upBrush, null, new Rect(upX, baselineY - hVal, barW, hVal), 2.5, 2.5);
            }
            else
            {
                dc.DrawRoundedRectangle(baselineBrush, null, new Rect(upX, baselineY - 2, barW, 2), 1, 1);
            }

            // Show peak value label above active bars if wide enough
            long dayMax = Math.Max(pt.DownloadedBytes, pt.UploadedBytes);
            if (dayMax > 0 && slotW >= 44)
            {
                double topY = baselineY - Math.Max(downH, upH);
                var valFt = new FormattedText(
                    FormatHelper.FormatBytes(dayMax),
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    9.5,
                    labelBrush,
                    dpi);
                double vx = i * slotW + (slotW - valFt.Width) / 2.0;
                double vy = Math.Max(padTop, topY - valFt.Height - 2);
                dc.DrawText(valFt, new Point(Math.Max(0, vx), vy));
            }

            // Date label (dd.MM)
            string shortDate = pt.DateKey.Length == 10
                ? $"{pt.DateKey.Substring(8, 2)}.{pt.DateKey.Substring(5, 2)}"
                : pt.DateKey;

            var dateFt = new FormattedText(
                shortDate,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                10,
                labelBrush,
                dpi);
            double tx = i * slotW + (slotW - dateFt.Width) / 2.0;
            dc.DrawText(dateFt, new Point(tx, baselineY + 5));
        }
    }
}
