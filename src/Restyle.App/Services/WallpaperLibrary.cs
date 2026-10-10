using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Restyle.App.Services;

public sealed record BuiltInWallpaper(string Id, string Name, Action<DrawingContext, double, double> Draw);

/// <summary>
/// Linux-inspired wallpapers drawn by Restyle itself (original artwork, free to use), rendered at the
/// screen's real resolution on first use and cached in %LOCALAPPDATA%\Restyle\Wallpapers.
/// Keeps the exe small: no image files are bundled.
/// </summary>
public sealed class WallpaperLibrary(string folder)
{
    public const string Prefix = "builtin:";

    private readonly Dictionary<string, ImageSource?> _thumbnails = new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; } = folder;

    public IReadOnlyList<BuiltInWallpaper> All { get; } =
    [
        new("gnome-blobs", "Blobs (GNOME-style)", DrawBlobs),
        new("plasma-shapes", "Shapes (Plasma-style)", DrawShapes),
        new("soft-light", "Soft light", DrawSoftLight),
        new("nord-waves", "Nord waves", DrawNordWaves),
        new("mint-hills", "Mint hills", DrawMintHills),
        new("aqua-waves", "Aqua waves (Mac-style)", DrawAquaWaves),
        new("midnight-waves", "Midnight waves (Mac-style)", DrawMidnightWaves),
        new("aubergine", "Aubergine (Ubuntu-style)", DrawAubergine),
        new("pop-peaks", "Peaks (Pop!_OS-style)", DrawPopPeaks),
    ];

    /// <summary>Turns "builtin:id" into a real file (rendering it if needed); expands %VARS% otherwise.</summary>
    public string Resolve(string path)
    {
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return Environment.ExpandEnvironmentVariables(path);

        var id = path[Prefix.Length..];
        var wallpaper = All.FirstOrDefault(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"There's no built-in wallpaper called '{id}'.");
        return EnsureFile(wallpaper);
    }

    public string FileFor(BuiltInWallpaper wallpaper) => Path.Combine(Folder, wallpaper.Id + ".png");

    public string EnsureFile(BuiltInWallpaper wallpaper)
    {
        var file = FileFor(wallpaper);
        if (File.Exists(file))
            return file;

        var (width, height) = ScreenSize();
        Directory.CreateDirectory(Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(wallpaper, width, height)));
        using (var stream = File.Create(file))
            encoder.Save(stream);
        return file;
    }

    /// <summary>True if the thumbnail was already loaded, so it can be drawn without any work.</summary>
    public bool TryGetCachedThumbnail(string? path, out ImageSource? image)
    {
        image = null;
        return string.IsNullOrWhiteSpace(path) || _thumbnails.TryGetValue(path, out image);
    }

    /// <summary>A small preview for any wallpaper path (built-in or file). Null if it can't be loaded.</summary>
    public ImageSource? Thumbnail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (_thumbnails.TryGetValue(path, out var cached))
            return cached;

        ImageSource? image = null;
        try
        {
            if (path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                var wallpaper = All.FirstOrDefault(w => w.Id.Equals(path[Prefix.Length..], StringComparison.OrdinalIgnoreCase));
                if (wallpaper is not null)
                    image = Render(wallpaper, 320, 180);
            }
            else
            {
                var full = Environment.ExpandEnvironmentVariables(path);
                if (File.Exists(full))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(full);
                    bitmap.DecodePixelWidth = 320;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    image = bitmap;
                }
            }
        }
        catch (Exception)
        {
            image = null; // unreadable/unsupported image: show a plain tile instead
        }

        _thumbnails[path] = image;
        return image;
    }

    public static BitmapSource Render(BuiltInWallpaper wallpaper, int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            wallpaper.Draw(dc, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Width, int Height) ScreenSize()
    {
        // The app is per-monitor DPI aware, so these are physical pixels.
        var w = GetSystemMetrics(0);
        var h = GetSystemMetrics(1);
        return (Math.Max(w, 1920), Math.Max(h, 1080));
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    // ---- Artwork -------------------------------------------------------------------------

    private static Color C(string hex, double alpha = 1)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        c.A = (byte)Math.Round(alpha * 255);
        return c;
    }

    private static void Background(DrawingContext dc, double w, double h, string top, string bottom, bool diagonal = false)
    {
        var brush = new LinearGradientBrush(C(top), C(bottom), diagonal ? new Point(0, 0) : new Point(0.5, 0),
            diagonal ? new Point(1, 1) : new Point(0.5, 1));
        dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
    }

    private static void Glow(DrawingContext dc, double x, double y, double r, string color, double alpha)
    {
        var brush = new RadialGradientBrush(
        [
            new GradientStop(C(color, alpha), 0),
            new GradientStop(C(color, alpha * 0.45), 0.5),
            new GradientStop(C(color, 0), 1),
        ]);
        dc.DrawEllipse(brush, null, new Point(x, y), r, r);
    }

    private static void Disc(DrawingContext dc, double x, double y, double r, string top, string bottom, double u)
    {
        dc.DrawEllipse(new SolidColorBrush(C("#000000", 0.22)), null, new Point(x + 6 * u, y + 12 * u), r, r);
        dc.DrawEllipse(new LinearGradientBrush(C(top), C(bottom), 90), null, new Point(x, y), r, r);
    }

    private static void Wave(DrawingContext dc, double w, double h, double y0, double y1, double amp, Brush fill)
    {
        var figure = new PathFigure { StartPoint = new Point(0, y0), IsClosed = true };
        figure.Segments.Add(new BezierSegment(new Point(w * 0.3, y0 - amp), new Point(w * 0.65, y1 + amp), new Point(w, y1), true));
        figure.Segments.Add(new LineSegment(new Point(w, h), true));
        figure.Segments.Add(new LineSegment(new Point(0, h), true));
        dc.DrawGeometry(fill, null, new PathGeometry([figure]));
    }

    private static void Triangle(DrawingContext dc, double w, double h, (double X, double Y)[] p, string color, double alpha)
    {
        var figure = new PathFigure { StartPoint = new Point(p[0].X * w, p[0].Y * h), IsClosed = true };
        figure.Segments.Add(new PolyLineSegment(p.Skip(1).Select(q => new Point(q.X * w, q.Y * h)), true));
        dc.DrawGeometry(new SolidColorBrush(C(color, alpha)), null, new PathGeometry([figure]));
    }

    private static LinearGradientBrush Grad(string left, string right, double alpha = 1) =>
        new(C(left, alpha), C(right, alpha), 0);

    private static void DrawAquaWaves(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#CFE3FA", "#F7E6F0");
        Glow(dc, 0.20 * w, 0.15 * h, 640 * u, "#FFFFFF", 0.75);
        Wave(dc, w, h, 0.46 * h, 0.30 * h, 150 * u, Grad("#8FBDF7", "#B39AEE", 0.85));
        Wave(dc, w, h, 0.62 * h, 0.50 * h, 130 * u, Grad("#5A8CEB", "#8467DC"));
        Wave(dc, w, h, 0.78 * h, 0.70 * h, 100 * u, Grad("#3D63D2", "#6145BE"));
        Wave(dc, w, h, 0.91 * h, 0.86 * h, 60 * u, Grad("#2B419F", "#412C8A"));
    }

    private static void DrawMidnightWaves(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#0A0F24", "#1A1338");
        Glow(dc, 0.75 * w, 0.20 * h, 620 * u, "#6A4BD8", 0.35);
        Glow(dc, 0.15 * w, 0.35 * h, 520 * u, "#2F6FE0", 0.25);
        Wave(dc, w, h, 0.50 * h, 0.36 * h, 150 * u, Grad("#28407F", "#4A2F86", 0.85));
        Wave(dc, w, h, 0.65 * h, 0.54 * h, 120 * u, Grad("#1F3170", "#3A2273"));
        Wave(dc, w, h, 0.80 * h, 0.72 * h, 90 * u, Grad("#16224F", "#2A1855"));
        Wave(dc, w, h, 0.92 * h, 0.88 * h, 50 * u, Grad("#0E1636", "#1C1040"));
    }

    private static void DrawAubergine(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#2C001E", "#5E2750", diagonal: true);
        Glow(dc, 0.10 * w, 0.10 * h, 560 * u, "#77216F", 0.55);
        Glow(dc, 0.88 * w, 0.88 * h, 760 * u, "#E95420", 0.45);
        Wave(dc, w, h, 0.72 * h, 0.52 * h, 140 * u, Grad("#E95420", "#77216F", 0.22));
        Wave(dc, w, h, 0.84 * h, 0.68 * h, 110 * u, Grad("#E95420", "#5E2750", 0.25));
        Wave(dc, w, h, 0.94 * h, 0.84 * h, 70 * u, Grad("#C34113", "#3A0C2E", 0.45));
    }

    private static void DrawPopPeaks(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#1F2733", "#33404F");
        Glow(dc, 0.70 * w, 0.30 * h, 460 * u, "#FAA41A", 0.35);
        dc.DrawEllipse(new SolidColorBrush(C("#FBB86C", 0.9)), null, new Point(0.70 * w, 0.30 * h), 70 * u, 70 * u);
        Triangle(dc, w, h, [(-0.1, 1), (0.25, 0.38), (0.6, 1)], "#48B9C7", 0.40);
        Triangle(dc, w, h, [(0.35, 1), (0.72, 0.33), (1.1, 1)], "#48B9C7", 0.30);
        Triangle(dc, w, h, [(0, 1), (0.45, 0.55), (0.85, 1)], "#2B6E78", 0.95);
        Triangle(dc, w, h, [(0.5, 1), (0.82, 0.60), (1.1, 1)], "#1A3D44", 0.95);
    }

    private static void DrawBlobs(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#241F31", "#16131F", diagonal: true);
        Glow(dc, 0.22 * w, 0.30 * h, 620 * u, "#3584E4", 0.50);
        Glow(dc, 0.82 * w, 0.78 * h, 700 * u, "#9141AC", 0.42);
        Glow(dc, 0.60 * w, 0.12 * h, 380 * u, "#33C7DE", 0.18);
        Disc(dc, 0.80 * w, 0.26 * h, 230 * u, "#62A0EA", "#1C71D8", u);
        Disc(dc, 0.14 * w, 0.86 * h, 300 * u, "#C061CB", "#813D9C", u);
        Disc(dc, 0.52 * w, 0.60 * h, 64 * u, "#F6F5F4", "#C0BFBC", u);
    }

    private static void DrawShapes(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#0E1621", "#1B2838");
        Glow(dc, 0.30 * w, 0.75 * h, 760 * u, "#1D99F3", 0.32);
        Triangle(dc, w, h, [(0, 1), (0.45, 0.25), (0.72, 1)], "#1D99F3", 0.28);
        Triangle(dc, w, h, [(0.2, 1), (0.6, 0.45), (1, 1)], "#3DAEE9", 0.22);
        Triangle(dc, w, h, [(0.5, 1), (0.86, 0.15), (1.1, 1)], "#1ABC9C", 0.16);
        Triangle(dc, w, h, [(-0.1, 0.6), (0.25, 0.05), (0.4, 0.75)], "#2980B9", 0.18);
        Triangle(dc, w, h, [(0.55, 0.3), (0.95, -0.05), (1.05, 0.55)], "#3DAEE9", 0.12);
        Triangle(dc, w, h, [(0.35, 1), (0.5, 0.62), (0.62, 1)], "#FFFFFF", 0.06);
    }

    private static void DrawSoftLight(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#F6F8FA", "#E1E7EF", diagonal: true);
        Glow(dc, 0.80 * w, 0.18 * h, 760 * u, "#FFFFFF", 0.85);
        Glow(dc, 0.12 * w, 0.92 * h, 620 * u, "#C3CFDD", 0.55);
        Wave(dc, w, h, 0.72 * h, 0.60 * h, 90 * u, new SolidColorBrush(C("#D7DFE9", 0.75)));
        Wave(dc, w, h, 0.86 * h, 0.78 * h, 60 * u, new SolidColorBrush(C("#C9D3DF", 0.70)));
    }

    private static void DrawNordWaves(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#2E3440", "#3B4252");
        Glow(dc, 0.74 * w, 0.24 * h, 520 * u, "#88C0D0", 0.20);
        Wave(dc, w, h, 0.58 * h, 0.50 * h, 110 * u, new SolidColorBrush(C("#5E81AC", 0.55)));
        Wave(dc, w, h, 0.68 * h, 0.64 * h, 90 * u, new SolidColorBrush(C("#4C566A")));
        Wave(dc, w, h, 0.80 * h, 0.76 * h, 70 * u, new SolidColorBrush(C("#3B4252")));
        Wave(dc, w, h, 0.90 * h, 0.88 * h, 40 * u, new SolidColorBrush(C("#2E3440")));
    }

    private static void DrawMintHills(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#E3F4E8", "#9ED8B4");
        Glow(dc, 0.70 * w, 0.30 * h, 340 * u, "#FFFFFF", 0.85);
        Wave(dc, w, h, 0.60 * h, 0.50 * h, 80 * u, new SolidColorBrush(C("#6FBF8E")));
        Wave(dc, w, h, 0.70 * h, 0.66 * h, 70 * u, new SolidColorBrush(C("#4AA272")));
        Wave(dc, w, h, 0.82 * h, 0.78 * h, 60 * u, new SolidColorBrush(C("#2E7D57")));
        Wave(dc, w, h, 0.92 * h, 0.90 * h, 30 * u, new SolidColorBrush(C("#1E5C40")));
    }
}
