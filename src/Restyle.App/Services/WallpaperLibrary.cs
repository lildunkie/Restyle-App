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
        new("sunset-valley", "Sunset valley", DrawSunsetValley),
        new("night-sky", "Night sky", DrawNightSky),
        new("moonlit-hills", "Moonlit hills", DrawMoonlitHills),
        new("glass-bloom", "Glass bloom", DrawGlassBloom),
    ];

    /// <summary>Built-in lock screen pictures are named after their wallpaper plus this, e.g. "builtin:night-sky-lock".</summary>
    public const string LockSuffix = "-lock";

    /// <summary>
    /// Lock screen versions of each wallpaper: the same scene framed closer around its focal point
    /// (moved low, under the lock screen clock), with a softer top so the white clock stays readable.
    /// </summary>
    private static readonly Dictionary<string, (double X, double Y, double Zoom)> LockFocus = new()
    {
        ["gnome-blobs"] = (0.14, 0.86, 1.35), // the big blue disc moves out of the clock's way, top right
        ["plasma-shapes"] = (0.45, 0.50, 1.20),
        ["soft-light"] = (0.80, 0.20, 1.15),
        ["nord-waves"] = (0.74, 0.30, 1.20),
        ["mint-hills"] = (0.70, 0.35, 1.20),
        ["aqua-waves"] = (0.50, 0.55, 1.20),
        ["midnight-waves"] = (0.60, 0.40, 1.20),
        ["aubergine"] = (0.85, 0.85, 1.20),
        ["pop-peaks"] = (0.70, 0.30, 1.60), // close up, the sun sits just under the clock
        ["sunset-valley"] = (0.555, 0.60, 1.30),
        ["night-sky"] = (0.50, 0.55, 1.15),
        ["moonlit-hills"] = (0.50, 0.50, 1.25),
        ["glass-bloom"] = (0.70, 0.62, 1.50), // framed on the pink orb; the small orange one leaves the top
    };

    /// <summary>Bright wallpapers need a deeper shade at the top for the white lock screen clock.</summary>
    private static readonly HashSet<string> LightWallpapers = ["soft-light", "mint-hills", "aqua-waves"];

    /// <summary>Every built-in lock screen picture, as ("builtin:id-lock", name).</summary>
    public IEnumerable<(string Path, string Name)> LockScreens =>
        All.Select(w => (Prefix + w.Id + LockSuffix, w.Name));

    /// <summary>Turns "builtin:id" into a real file (rendering it if needed); expands %VARS% otherwise.</summary>
    public string Resolve(string path)
    {
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return Environment.ExpandEnvironmentVariables(path);

        var id = path[Prefix.Length..];
        var isLock = id.EndsWith(LockSuffix, StringComparison.OrdinalIgnoreCase);
        var baseId = isLock ? id[..^LockSuffix.Length] : id;
        var wallpaper = All.FirstOrDefault(w => w.Id.Equals(baseId, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"There's no built-in wallpaper called '{id}'.");
        return isLock ? EnsureLockFile(wallpaper) : EnsureFile(wallpaper);
    }

    /// <summary>Where a built-in lock screen picture ("builtin:id-lock") is saved once rendered.</summary>
    public string LockFileFor(string builtinPath) => Path.Combine(Folder, builtinPath[Prefix.Length..] + ".png");

    public string EnsureLockFile(BuiltInWallpaper wallpaper)
    {
        var file = Path.Combine(Folder, wallpaper.Id + LockSuffix + ".png");
        if (File.Exists(file))
            return file;

        var (width, height) = ScreenSize();
        Directory.CreateDirectory(Folder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(RenderLock(wallpaper, width, height)));
        using (var stream = File.Create(file))
            encoder.Save(stream);
        return file;
    }

    public static BitmapSource RenderLock(BuiltInWallpaper wallpaper, int width, int height)
    {
        var (fx, fy, zoom) = LockFocus.GetValueOrDefault(wallpaper.Id, (0.5, 0.5, 1.2));
        double w = width, h = height;

        // Zoom in, putting the focal point low and centered, without ever showing past the picture's edges.
        var tx = Math.Clamp(0.5 * w - zoom * fx * w, w - zoom * w, 0);
        var ty = Math.Clamp(0.60 * h - zoom * fy * h, h - zoom * h, 0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.PushTransform(new MatrixTransform(zoom, 0, 0, zoom, tx, ty));
            wallpaper.Draw(dc, w, h);
            dc.Pop();

            // A soft shade at the top for the clock and date, and a gentle vignette.
            var light = LightWallpapers.Contains(wallpaper.Id);
            dc.DrawRectangle(new LinearGradientBrush(C(light ? "#1A2233" : "#000000", light ? 0.75 : 0.40), C("#000000", 0), 90), null, new Rect(0, 0, w, h * (light ? 0.58 : 0.48)));
            var vignette = new RadialGradientBrush(C("#000000", 0), C("#000000", 0.35)) { RadiusX = 0.75, RadiusY = 0.75 };
            vignette.GradientStops.Insert(1, new GradientStop(C("#000000", 0), 0.55));
            dc.DrawRectangle(vignette, null, new Rect(0, 0, w, h));
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
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
                var id = path[Prefix.Length..];
                var isLock = id.EndsWith(LockSuffix, StringComparison.OrdinalIgnoreCase);
                var baseId = isLock ? id[..^LockSuffix.Length] : id;
                var wallpaper = All.FirstOrDefault(w => w.Id.Equals(baseId, StringComparison.OrdinalIgnoreCase));
                if (wallpaper is not null)
                    image = isLock ? RenderLock(wallpaper, 320, 180) : Render(wallpaper, 320, 180);
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

    /// <summary>Purple dusk over layered mountains, a glowing sun, a lake, pines and a deer.</summary>
    private static void DrawSunsetValley(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        var sky = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        sky.GradientStops.Add(new GradientStop(C("#2A1B5E"), 0));
        sky.GradientStops.Add(new GradientStop(C("#5B3290"), 0.30));
        sky.GradientStops.Add(new GradientStop(C("#B45AA0"), 0.52));
        sky.GradientStops.Add(new GradientStop(C("#F29BAE"), 0.66));
        sky.GradientStops.Add(new GradientStop(C("#F7C1B4"), 0.72));
        dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

        // Stars in the upper sky (fixed seed: the same picture every time).
        var random = new Random(7);
        for (var i = 0; i < 160; i++)
        {
            var y = Math.Pow(random.NextDouble(), 1.6) * 0.42 * h;
            var alpha = 0.25 + random.NextDouble() * 0.6 * (1 - y / (0.42 * h));
            dc.DrawEllipse(new SolidColorBrush(C("#FFFFFF", alpha)), null, new Point(random.NextDouble() * w, y), (0.8 + random.NextDouble() * 1.4) * u, (0.8 + random.NextDouble() * 1.4) * u);
        }

        // Sun with soft rings.
        var sun = new Point(0.555 * w, 0.585 * h);
        Glow(dc, sun.X, sun.Y, 620 * u, "#FFC9A8", 0.35);
        for (var r = 5; r >= 1; r--)
            dc.DrawEllipse(new SolidColorBrush(C("#FFD8B8", 0.07 + 0.03 * (5 - r))), null, sun, (60 + r * 46) * u, (60 + r * 46) * u);
        dc.DrawEllipse(new SolidColorBrush(C("#FFEBC8")), null, sun, 52 * u, 52 * u);

        // Birds.
        var birdPen = new Pen(new SolidColorBrush(C("#3A1F5C", 0.75)), 2.2 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        foreach (var (bx, by, s) in new[] { (0.16, 0.40, 1.0), (0.19, 0.42, 0.8), (0.22, 0.395, 0.9), (0.245, 0.43, 0.7), (0.27, 0.41, 0.75), (0.205, 0.45, 0.6), (0.30, 0.44, 0.6) })
        {
            var x = bx * w;
            var y = by * h;
            var k = 9 * s * u;
            var bird = new PathFigure { StartPoint = new Point(x - k, y - k * 0.5) };
            bird.Segments.Add(new QuadraticBezierSegment(new Point(x - k * 0.4, y - k * 0.6), new Point(x, y), true));
            bird.Segments.Add(new QuadraticBezierSegment(new Point(x + k * 0.4, y - k * 0.6), new Point(x + k, y - k * 0.5), true));
            dc.DrawGeometry(null, birdPen, new PathGeometry([bird]));
        }

        // Mountain ranges, far (pale) to near (dark).
        Ridge(dc, w, h, 0.63, 0.12, 11, 9, "#C77BAE", 0.85);
        Ridge(dc, w, h, 0.66, 0.11, 23, 7, "#9C579E", 0.95);
        Ridge(dc, w, h, 0.69, 0.10, 5, 6, "#6E3A88", 1);

        // Lake with the sun's reflection.
        var lakeTop = 0.715 * h;
        var lake = new LinearGradientBrush(C("#E09AB4"), C("#6B3D8C"), 90);
        dc.DrawRectangle(lake, null, new Rect(0, lakeTop, w, 0.12 * h));
        for (var i = 0; i < 7; i++)
        {
            var y = lakeTop + (8 + i * 13) * u;
            var half = (90 - i * 10) * u;
            dc.DrawRoundedRectangle(new SolidColorBrush(C("#FFE6C8", 0.55 - i * 0.06)), null, new Rect(sun.X - half, y, half * 2, 3 * u), 1.5 * u, 1.5 * u);
        }

        // Near shore and hills.
        Ridge(dc, w, h, 0.83, 0.05, 31, 5, "#3E1F5E", 1);
        Ridge(dc, w, h, 0.90, 0.04, 41, 4, "#26123F", 1);

        // Pines on both sides.
        var pine = new SolidColorBrush(C("#1B0D30"));
        foreach (var (px, height) in new[] { (0.02, 0.34), (0.06, 0.26), (0.10, 0.20), (0.135, 0.15), (0.84, 0.17), (0.88, 0.24), (0.93, 0.31), (0.975, 0.38) })
            Pine(dc, px * w, h, height * h, pine);

        Deer(dc, 0.385 * w, 0.85 * h, 1.0 * u, pine);
    }

    /// <summary>A deep blue starry sky with the Milky Way, a bright sun, soft clouds and a grassy hill.</summary>
    private static void DrawNightSky(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        var sky = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        sky.GradientStops.Add(new GradientStop(C("#0A1838"), 0));
        sky.GradientStops.Add(new GradientStop(C("#143067"), 0.45));
        sky.GradientStops.Add(new GradientStop(C("#2B5C9C"), 1));
        dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));

        // The Milky Way: a soft diagonal glow packed with tiny stars.
        var random = new Random(11);
        var band = new RotateTransform(-38, 0.48 * w, 0.42 * h);
        dc.PushTransform(band);
        dc.DrawEllipse(new RadialGradientBrush(C("#9FB8FF", 0.22), C("#9FB8FF", 0)), null, new Point(0.48 * w, 0.42 * h), 0.75 * w, 0.10 * h);
        for (var i = 0; i < 900; i++)
        {
            var x = 0.48 * w + (random.NextDouble() - 0.5) * 1.5 * w;
            var spread = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 0.12 * h; // bell curve
            var r = (0.5 + random.NextDouble()) * u;
            dc.DrawEllipse(new SolidColorBrush(C("#FFFFFF", 0.25 + random.NextDouble() * 0.6)), null, new Point(x, 0.42 * h + spread), r, r);
        }

        dc.Pop();
        for (var i = 0; i < 260; i++)
        {
            var r = (0.6 + random.NextDouble() * 1.3) * u;
            dc.DrawEllipse(new SolidColorBrush(C("#FFFFFF", 0.3 + random.NextDouble() * 0.6)), null, new Point(random.NextDouble() * w, random.NextDouble() * 0.85 * h), r, r);
        }

        // A bright sun (or moon) with rays, top right.
        var sun = new Point(0.885 * w, 0.14 * h);
        Glow(dc, sun.X, sun.Y, 260 * u, "#CFE6FF", 0.55);
        var rayPen = new Pen(new SolidColorBrush(C("#FFFFFF", 0.55)), 2 * u);
        for (var i = 0; i < 28; i++)
        {
            var a = i * Math.PI * 2 / 28;
            var len = (i % 2 == 0 ? 92 : 78) * u;
            dc.DrawLine(rayPen, new Point(sun.X + Math.Cos(a) * 66 * u, sun.Y + Math.Sin(a) * 66 * u), new Point(sun.X + Math.Cos(a) * len, sun.Y + Math.Sin(a) * len));
        }

        dc.DrawEllipse(new SolidColorBrush(C("#FFFFFF")), null, sun, 62 * u, 62 * u);

        // Soft clouds lit from above.
        Cloud(dc, 0.16 * w, 0.64 * h, 0.30 * w, u, 21);
        Cloud(dc, 0.47 * w, 0.60 * h, 0.24 * w, u, 22);
        Cloud(dc, 0.80 * w, 0.42 * h, 0.30 * w, u, 23);
        Cloud(dc, 0.66 * w, 0.27 * h, 0.13 * w, u, 24);
        Cloud(dc, 0.10 * w, 0.10 * h, 0.20 * w, u, 25);
        Cloud(dc, 0.33 * w, 0.47 * h, 0.10 * w, u, 26);
        Cloud(dc, 0.93 * w, 0.68 * h, 0.12 * w, u, 27);

        // A dark grassy hill rising to the right, with grass blades along its edge.
        var hill = new PathFigure { StartPoint = new Point(0.30 * w, h), IsClosed = true };
        hill.Segments.Add(new BezierSegment(new Point(0.55 * w, 0.92 * h), new Point(0.80 * w, 0.62 * h), new Point(1.02 * w, 0.40 * h), true));
        hill.Segments.Add(new LineSegment(new Point(1.02 * w, h), true));
        var hillGeometry = new PathGeometry([hill]);
        var ground = new SolidColorBrush(C("#091428"));
        dc.DrawGeometry(ground, null, hillGeometry);
        dc.DrawRectangle(new LinearGradientBrush(C("#0A1630", 0), C("#071022"), 90), null, new Rect(0, 0.88 * h, w, 0.12 * h));
        var bladePen = new Pen(ground, 2.2 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (var i = 0; i < 700; i++)
        {
            var t = random.NextDouble();
            hillGeometry.GetPointAtFractionLength(t * 0.5, out var p, out _); // along the curved top edge
            if (p.Y > h - 2 || p.X < 0.3 * w)
                continue;
            var len = (8 + random.NextDouble() * 26) * u;
            dc.DrawLine(bladePen, p, new Point(p.X + (random.NextDouble() - 0.3) * len * 0.6, p.Y - len));
        }
    }

    /// <summary>
    /// A soft, fluffy cloud: many see-through puffs that fade at their edges, taller in the middle,
    /// light on top and a deeper blue underneath.
    /// </summary>
    private static void Cloud(DrawingContext dc, double cx, double cy, double width, double u, int seed)
    {
        var random = new Random(seed);
        var puffs = 26 + random.Next(10);
        var list = new List<(double X, double Y, double R)>();
        for (var i = 0; i < puffs; i++)
        {
            var t = random.NextDouble() - 0.5;
            var height = 1 - Math.Abs(t) * 1.7; // taller in the middle
            var r = width * (0.035 + 0.06 * Math.Max(0.15, height) * (0.6 + random.NextDouble() * 0.6));
            var y = cy - random.NextDouble() * Math.Max(0, height) * width * 0.14;
            list.Add((cx + t * width, y, r));
        }

        // Shadow side first, then the lit puffs on top (sorted bottom to top so upper puffs stay bright).
        foreach (var (x, y, r) in list)
            dc.DrawEllipse(Puff("#2F5694", 0.30), null, new Point(x + 4 * u, y + r * 0.35), r * 1.05, r * 0.75);
        foreach (var (x, y, r) in list.OrderByDescending(p => p.Y))
        {
            var lift = Math.Clamp((cy - y) / (width * 0.14), 0, 1); // 0 at the base, 1 at the top
            dc.DrawEllipse(Puff(lift > 0.45 ? "#E6F0FF" : "#A9C6F0", 0.55 + 0.35 * lift), null, new Point(x, y), r, r * 0.82);
        }
    }

    private static RadialGradientBrush Puff(string color, double alpha) => new(
    [
        new GradientStop(C(color, alpha), 0),
        new GradientStop(C(color, alpha * 0.8), 0.55),
        new GradientStop(C(color, 0), 1),
    ]);

    /// <summary>A warm full moon over dark rolling hills with pines.</summary>
    private static void DrawMoonlitHills(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        dc.DrawRectangle(new RadialGradientBrush(C("#2C2A2E"), C("#121214")) { RadiusX = 0.8, RadiusY = 0.9, Center = new Point(0.5, 0.45), GradientOrigin = new Point(0.5, 0.45) }, null, new Rect(0, 0, w, h));

        var random = new Random(5);
        for (var i = 0; i < 140; i++)
        {
            var r = (0.8 + random.NextDouble() * 1.6) * u;
            dc.DrawEllipse(new SolidColorBrush(C(random.NextDouble() < 0.3 ? "#F6D9A0" : "#FFFFFF", 0.25 + random.NextDouble() * 0.55)), null, new Point(random.NextDouble() * w, random.NextDouble() * 0.7 * h), r, r);
        }

        var moon = new Point(0.5 * w, 0.46 * h);
        Glow(dc, moon.X, moon.Y, 280 * u, "#F2C98A", 0.28);
        dc.DrawEllipse(new RadialGradientBrush(C("#F7D7A0"), C("#E3AE6A")) { GradientOrigin = new Point(0.35, 0.3) }, null, moon, 58 * u, 58 * u);
        foreach (var (dx, dy, r) in new[] { (-18.0, -12.0, 11.0), (16, 8, 8), (-6, 22, 6), (22, -20, 5) })
            dc.DrawEllipse(new SolidColorBrush(C("#D9A262", 0.45)), null, new Point(moon.X + dx * u, moon.Y + dy * u), r * u, r * u);

        // Pines along the far ridge, then two big rolling hills in front.
        var pine = new SolidColorBrush(C("#1F1A19"));
        foreach (var (px, height) in new[] { (0.20, 0.10), (0.235, 0.14), (0.27, 0.09), (0.34, 0.12), (0.37, 0.08), (0.47, 0.11), (0.50, 0.15), (0.53, 0.10), (0.62, 0.13), (0.66, 0.17), (0.70, 0.12), (0.74, 0.14), (0.78, 0.09) })
            Pine(dc, px * w, 0.70 * h, height * h, pine);

        Hill(dc, w, h, -0.05, 0.58, 0.68, "#3A302A", "#211B18");
        Hill(dc, w, h, 0.38, 1.10, 0.64, "#2E2622", "#191513");
    }

    private static void Hill(DrawingContext dc, double w, double h, double left, double right, double top, string light, string dark)
    {
        var figure = new PathFigure { StartPoint = new Point(left * w, h), IsClosed = true };
        figure.Segments.Add(new BezierSegment(new Point((left + (right - left) * 0.25) * w, top * h), new Point((left + (right - left) * 0.75) * w, top * h), new Point(right * w, h), true));
        dc.DrawGeometry(new LinearGradientBrush(C(light), C(dark), 90), null, new PathGeometry([figure]));
    }

    /// <summary>Big soft blobs of color: the classic backdrop that makes frosted glass glow.</summary>
    private static void DrawGlassBloom(DrawingContext dc, double w, double h)
    {
        var u = h / 1080;
        Background(dc, w, h, "#100C2A", "#1A1035", diagonal: true);
        Glow(dc, 0.18 * w, 0.28 * h, 620 * u, "#4F5BFF", 0.85);
        Glow(dc, 0.78 * w, 0.22 * h, 560 * u, "#FF5FA2", 0.75);
        Glow(dc, 0.66 * w, 0.82 * h, 600 * u, "#FFB15B", 0.65);
        Glow(dc, 0.22 * w, 0.85 * h, 520 * u, "#2FD3FF", 0.6);
        Glow(dc, 0.48 * w, 0.50 * h, 460 * u, "#9B5BFF", 0.6);
        Glow(dc, 0.92 * w, 0.62 * h, 320 * u, "#FF7B5B", 0.45);

        // A few crisp glossy orbs so the glass has something to blur.
        Orb(dc, 0.30 * w, 0.40 * h, 120 * u, "#7DA2FF", "#3B4BD8");
        Orb(dc, 0.70 * w, 0.62 * h, 170 * u, "#FF9AC6", "#C2367A");
        Orb(dc, 0.58 * w, 0.14 * h, 70 * u, "#FFD08A", "#E07B2E"); // kept clear of where the widgets sit
        Orb(dc, 0.42 * w, 0.86 * h, 60 * u, "#7FF0FF", "#1C9CC8");
    }

    private static void Orb(DrawingContext dc, double x, double y, double r, string light, string dark)
    {
        dc.DrawEllipse(new RadialGradientBrush(C(light), C(dark)) { GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.4, 0.35), RadiusX = 0.75, RadiusY = 0.75 }, null, new Point(x, y), r, r);
        dc.DrawEllipse(new RadialGradientBrush(C("#FFFFFF", 0.55), C("#FFFFFF", 0)), null, new Point(x - r * 0.35, y - r * 0.4), r * 0.35, r * 0.25);
    }

    /// <summary>A jagged mountain silhouette across the whole width, filled to the bottom.</summary>
    private static void Ridge(DrawingContext dc, double w, double h, double baseY, double amplitude, int seed, int peaks, string color, double alpha)
    {
        var random = new Random(seed);
        var figure = new PathFigure { StartPoint = new Point(0, h), IsClosed = true };
        var points = new List<Point> { new(0, (baseY - amplitude * random.NextDouble() * 0.5) * h) };
        var step = w / (peaks * 2);
        for (var i = 1; i <= peaks * 2; i++)
        {
            var peak = i % 2 == 1;
            var y = peak ? baseY - amplitude * (0.45 + random.NextDouble() * 0.55) : baseY - amplitude * random.NextDouble() * 0.25;
            points.Add(new Point(i * step + (random.NextDouble() - 0.5) * step * 0.6, y * h));
        }

        points.Add(new Point(w, points[^1].Y));
        points.Add(new Point(w, h));
        figure.Segments.Add(new PolyLineSegment(points, true));
        dc.DrawGeometry(new SolidColorBrush(C(color, alpha)), null, new PathGeometry([figure]));
    }

    private static void Pine(DrawingContext dc, double x, double bottom, double height, Brush brush)
    {
        var figure = new PathFigure { StartPoint = new Point(x, bottom - height), IsClosed = true };
        var points = new List<Point>();
        const int tiers = 7;
        for (var i = 1; i <= tiers; i++)
        {
            var y = bottom - height + height * i / (tiers + 1.0);
            var half = height * 0.055 * (0.6 + i * 0.55);
            points.Add(new Point(x + half, y));
            points.Add(new Point(x + half * 0.45, y - height * 0.025));
        }

        points.Add(new Point(x + height * 0.02, bottom));
        points.Add(new Point(x - height * 0.02, bottom));
        for (var i = tiers; i >= 1; i--)
        {
            var y = bottom - height + height * i / (tiers + 1.0);
            var half = height * 0.055 * (0.6 + i * 0.55);
            points.Add(new Point(x - half * 0.45, y - height * 0.025));
            points.Add(new Point(x - half, y));
        }

        figure.Segments.Add(new PolyLineSegment(points, true));
        dc.DrawGeometry(brush, null, new PathGeometry([figure]));
    }

    /// <summary>A stag silhouette facing left, standing at (x, ground).</summary>
    private static void Deer(DrawingContext dc, double x, double ground, double s, Brush brush)
    {
        Point P(double dx, double dy) => new(x + dx * s, ground - dy * s);
        var body = new PathFigure { StartPoint = P(-44, 80), IsClosed = true };
        body.Segments.Add(new PolyLineSegment(
        [
            P(-50, 100), P(-56, 118), P(-64, 136), P(-78, 138), P(-84, 144), P(-78, 152),  // chest, throat, muzzle
            P(-64, 158), P(-54, 152), P(-44, 130), P(-34, 116),                            // head and back of the neck
            P(10, 112), P(40, 116), P(52, 108), P(54, 92), P(48, 80),                      // back and rump
            P(46, 44), P(50, 0), P(44, 0), P(38, 44), P(34, 76),                           // hind leg
            P(24, 76), P(20, 44), P(24, 0), P(18, 0), P(12, 44), P(12, 74),                // second hind leg
            P(-22, 74), P(-26, 44), P(-22, 0), P(-28, 0), P(-32, 44), P(-34, 76),          // front leg
            P(-38, 78), P(-40, 44), P(-36, 0), P(-42, 0), P(-46, 44),                      // second front leg
        ], true));
        dc.DrawGeometry(brush, null, new PathGeometry([body]));

        // Antlers.
        var pen = new Pen(brush, 3.4 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        foreach (var side in new[] { -1.0, 1.0 })
        {
            var b = side * 7;
            var antler = new PathFigure { StartPoint = P(-60, 156) };
            antler.Segments.Add(new PolyLineSegment([P(-58 + b, 178), P(-52 + b * 2, 200), P(-46 + b * 2.6, 220)], true));
            dc.DrawGeometry(null, pen, new PathGeometry([antler]));
            dc.DrawLine(pen, P(-58 + b, 178), P(-70 + b, 190));
            dc.DrawLine(pen, P(-52 + b * 2, 200), P(-62 + b * 2, 214));
            dc.DrawLine(pen, P(-52 + b * 2, 200), P(-38 + b * 2, 208));
        }

        dc.DrawLine(new Pen(brush, 5 * s) { EndLineCap = PenLineCap.Round }, P(-52, 152), P(-44, 160)); // ear
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
