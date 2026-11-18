using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Gem.Controls;

public sealed class PieceMapControl : FrameworkElement
{
    public static readonly DependencyProperty PiecesProperty = DependencyProperty.Register(
        nameof(Pieces),
        typeof(IReadOnlyList<double>),
        typeof(PieceMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UploadingPiecesProperty = DependencyProperty.Register(
        nameof(UploadingPieces),
        typeof(IReadOnlyCollection<int>),
        typeof(PieceMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnUploadingPiecesChanged));

    public static readonly DependencyProperty TotalPiecesProperty = DependencyProperty.Register(
        nameof(TotalPieces),
        typeof(int),
        typeof(PieceMapControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double FadeInSeconds = 0.22;
    private const double FadeOutSeconds = 0.38;

    private readonly DispatcherTimer _animTimer;
    private readonly Dictionary<int, double> _cellBorderOpacity = new();
    private DateTime _lastTickUtc = DateTime.UtcNow;

    public IReadOnlyList<double>? Pieces
    {
        get => (IReadOnlyList<double>?)GetValue(PiecesProperty);
        set => SetValue(PiecesProperty, value);
    }

    public IReadOnlyCollection<int>? UploadingPieces
    {
        get => (IReadOnlyCollection<int>?)GetValue(UploadingPiecesProperty);
        set => SetValue(UploadingPiecesProperty, value);
    }

    public int TotalPieces
    {
        get => (int)GetValue(TotalPiecesProperty);
        set => SetValue(TotalPiecesProperty, value);
    }

    public PieceMapControl()
    {
        ClipToBounds = true;
        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(30)
        };
        _animTimer.Tick += OnAnimTick;
        Unloaded += (_, _) => _animTimer.Stop();
    }

    private static void OnUploadingPiecesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PieceMapControl control)
            return;

        control._lastTickUtc = DateTime.UtcNow;
        if (!control._animTimer.IsEnabled)
            control._animTimer.Start();
    }

    private void OnAnimTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0.005, 0.1);
        _lastTickUtc = now;

        if (!UpdateBorderOpacities(dt))
        {
            _animTimer.Stop();
        }

