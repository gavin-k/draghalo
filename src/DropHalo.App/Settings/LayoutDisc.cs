using DropHalo.App.Styles;
using DropHalo.Core.Wheel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DropHalo.App.Settings;

/// <summary>What one sector of the 轮盘排布 disc shows: an item's label or a group's name, or nothing.</summary>
internal sealed record DiscSector(string Label, bool IsGroup, bool IsEmpty);

/// <summary>
/// The six sectors of the wheel being edited, in the shape the wheel draws them. A sector dragged onto another
/// raises <see cref="Moved"/>; let go on the hub, outside the rim or on itself and nothing happens. A redraw drops
/// a drag in progress, so a layout changed under the pointer is never swapped from a stale picture.
/// </summary>
internal sealed class LayoutDisc
{
    private const double Radius = WheelGeometry.OuterRadius;
    private const double LabelWidth = 80;
    private const double LabelHeight = 40;

    private static readonly SolidColorBrush Clear = new(Colors.Transparent);

    private readonly Canvas _canvas;
    private readonly Path[] _wedges = new Path[WheelModel.SlotCount];
    private readonly TextBlock[] _labels = new TextBlock[WheelModel.SlotCount];
    private readonly Grid[] _labelBoxes = new Grid[WheelModel.SlotCount];
    private DiscSector[] _sectors = [.. Enumerable.Repeat(new DiscSector("", false, true), WheelModel.SlotCount)];
    private int _from = -1;
    private int _over = -1;

    public LayoutDisc()
    {
        // A touch drag stays with the disc instead of scrolling the settings page.
        _canvas = new Canvas
        {
            Width = 2 * Radius,
            Height = 2 * Radius,
            Background = Clear,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 4),
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
        };
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            _wedges[i] = new Path { Data = Wedge(i) };
            _canvas.Children.Add(_wedges[i]);

            _labels[i] = new TextBlock
            {
                FontSize = 12,
                MaxWidth = LabelWidth,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = AppStyles.Brush("DwTextBrush"),
            };
            AutomationProperties.SetAutomationId(_labels[i], $"layout-disc-{i}");
            var (x, y) = WheelGeometry.SectorCenter(i);
            _labelBoxes[i] = new Grid { Width = LabelWidth, Height = LabelHeight, IsHitTestVisible = false, Children = { _labels[i] } };
            Canvas.SetLeft(_labelBoxes[i], Radius + x - LabelWidth / 2);
            Canvas.SetTop(_labelBoxes[i], Radius + y - LabelHeight / 2);
            _canvas.Children.Add(_labelBoxes[i]);
        }

        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerCaptureLost += (_, _) => Cancel();
        _canvas.PointerCanceled += (_, _) => Cancel();
        Show(_sectors);
    }

    public UIElement View => _canvas;

    /// <summary>A sector was dropped on another: (from, to), different, both 0..5.</summary>
    public event Action<int, int>? Moved;

    /// <summary>Redraws all six sectors and drops any drag in progress.</summary>
    public void Show(IReadOnlyList<DiscSector> sectors)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(sectors.Count, WheelModel.SlotCount, nameof(sectors));
        Cancel();
        _sectors = [.. sectors];
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            var text = _sectors[i].IsEmpty ? L.T("（空）") : _sectors[i].Label;
            _labels[i].Text = text;
            AutomationProperties.SetName(_labels[i], L.T("{0} · {1}", i + 1, text));
        }

        Paint();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        var pressed = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse ? point.Properties.IsLeftButtonPressed : point.IsInContact;
        var index = SectorAt(point.Position);
        if (!pressed || _from >= 0 || index < 0 || _sectors[index].IsEmpty || !_canvas.CapturePointer(e.Pointer))
        {
            return;
        }

        (_from, _over) = (index, -1);
        e.Handled = true;
        Paint();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_from < 0)
        {
            return;
        }

        var over = SectorAt(e.GetCurrentPoint(_canvas).Position);
        over = over == _from ? -1 : over;
        e.Handled = true;
        if (over != _over)
        {
            _over = over;
            Paint();
        }
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_from < 0)
        {
            return;
        }

        var (from, to) = (_from, SectorAt(e.GetCurrentPoint(_canvas).Position));
        e.Handled = true;
        Cancel();
        if (to >= 0 && to != from)
        {
            Moved?.Invoke(from, to);
        }
    }

    private void Cancel()
    {
        if (_from < 0)
        {
            return;
        }

        // Cleared first: releasing the capture raises PointerCaptureLost, which comes back here.
        (_from, _over) = (-1, -1);
        _canvas.ReleasePointerCaptures();
        Paint();
    }

    /// <summary>The picked-up sector is faded, the one under the pointer filled with the accent, a group outlined in it.</summary>
    private void Paint()
    {
        for (var i = 0; i < WheelModel.SlotCount; i++)
        {
            var sector = _sectors[i];
            _wedges[i].Fill = i == _over ? AppStyles.Brush("DwAccentSoftBrush") : sector.IsEmpty ? Clear : AppStyles.Brush("DwSurface2Brush");
            _wedges[i].Stroke = AppStyles.Brush(sector.IsGroup ? "DwAccentBrush" : "DwBorderBrush");
            _wedges[i].StrokeThickness = sector.IsGroup ? 2 : 1;
            var faded = i == _from ? 0.4 : 1;
            _wedges[i].Opacity = faded;
            _labelBoxes[i].Opacity = faded * (sector.IsEmpty ? 0.5 : 1);
        }
    }

    private static int SectorAt(Point p) => WheelGeometry.SectorAt(p.X - Radius, p.Y - Radius);

    /// <summary>Sector <paramref name="index"/> as the wheel draws it, with a hair of gap so the outline reads as a divider.</summary>
    private static PathGeometry Wedge(int index)
    {
        const double GapDeg = 0.8;
        var a0 = WheelGeometry.StartAngle(index) + GapDeg;
        var a1 = WheelGeometry.StartAngle(index) + WheelGeometry.SectorSweep - GapDeg;
        const double Outer = WheelGeometry.OuterRadius - 1;
        const double Inner = WheelGeometry.InnerRadius;
        var figure = new PathFigure { StartPoint = At(Outer, a0), IsClosed = true };
        figure.Segments.Add(new ArcSegment { Point = At(Outer, a1), Size = new Size(Outer, Outer), SweepDirection = SweepDirection.Clockwise });
        figure.Segments.Add(new LineSegment { Point = At(Inner, a1) });
        figure.Segments.Add(new ArcSegment { Point = At(Inner, a0), Size = new Size(Inner, Inner), SweepDirection = SweepDirection.Counterclockwise });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;

        static Point At(double radius, double degrees)
        {
            var a = degrees * Math.PI / 180;
            return new Point(Radius + Math.Cos(a) * radius, Radius + Math.Sin(a) * radius);
        }
    }
}
