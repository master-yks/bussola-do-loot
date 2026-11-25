using System.Windows;
using System.Windows.Media;

namespace ChestCounter.Gui;

public sealed partial class IslandMap
{
    private sealed record Marker(Point World, Rect Box, MapSpot[] Spots, string? Region);
    private readonly List<Marker> _markers = new();
    private readonly Dictionary<MapSpot, MapPlace> _regions = new(ReferenceEqualityComparer.Instance);
    internal Dictionary<string, DrawingImage> Symbols { get; } = new();
    internal event Action<MapSpot, string>? SpotInspected;
    internal event Action? BackgroundClicked;
    internal event Action<Point>? OriginPicked;
    internal bool PickingOrigin { get; set; }
    private static readonly Brush MarkerBack = Ink("#F1152834"), BadgeBack = Ink("#FA203A48");
    private static readonly Pen MarkerEdge = Stroke(Ink("#9EBCD0D1"), 1);
    private static readonly Brush Shadow = Ink("#5207141B");
    private static readonly Brush MapTint = Ink("#15091824");
    private static readonly Brush Ivory = Ink("#FFE5A2");

    private void IndexMarkers()
    {
        var places = Data.Places.Where(p => !p.Landmark).DistinctBy(p => (p.X, p.Y)).ToArray();
        foreach (var layer in Data.Layers) Symbols[layer.Id] = MapSymbols.Create(layer);
        foreach (var spot in _cells.Values.SelectMany(s => s))
            _regions[spot] = places.MinBy(p => (new Point(p.X, p.Y) - spot.Position).LengthSquared)!;
    }

    private void DrawMarkers(DrawingContext dc, Rect bounds, Rect viewport)
    {
        _markers.Clear();
        bool regional = _zoom < 1.65 && _route.Count == 0;
        if (regional)
        {
            // Use the entire layer so the number associated with a region does not change while panning.
            var areas = _regions.Where(s => VisibleLayers.Contains(s.Key.Layer.Id)).GroupBy(s => s.Value);
            foreach (var group in areas)
                DrawMarker(dc, new Point(group.Key.X, group.Key.Y), group.Select(s => s.Key).ToArray(), group.Key.Name, viewport);
        }
        else
        {
            double cell = (_zoom < 3 ? 46 : _zoom < 7 ? 32 : 19) / Scale;
            var groups = InBounds(bounds).Where(s => VisibleLayers.Contains(s.Layer.Id) || _near.Contains(s))
                .GroupBy(s => ((int)Math.Floor(s.Position.X / cell), (int)Math.Floor(s.Position.Y / cell)));
            var handles = _route.Select(Screen).ToArray();
            foreach (var g in groups)
            {
                var spots = g.ToArray();
                var center = new Point(spots.Average(s => s.Position.X), spots.Average(s => s.Position.Y));
                // A numbered stop takes precedence over the object it sits on.
                var screen = Screen(center);
                if (handles.Any(p => (p - screen).LengthSquared < 17 * 17)) continue;
                DrawMarker(dc, center, spots, null, viewport);
            }
        }

        if (!ShowLabels) return;
        var occupied = _markers.Select(m => m.Box).ToList();
        occupied.AddRange(_route.Select(p => { var center = Screen(p); return new Rect(center.X - 10, center.Y - 10, 20, 20); }));
        if (regional)
        {
            // Place short nameplates around the counts, including locations with no visible spawns.
            foreach (var place in Data.Places.Where(p => !p.Landmark).DistinctBy(p => (p.X, p.Y)))
                DrawPlaceName(dc, place, viewport, occupied);
        }
        else
        {
            foreach (var place in Data.Places.Where(p => !p.Landmark || _zoom >= 3))
                DrawPlaceName(dc, place, viewport, occupied);
        }
    }

    private void DrawPlaceName(DrawingContext dc, MapPlace place, Rect viewport, List<Rect> occupied)
    {
        var p = Screen(new(place.X, place.Y));
        if (!viewport.Contains(p)) return;
        var label = Label(place.Name, place.Landmark ? 10 : 11.5);
        double w = label.Width + 10, h = label.Height + 3;
        Rect[] positions = [new(p.X - w / 2, p.Y + 13, w, h), new(p.X - w / 2, p.Y - 14 - h, w, h),
            new(p.X + 22, p.Y - h / 2, w, h), new(p.X - w - 22, p.Y - h / 2, w, h)];
        foreach (var box in positions)
        {
            if (!viewport.Contains(box) || occupied.Any(b => b.IntersectsWith(box))) continue;
            dc.DrawRoundedRectangle(Navy, null, box, 4, 4);
            dc.DrawText(label, new(box.X + 5, box.Y + 1));
            occupied.Add(box);
            break;
        }
    }