        InvalidateVisual();
    }

    private bool UpdateBorderOpacities(double dt)
    {
        var activeCells = ComputeUploadingCells();

        // Ensure newly active cells start at 0 so they fade in smoothly
        foreach (int cell in activeCells)
        {
            if (!_cellBorderOpacity.ContainsKey(cell))
                _cellBorderOpacity[cell] = 0.0;
        }

        bool anyAnimating = false;
        var keys = _cellBorderOpacity.Keys.ToArray();
        foreach (int cell in keys)
        {
            double current = _cellBorderOpacity[cell];
            if (activeCells.Contains(cell))
            {
                if (current < 1.0)
                {
                    current = Math.Min(1.0, current + dt / FadeInSeconds);
                    _cellBorderOpacity[cell] = current;
                    if (current < 1.0)
                        anyAnimating = true;
                }
            }
            else
            {
                current -= dt / FadeOutSeconds;
                if (current <= 0.001)
                {
                    _cellBorderOpacity.Remove(cell);
                }
                else
                {
                    _cellBorderOpacity[cell] = current;
                    anyAnimating = true;
                }
            }
        }

        return anyAnimating;
    }

    private HashSet<int> ComputeUploadingCells()
    {
        var result = new HashSet<int>();
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 16 || height <= 16)
            return result;

        var uploading = UploadingPieces;
        if (uploading is null || uploading.Count == 0)
            return result;

        var (cols, rows, _, _, _) = ComputeGridMetrics(width, height);
        int totalCells = cols * rows;
        int pieceCount = Pieces?.Count ?? 0;
        int realPieceCount = TotalPieces > 0 ? TotalPieces : pieceCount;
        if (realPieceCount <= 0)
            return result;

        foreach (int pieceIdx in uploading)
        {
            if (pieceIdx < 0 || pieceIdx >= realPieceCount)
                continue;

            if (realPieceCount >= totalCells)
            {
                int cellIdx = Math.Clamp((int)((long)pieceIdx * totalCells / realPieceCount), 0, totalCells - 1);
                result.Add(cellIdx);
            }
            else
            {
                int startCell = (int)((long)pieceIdx * totalCells / realPieceCount);
                int endCell = Math.Max(startCell + 1, (int)((long)(pieceIdx + 1) * totalCells / realPieceCount));
                for (int c = startCell; c < endCell && c < totalCells; c++)
                    result.Add(c);
            }
        }

        return result;
    }

    internal static (int Cols, int Rows, double CellSize, double StepX, double StepY) ComputeGridMetrics(double width, double height)
    {
        const double targetCell = 14.0;
        const double gap = 3.5;
        double targetPitch = targetCell + gap;

        int cols = Math.Max(4, (int)Math.Round((width + gap) / targetPitch));
        int rows = Math.Max(3, (int)Math.Round((height + gap) / targetPitch));

        double cellWidth = (width - (cols - 1) * gap) / cols;
        double cellHeight = (height - (rows - 1) * gap) / rows;
        double cellSize = Math.Max(4.0, Math.Min(cellWidth, cellHeight));

        double stepX = cols > 1 ? (width - cellSize) / (cols - 1) : 0;
        double stepY = rows > 1 ? (height - cellSize) / (rows - 1) : 0;
        return (cols, rows, cellSize, stepX, stepY);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 16 || height <= 16)
            return;

        Color accentColor = (Application.Current?.TryFindResource("SystemAccentColorPrimaryBrush") as SolidColorBrush)?.Color
                            ?? Color.FromRgb(0x00, 0x78, 0xD4);

        var emptyBrush = new SolidColorBrush(Color.FromArgb(28, 140, 140, 140));
        emptyBrush.Freeze();

        var fullBrush = new SolidColorBrush(accentColor);
        fullBrush.Freeze();

        var (cols, rows, cellSize, stepX, stepY) = ComputeGridMetrics(width, height);
        double radius = Math.Min(2.5, cellSize * 0.20);

        int totalCells = cols * rows;
        var pieces = Pieces;
        int pieceCount = pieces?.Count ?? 0;

        // Seed any new uploading cells on first render if timer hasn't ticked yet
        var activeCells = ComputeUploadingCells();
        bool needTimer = false;
        foreach (int cell in activeCells)
        {
            if (!_cellBorderOpacity.ContainsKey(cell))
            {
                _cellBorderOpacity[cell] = 0.0;
                needTimer = true;
            }
        }
        if (_cellBorderOpacity.Keys.Any(k => !activeCells.Contains(k)))
            needTimer = true;

        if (needTimer && !_animTimer.IsEnabled)
        {
            _lastTickUtc = DateTime.UtcNow;
            _animTimer.Start();
        }

        double borderThickness = Math.Max(1.5, Math.Min(2.4, cellSize * 0.16));

        for (int cellIdx = 0; cellIdx < totalCells; cellIdx++)
        {
            int col = cellIdx % cols;
            int row = cellIdx / cols;
            double x = col * stepX;
            double y = row * stepY;

            double fill = 0.0;
            if (pieceCount > 0)
            {
                if (pieceCount >= totalCells)
                {
                    int start = (int)((long)cellIdx * pieceCount / totalCells);
                    int end = Math.Max(start + 1, (int)((long)(cellIdx + 1) * pieceCount / totalCells));
                    double sum = 0;
                    for (int p = start; p < end; p++)
                        sum += pieces![p];
                    fill = sum / (end - start);
                }
                else
                {
                    int pieceIdx = Math.Min(pieceCount - 1, (int)((long)cellIdx * pieceCount / totalCells));
                    fill = pieces![pieceIdx];
                }
            }

            fill = Math.Clamp(fill, 0.0, 1.0);

            Brush brush;
            if (fill <= 0.01)
            {
                brush = emptyBrush;
            }
            else if (fill >= 0.99)
            {
                brush = fullBrush;
            }
            else
            {
                byte alpha = (byte)Math.Clamp(45 + fill * 200, 45, 245);
                brush = new SolidColorBrush(Color.FromArgb(alpha, accentColor.R, accentColor.G, accentColor.B));
                brush.Freeze();
            }

            if (_cellBorderOpacity.TryGetValue(cellIdx, out double rawOpacity) && rawOpacity > 0.01)
            {
                // SmoothStep easing for smooth fade-in and fade-out
                double eased = rawOpacity * rawOpacity * (3.0 - 2.0 * rawOpacity);
                byte borderAlpha = (byte)Math.Clamp(eased * 255.0, 0, 255);
                var borderBrush = new SolidColorBrush(Color.FromArgb(borderAlpha, 255, 255, 255));
                borderBrush.Freeze();
                var pen = new Pen(borderBrush, borderThickness);
                pen.Freeze();

                double inset = borderThickness * 0.5;
                var rect = new Rect(x + inset, y + inset, Math.Max(1.0, cellSize - borderThickness), Math.Max(1.0, cellSize - borderThickness));
                dc.DrawRoundedRectangle(brush, pen, rect, radius, radius);
            }
            else
            {
                dc.DrawRoundedRectangle(brush, null, new Rect(x, y, cellSize, cellSize), radius, radius);
            }
        }
    }
}
