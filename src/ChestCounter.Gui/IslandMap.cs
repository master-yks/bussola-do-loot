using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChestCounter.Gui;

internal sealed record MapPlace(string Name, double X, double Y, bool Landmark);
internal sealed record MapLayer(string Id, string Name, string Color, bool Chest, double[][] Points);
internal sealed record IslandData(string Version, double WidthMeters, MapPlace[] Places, MapLayer[] Layers);
internal sealed record MapSpot(Point Position, MapLayer Layer);

public sealed partial class IslandMap : FrameworkElement
{
    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Sea = Ink("#125493"), Navy = Ink("#D40D222D"), Mint = Ink("#87F4D5");
    private static readonly Pen Edge = Stroke(Navy, 1), Selected = Stroke(Mint, 2);
    private readonly BitmapSource _image;
    private readonly BitmapSource _ortho;
    private readonly Dictionary<(int X, int Y), BitmapSource> _tiles = new();
    private static readonly Brush DeepSea = Ink("#295966");
    private readonly Dictionary<(int, int), List<MapSpot>> _cells = new();
    private readonly HashSet<MapSpot> _near = new(ReferenceEqualityComparer.Instance);
    private readonly List<Point[]> _history = new();
    private readonly List<Point> _route = new();
    private readonly Dictionary<string, FormattedText> _labels = new();
    private static readonly Point IslandCenter = new(.54, .57);
    private const double OverviewScale = 1.54;
    private Point _center = IslandCenter, _press, _startCenter;
    private double _zoom = 1;
    private int _handle = -1;
    private bool _dragged;
    private double _radius = 20;
    internal IslandData Data { get; }
    internal IReadOnlyList<Point> Route => _route;
    internal IReadOnlyCollection<MapSpot> Nearby => _near;
    internal double LengthMeters { get; private set; }
    internal bool CanUndo => _history.Count > 0;
    internal event Action? RouteChanged;
    internal HashSet<string> VisibleLayers { get; } = new();
    internal bool Editing { get; set; }
    internal bool ShowLabels { get; set; } = true;
    private double Scale => Math.Max(1, Math.Min(ActualWidth, ActualHeight) * OverviewScale * _zoom);