    private void DrawMarker(DrawingContext dc, Point world, MapSpot[] spots, string? region, Rect viewport)
    {
        var p = Screen(world);
        bool multiple = spots.Length > 1;
        double side = region != null ? 24 : _zoom >= 7 ? 24 : 20;
        var count = multiple ? Label(spots.Length.ToString(), 11) : null;
        double width = side + (count == null ? 0 : count.Width + 5);
        var box = new Rect(p.X - width / 2, p.Y - side / 2, width, side);
        if (!viewport.IntersectsWith(box)) return;
        var dominant = spots.GroupBy(s => s.Layer.Id).MaxBy(g => g.Count())!.Key;
        bool selected = spots.Any(s => _near.Contains(s));
        dc.DrawRoundedRectangle(Shadow, null, new Rect(box.X, box.Y + 2, box.Width, box.Height), 7, 7);
        dc.DrawRoundedRectangle(multiple ? BadgeBack : MarkerBack, selected ? Selected : MarkerEdge, box, 7, 7);
        dc.DrawImage(Symbols[dominant], new Rect(box.X + 1, box.Y + 1, side - 2, side - 2));
        if (count != null) dc.DrawText(count, new(box.X + side, p.Y - count.Height / 2));
        _markers.Add(new(world, box, spots, region));
    }

    private Marker? HitMarker(Point p) => _markers.LastOrDefault(m => m.Box.Contains(p));
    private static string MarkerDescription(Marker m)
    {
        var lines = m.Spots.GroupBy(s => s.Layer.Name).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} · {g.Count()}");
        return string.Join(Environment.NewLine, m.Region == null ? lines : lines.Prepend(m.Region));
    }

    private void Inspect(Point screen)
    {
        var marker = HitMarker(screen);
        if (marker == null) { BackgroundClicked?.Invoke(); return; }
        if (marker.Region != null || (marker.Spots.Length > 1 && _zoom < 32))
        {
            BackgroundClicked?.Invoke();
            _center = marker.World;
            _zoom = Math.Min(32, marker.Region != null ? 5 : _zoom * 2);
            InvalidateVisual();
            return;
        }
        SpotInspected?.Invoke(marker.Spots[0], marker.Spots.Length > 1 ? MarkerDescription(marker) : _regions[marker.Spots[0]].Name);
    }

    internal int SuggestStops(Point origin, int limit)
    {
        const double searchMeters = 180;
        double r = searchMeters / Data.WidthMeters;
        var candidates = InBounds(new Rect(origin.X - r, origin.Y - r, 2 * r, 2 * r))
            .Where(s => s.Layer.Chest && VisibleLayers.Contains(s.Layer.Id) && (s.Position - origin).Length <= r)
            .Select(s => s.Position).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (candidates.Count == 0) return 0;
        double minSpacing = 3 / Data.WidthMeters;
        var stops = new List<Point> { origin };
        candidates.RemoveAll(p => _route.Any(old => (p - old).Length < minSpacing));
        while (stops.Count <= limit && candidates.Count > 0)
        {
            var next = candidates.MinBy(p => (stops[^1] - p).LengthSquared);
            candidates.RemoveAll(p => (p - next).Length < minSpacing);
            if ((next - stops[^1]).Length >= minSpacing) stops.Add(next);
        }
        if (stops.Count == 1 && _route.Count > 0) return 0;
        // Shorten crossing legs while keeping the chosen origin fixed (open-path 2-opt).
        bool improved;
        do
        {
            improved = false;
            for (int i = 1; i < stops.Count - 1; i++)
                for (int j = i + 1; j < stops.Count; j++)
                {
                    double before = (stops[i - 1] - stops[i]).Length;
                    double after = (stops[i - 1] - stops[j]).Length;
                    if (j + 1 < stops.Count)
                    {
                        before += (stops[j] - stops[j + 1]).Length;
                        after += (stops[i] - stops[j + 1]).Length;
                    }
                    if (after + 1e-9 >= before) continue;
                    stops.Reverse(i, j - i + 1);
                    improved = true;
                }
        } while (improved);
        bool extend = _route.Count > 0;
        ReplaceRoute(_route.Concat(stops.Skip(extend ? 1 : 0)).ToArray());
        FitRoute();
        return stops.Count - (extend ? 1 : 0);
    }
}
