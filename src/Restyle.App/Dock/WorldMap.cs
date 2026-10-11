using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Restyle.Core.Dock;

namespace Restyle.App.Docking;

/// <summary>
/// A frosted world map with the local time in a few cities (time zones come from Windows, no internet).
/// The coastlines are a simple hand-drawn outline: recognizable at widget size, tiny in the exe.
/// </summary>
internal sealed class WorldMap : FrameworkElement
{
    private const double West = -170, East = 190, North = 80, South = -58;

    // Rough continent outlines as longitude/latitude pairs.
    private static readonly double[][] Land =
    [
        // North and Central America
        [-168, 66, -162, 70, -156, 71, -140, 70, -128, 70, -115, 68, -95, 72, -82, 73, -80, 63, -90, 57, -82, 52, -78, 58, -70, 60,
         -64, 58, -56, 52, -60, 46, -66, 44, -70, 42, -74, 40, -76, 35, -81, 31, -80, 25, -82, 27, -84, 30, -90, 29, -97, 27, -97, 22,
         -92, 18, -87, 21, -88, 16, -84, 11, -80, 8, -78, 8, -83, 8, -87, 13, -92, 15, -96, 16, -105, 20, -106, 23, -110, 24, -112, 29,
         -117, 32, -120, 34, -124, 40, -124, 46, -125, 49, -130, 54, -135, 58, -142, 60, -150, 61, -155, 58, -165, 55, -160, 59, -165, 62],
        // Greenland
        [-73, 78, -60, 82, -30, 83, -20, 80, -18, 75, -22, 70, -40, 65, -44, 60, -50, 64, -55, 70, -60, 76],
        // South America
        [-80, 9, -72, 12, -62, 10, -52, 5, -50, 0, -44, -2, -35, -6, -38, -13, -40, -22, -48, -26, -53, -34, -58, -38, -63, -41,
         -65, -46, -68, -51, -70, -55, -74, -50, -73, -42, -72, -30, -70, -18, -76, -14, -81, -6, -80, 0, -78, 7],
        // Europe and Asia
        [-9, 37, -9, 43, -1, 44, -4, 48, 2, 51, 8, 54, 8, 57, 11, 59, 5, 59, 5, 62, 14, 67, 20, 70, 28, 71, 40, 67, 44, 68, 55, 68,
         70, 73, 80, 73, 100, 77, 113, 74, 130, 71, 150, 71, 170, 70, 180, 68, 180, 65, 170, 60, 162, 57, 156, 51, 156, 57, 160, 61,
         150, 59, 140, 54, 141, 48, 135, 43, 130, 42, 129, 35, 126, 35, 126, 38, 122, 40, 121, 37, 119, 35, 122, 30, 120, 25,
         110, 21, 108, 16, 109, 12, 105, 9, 100, 13, 99, 8, 103, 1, 101, 3, 98, 8, 98, 16, 94, 17, 92, 22, 89, 22, 87, 21, 80, 15,
         80, 10, 77, 8, 73, 17, 72, 21, 67, 24, 61, 25, 57, 26, 56, 24, 59, 22, 52, 17, 44, 12, 43, 15, 38, 21, 35, 28, 34, 31,
         36, 36, 30, 36, 27, 37, 26, 40, 23, 40, 23, 37, 20, 40, 19, 42, 13, 45, 16, 41, 18, 40, 16, 38, 12, 42, 9, 44, 3, 43,
         0, 39, -2, 37, -6, 36],
        // Africa
        [-17, 21, -17, 15, -12, 8, -8, 4, 0, 5, 9, 4, 10, -2, 13, -10, 12, -17, 15, -27, 18, -34, 25, -34, 32, -28, 35, -22,
         40, -15, 40, -10, 39, -5, 42, 0, 51, 11, 43, 12, 38, 18, 32, 30, 20, 32, 10, 37, 0, 36, -6, 36, -10, 30, -16, 25],
        [44, -25, 47, -25, 50, -15, 49, -12, 44, -16], // Madagascar
        [114, -22, 122, -18, 130, -12, 137, -12, 136, -15, 141, -11, 146, -19, 153, -26, 151, -34, 146, -39, 140, -38, 135, -35,
         131, -31, 124, -34, 115, -34], // Australia
        [-5, 50, 1, 51, 1, 53, -2, 55, -2, 58, -5, 58, -5, 55, -3, 53, -5, 52], // Great Britain
        [-6, 52, -6, 55, -10, 54, -10, 52], // Ireland
        [-24, 65, -14, 66, -14, 64, -22, 63], // Iceland
        [130, 31, 132, 34, 136, 34, 140, 36, 142, 40, 141, 45, 140, 42, 138, 38, 134, 35, 130, 34], // Japan
        [95, 6, 106, -6, 102, -4, 95, 3], // Sumatra
        [109, 1, 117, 7, 119, 1, 116, -4, 110, -3], // Borneo
        [131, -1, 141, -3, 150, -10, 141, -9, 137, -5], // New Guinea
        [172, -35, 178, -38, 174, -41, 171, -46, 167, -46, 172, -41], // New Zealand
    ];