    public IslandMap()
    {
        ClipToBounds = true;
        Focusable = true;
        using var json = Application.GetResourceStream(new Uri("/BussolaDoLoot;component/Assets/Island/island.json", UriKind.Relative)).Stream;
        Data = JsonSerializer.Deserialize<IslandData>(json)!;
        _image = ReadImage("crossbones.png");
        _ortho = ReadImage("Ortho/overview.jpg");
        foreach (var layer in Data.Layers)
        {
            if (layer.Chest) VisibleLayers.Add(layer.Id);
            foreach (var xy in layer.Points)
            {
                var p = new Point(xy[0], xy[1]);
                var key = (Cell(p.X), Cell(p.Y));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new();
                list.Add(new(p, layer));
            }
        }
        IndexMarkers();
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    private static BitmapSource ReadImage(string file)
    {
        using var stream = Application.GetResourceStream(new Uri("/BussolaDoLoot;component/Assets/Island/" + file, UriKind.Relative)).Stream;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void DrawTerrain(DrawingContext dc, Rect viewport)
    {
        bool detailed = _zoom >= 2.4;
        dc.DrawRectangle(detailed ? DeepSea : Sea, null, viewport);
        dc.DrawImage(detailed ? _ortho : _image, new Rect(Screen(new(0, 0)), Screen(new(1, 1))));
        if (!detailed) return;
        var frame = new Rect(World(new(0, 0)), World(new(ActualWidth, ActualHeight)));
        int xMin = Math.Max(2, (int)Math.Floor(frame.Left * 8)), xMax = Math.Min(6, (int)Math.Floor(frame.Right * 8));
        int yMin = Math.Max(2, (int)Math.Floor(frame.Top * 8)), yMax = Math.Min(6, (int)Math.Floor(frame.Bottom * 8));
        foreach (var key in _tiles.Keys.ToArray())
            if (key.X < xMin - 1 || key.X > xMax + 1 || key.Y < yMin - 1 || key.Y > yMax + 1)
                _tiles.Remove(key);
        for (int x = xMin; x <= xMax; x++)
            for (int y = yMin; y <= yMax; y++)
            {
                if (!_tiles.TryGetValue((x, y), out var tile))
                    _tiles[(x, y)] = tile = ReadImage($"Ortho/{x}-{y}.jpg");
                var top = Screen(new(x / 8.0, y / 8.0));
                dc.DrawImage(tile, new Rect(top, new Size(Scale / 8 + .35, Scale / 8 + .35)));
            }
    }

    private static SolidColorBrush Ink(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
    private static Pen Stroke(Brush brush, double width)
    {
        var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
    private static int Cell(double n) => Math.Clamp((int)Math.Floor(n * 48), 0, 47);
    private IEnumerable<MapSpot> InBounds(Rect rect)
    {
        for (int x = Cell(rect.Left); x <= Cell(rect.Right); x++)
            for (int y = Cell(rect.Top); y <= Cell(rect.Bottom); y++)
                if (_cells.TryGetValue((x, y), out var spots))
                    foreach (var spot in spots)
                        if (rect.Contains(spot.Position)) yield return spot;
    }

    private Point Screen(Point p) => new((p.X - _center.X) * Scale + ActualWidth / 2, (p.Y - _center.Y) * Scale + ActualHeight / 2);
    private Point World(Point p) => new((p.X - ActualWidth / 2) / Scale + _center.X, (p.Y - ActualHeight / 2) / Scale + _center.Y);
    private static Point Clamp(Point p) => new(Math.Clamp(p.X, 0, 1), Math.Clamp(p.Y, 0, 1));
    internal void FocusStop(Point point) { _center = point; _zoom = Math.Max(10, _zoom); InvalidateVisual(); }
    internal void FocusPlace(MapPlace place) { _center = new(place.X, place.Y); _zoom = 5; InvalidateVisual(); }
    internal void ResetView() { _center = IslandCenter; _zoom = 1; InvalidateVisual(); }
    internal void FitRoute()
    {
        if (_route.Count == 0) return;
        var bounds = new Rect(_route[0], _route[0]);
        foreach (var p in _route) bounds.Union(p);
        _center = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        double padding = 2 * Math.Max(12, _radius) / Data.WidthMeters;
        double scale = Math.Min(ActualWidth * .85 / (bounds.Width + padding), ActualHeight * .85 / (bounds.Height + padding));
        _zoom = Math.Clamp(scale / Math.Max(1, Math.Min(ActualWidth, ActualHeight) * OverviewScale), .8, 32);
        InvalidateVisual();
    }
    internal void Zoom(double factor, Point? anchor = null)
    {
        var p = anchor ?? new Point(ActualWidth / 2, ActualHeight / 2);
        var fixedPoint = World(p);
        _zoom = Math.Clamp(_zoom * factor, .8, 32);
        _center = Clamp(_center + (fixedPoint - World(p)));
        InvalidateVisual();
    }
    internal void SetRadius(double meters) { _radius = meters; MeasureRoute(); }
    private void Remember()
    {
        if (_history.Count == 64) _history.RemoveAt(0);
        _history.Add(_route.ToArray());
    }
    internal void ReplaceRoute(IEnumerable<Point> points)
    {
        Remember();
        _route.Clear();
        _route.AddRange(points);
        MeasureRoute();
    }
    internal void Undo()
    {
        if (!CanUndo) return;
        _route.Clear();
        _route.AddRange(_history[^1]);
        _history.RemoveAt(_history.Count - 1);
        MeasureRoute();
    }
    private static double DistanceSquared(Point p, Point a, Point b)
    {
        var ab = b - a;
        double t = ab.LengthSquared == 0 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / ab.LengthSquared, 0, 1);
        return (p - (a + ab * t)).LengthSquared;
    }
    internal void MeasureRoute()
    {
        _near.Clear();
        LengthMeters = 0;
        double radius = _radius / Data.WidthMeters;
        for (int i = 0; i < _route.Count; i++)
        {
            var a = _route[Math.Max(0, i - 1)];
            var b = _route[i];
            LengthMeters += (b - a).Length * Data.WidthMeters;
            var bounds = new Rect(a, b);
            bounds.Inflate(radius, radius);
            foreach (var spot in InBounds(bounds))
                if (spot.Layer.Chest && VisibleLayers.Contains(spot.Layer.Id) && DistanceSquared(spot.Position, a, b) <= radius * radius)
                    _near.Add(spot);
        }
        RouteChanged?.Invoke();
        InvalidateVisual();
    }

    private FormattedText Label(string value, double size)
    {
        string key = size + "|" + value;
        if (!_labels.TryGetValue(key, out var text))
            _labels[key] = text = new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return text;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _labels.Clear();
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        var viewport = new Rect(RenderSize);
        DrawTerrain(dc, viewport);
        dc.DrawRectangle(MapTint, null, viewport);
        var bounds = new Rect(World(new(-60, -60)), World(new(ActualWidth + 60, ActualHeight + 60)));
        var path = new StreamGeometry();
        if (_route.Count > 0)
        {
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(Screen(_route[0]), false, false);
                for (int i = 1; i < _route.Count; i++) ctx.LineTo(Screen(_route[i]), true, false);
            }
            double band = Scale * _radius / Data.WidthMeters;
            dc.PushOpacity(.23);
            if (_route.Count == 1) dc.DrawEllipse(Mint, null, Screen(_route[0]), band, band);
            else dc.DrawGeometry(null, Stroke(Mint, 2 * band), path);
            dc.Pop();
        }
        DrawMarkers(dc, bounds, viewport);
        if (_route.Count > 0)
        {
            dc.DrawGeometry(null, Stroke(Navy, 5), path);
            dc.DrawGeometry(null, Selected, path);
            for (int i = 0; i < _route.Count; i++)
            {
                var p = Screen(_route[i]);
                var text = Label((i + 1).ToString(), 11);
                double r = Math.Max(9, text.Width / 2 + 3);
                if (i > 0)
                {
                    var previous = Screen(_route[i - 1]);
                    var heading = p - previous;
                    if (heading.Length > 48)
                    {
                        heading.Normalize();
                        var mid = previous + (p - previous) * .55;
                        var wing = new Vector(-heading.Y, heading.X) * 4;
                        dc.DrawLine(Selected, mid - heading * 6 + wing, mid);
                        dc.DrawLine(Selected, mid - heading * 6 - wing, mid);
                    }
                }
                dc.DrawEllipse(Navy, i == 0 ? Stroke(Ivory, 2.5) : Selected, p, r, r);
                dc.DrawText(text, new(p.X - text.Width / 2, p.Y - text.Height / 2));
            }
        }
        double meters = new[] { 1, 5, 10, 25, 50, 100, 250, 500 }.LastOrDefault(m => m / Data.WidthMeters * Scale <= 90, 1);
        double width = meters / Data.WidthMeters * Scale;
        var left = new Point(14, ActualHeight - 14);
        var right = left + new Vector(width, 0);
        dc.DrawRoundedRectangle(Navy, null, new Rect(7, left.Y - 21, Math.Max(50, width + 14), 28), 4, 4);
        dc.DrawText(Label($"{meters} m", 10), new(left.X, left.Y - 19));
        dc.DrawLine(Stroke(Brushes.White, 2), left, right);
    }
    private Marker? _hovered;
    private int HitHandle(Point p) => _route.FindLastIndex(q => (Screen(q) - p).LengthSquared <= 121);
    protected override void OnMouseWheel(MouseWheelEventArgs e) { Zoom(Math.Pow(1.25, e.Delta / 120.0), e.GetPosition(this)); e.Handled = true; }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus(); _press = e.GetPosition(this); _startCenter = _center;
        _handle = Editing && !PickingOrigin ? HitHandle(_press) : -1; _dragged = false; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (!IsMouseCaptured)
        {
            var marker = HitMarker(p);
            if (!ReferenceEquals(_hovered, marker))
            {
                _hovered = marker;
                ToolTip = marker == null ? null : MarkerDescription(marker);
            }
            Cursor = PickingOrigin || Editing ? Cursors.Cross : marker == null ? Cursors.ScrollAll : Cursors.Hand;
            return;
        }
        var delta = p - _press;
        if (!_dragged && delta.Length < 4) return;
        if (_handle >= 0)
        {
            if (!_dragged) Remember();
            _route[_handle] = Clamp(World(p)); MeasureRoute();
        }
        else { _center = Clamp(_startCenter - delta / Scale); InvalidateVisual(); }
        _dragged = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var p = World(e.GetPosition(this));
        ReleaseMouseCapture();
        if (!_dragged && PickingOrigin && new Rect(0, 0, 1, 1).Contains(p))
        {
            OriginPicked?.Invoke(HitMarker(e.GetPosition(this)) is { Region: null, Spots.Length: 1 } marker
                ? marker.Spots[0].Position : p);
        }
        else if (!_dragged && !Editing && !PickingOrigin) Inspect(e.GetPosition(this));
        else if (Editing && !_dragged && _handle < 0 && new Rect(0, 0, 1, 1).Contains(p))
        {
            Remember();
            if (HitMarker(e.GetPosition(this)) is { Region: null, Spots.Length: 1 } marker)
                p = marker.Spots[0].Position;
            int index = _route.Count;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && index >= 2)
                index = Enumerable.Range(1, index - 1).MinBy(i => DistanceSquared(p, _route[i - 1], _route[i]));
            _route.Insert(index, p); MeasureRoute();
        }
        e.Handled = true;
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        int i = HitHandle(e.GetPosition(this));
        if (!Editing || i < 0) return;
        Remember(); _route.RemoveAt(i); MeasureRoute(); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): Undo(); break;
            case Key.Add or Key.OemPlus: Zoom(1.5); break;
            case Key.Subtract or Key.OemMinus: Zoom(1 / 1.5); break;
            case Key.Home: ResetView(); break;
            case Key.Left:  _center = Clamp(_center + new Vector(-64 / Scale, 0)); break;
            case Key.Right: _center = Clamp(_center + new Vector(64 / Scale, 0)); break;
            case Key.Up:    _center = Clamp(_center + new Vector(0, -64 / Scale)); break;
            case Key.Down:  _center = Clamp(_center + new Vector(0, 64 / Scale)); break;
            default: base.OnKeyDown(e); return;
        }
        InvalidateVisual();
        e.Handled = true;
    }
}
