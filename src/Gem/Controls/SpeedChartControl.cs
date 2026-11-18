using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Gem.Helpers;

namespace Gem.Controls;

public sealed class SpeedChartControl : FrameworkElement
{
    private IReadOnlyList<long> _downloadPoints = [];
    private IReadOnlyList<long> _uploadPoints = [];

    private static readonly Color DownloadColor = Color.FromRgb(0x00, 0x78, 0xD4);
    private static readonly Color UploadColor = Color.FromRgb(0x10, 0x9E, 0x4E);

    public SpeedChartControl()
    {
        ClipToBounds = true;
    }

    public void UpdateSeries(IReadOnlyList<long> downloadPoints, IReadOnlyList<long> uploadPoints)
    {
        _downloadPoints = downloadPoints;
        _uploadPoints = uploadPoints;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 20 || h <= 20)
            return;

        double padTop = 10;
        double padBottom = 8;
        double chartH = Math.Max(10, h - padTop - padBottom);

        long maxVal = 1024; // minimum 1 KB/s scale
        foreach (var v in _downloadPoints)
            if (v > maxVal) maxVal = v;
        foreach (var v in _uploadPoints)
            if (v > maxVal) maxVal = v;

        // Round up to a neat multiple
        double scaleMax = maxVal * 1.15;

        // Draw 3 subtle horizontal gridlines
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(32, 128, 128, 128)), 1);
        gridPen.Freeze();

        for (int i = 0; i <= 2; i++)
        {
            double y = padTop + (chartH * i / 2.0);
            dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
        }

        // Peak label at top-left
        var labelBrush = Application.Current?.TryFindResource("TextFillColorTertiaryBrush") as Brush
                         ?? Brushes.Gray;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var peakText = new FormattedText(
            Services.LocalizationService.Instance.Format("ChartPeakFormat", FormatHelper.FormatSpeed((long)scaleMax)),
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10.5,
            labelBrush,
            dpi);
        dc.DrawText(peakText, new Point(4, 2));

        // Draw Upload first, then Download on top using Windows system accent color
        Color downloadColor = (Application.Current?.TryFindResource("SystemAccentColorPrimaryBrush") as SolidColorBrush)?.Color
                              ?? DownloadColor;
        DrawSeries(dc, _uploadPoints, w, padTop, chartH, scaleMax, UploadColor);
        DrawSeries(dc, _downloadPoints, w, padTop, chartH, scaleMax, downloadColor);
    }

    private static void DrawSeries(
        DrawingContext dc,
        IReadOnlyList<long> points,
        double width,
        double padTop,
        double chartHeight,
        double scaleMax,
        Color color)
    {
        if (points.Count < 2)
            return;

        double stepX = width / (points.Count - 1);
        double baselineY = padTop + chartHeight;

        var areaGeometry = new StreamGeometry();
        var lineGeometry = new StreamGeometry();

        using (var areaCtx = areaGeometry.Open())
        using (var lineCtx = lineGeometry.Open())
        {
            for (int i = 0; i < points.Count; i++)
            {
                double x = i * stepX;
                double ratio = Math.Clamp(points[i] / scaleMax, 0, 1);
                double y = baselineY - (ratio * chartHeight);

                var pt = new Point(x, y);
                if (i == 0)
                {
                    areaCtx.BeginFigure(new Point(x, baselineY), true, true);
                    areaCtx.LineTo(pt, false, false);
                    lineCtx.BeginFigure(pt, false, false);
                }
                else
                {
                    areaCtx.LineTo(pt, false, false);
                    lineCtx.LineTo(pt, true, true);
                }

                if (i == points.Count - 1)
                {
                    areaCtx.LineTo(new Point(x, baselineY), false, false);
                }
            }
        }

        areaGeometry.Freeze();
        lineGeometry.Freeze();

        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(75, color.R, color.G, color.B),
            Color.FromArgb(4, color.R, color.G, color.B),
            new Point(0, 0),
            new Point(0, 1));
        fillBrush.Freeze();

        var strokePen = new Pen(new SolidColorBrush(color), 2.0)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        strokePen.Freeze();

        dc.DrawGeometry(fillBrush, null, areaGeometry);
        dc.DrawGeometry(null, strokePen, lineGeometry);
    }
}