    private static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly List<WorldCity> _cities;
    private Geometry? _land;
    private Size _landSize;

    public WorldMap(List<WorldCity> cities)
    {
        _cities = cities;
        Width = 340;
        Height = 190;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (_land is null || _landSize != new Size(w, h))
        {
            _land = BuildLand(w, h);
            _landSize = new Size(w, h);
        }

        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), null, _land);

        var now = DateTime.UtcNow;
        var dot = Brushes.White;
        var line = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var placed = new List<Rect>();
        foreach (var city in _cities.OrderByDescending(c => c.Longitude < West ? c.Longitude + 360 : c.Longitude)) // east first: western labels make room
        {
            var p = Project(city.Longitude, city.Latitude, w, h);
            string time;
            try
            {
                time = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.FindSystemTimeZoneById(city.TimeZone)).ToString("t", CultureInfo.CurrentCulture);
            }
            catch (Exception)
            {
                continue; // unknown time zone name in the config
            }

            // A small flag: a line from the city to a label with its name and time.
            var name = new FormattedText(city.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 8.5, Brushes.White, dpi);
            var clock = new FormattedText(time, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 8, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), dpi);
            var labelWidth = Math.Max(name.Width, clock.Width) + 4;

            // Up-right if there's room, otherwise up-left, then below: labels shouldn't cover each other.
            Rect[] candidates =
            [
                new(p.X + 1, p.Y - 24, labelWidth, 21),
                new(p.X - 1 - labelWidth, p.Y - 24, labelWidth, 21),
                new(p.X + 1, p.Y + 4, labelWidth, 21),
                new(p.X - 1 - labelWidth, p.Y + 4, labelWidth, 21),
            ];
            var label = candidates.FirstOrDefault(r => r.Left >= 0 && r.Right <= w && !placed.Any(o => o.IntersectsWith(r)), candidates[0]);
            placed.Add(label);

            dc.DrawEllipse(dot, null, p, 2, 2);
            var anchorX = label.Left > p.X ? label.Left : label.Right;
            dc.DrawLine(line, p, new Point(anchorX, label.Top < p.Y ? label.Bottom - 3 : label.Top + 3));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 40, 20, 70)), null, label, 2, 2);
            dc.DrawText(name, new Point(label.Left + 2, label.Top));
            dc.DrawText(clock, new Point(label.Left + 2, label.Top + 10));
        }
    }

    private static Point Project(double lon, double lat, double w, double h)
    {
        if (lon < West)
            lon += 360;
        return new Point((lon - West) / (East - West) * w, (North - lat) / (North - South) * h);
    }

    private static Geometry BuildLand(double w, double h)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var shape in Land)
            {
                ctx.BeginFigure(Project(shape[0], shape[1], w, h), true, true);
                var points = new List<Point>();
                for (var i = 2; i < shape.Length; i += 2)
                    points.Add(Project(shape[i], shape[i + 1], w, h));
                ctx.PolyLineTo(points, false, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
