using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Restyle.App.Docking;

/// <summary>
/// A tiny, pre-blurred copy of the current desktop wallpaper. Glass panels draw the part that's behind
/// them, which looks like frosted glass but costs nothing per frame (no live blur, no GPU needed).
/// </summary>
internal static class WallpaperBackdrop
{
    private const int Width = 200; // the whole screen squeezed into 200 px wide: blurry by design
    private static BitmapSource? _image;
    private static string? _key;

    /// <summary>The blurred wallpaper, stretched to the primary screen's shape; null if it can't be read.</summary>
    public static BitmapSource? Image
    {
        get
        {
            var path = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
            string key;
            try
            {
                key = string.IsNullOrEmpty(path) || !File.Exists(path) ? "" : path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception)
            {
                key = "";
            }

            if (key != _key)
            {
                _key = key;
                _image = key.Length == 0 ? null : Build(path!);
            }

            return _image;
        }
    }

    /// <summary>A small, blurred copy of any picture, cropped to the screen's shape (also used for lock screen panels).</summary>
    internal static BitmapSource? Build(string path)
    {
        try
        {
            var decoded = new BitmapImage();
            decoded.BeginInit();
            decoded.UriSource = new Uri(path);
            decoded.DecodePixelWidth = Width * 2;
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.EndInit();

            // "Fill" placement: crop the picture to the screen's shape, like Windows does.
            var screenAspect = GetSystemMetrics(0) / (double)Math.Max(1, GetSystemMetrics(1));
            int w = decoded.PixelWidth, h = decoded.PixelHeight;
            var crop = w / (double)h > screenAspect
                ? new Int32Rect((int)((w - h * screenAspect) / 2), 0, (int)(h * screenAspect), h)
                : new Int32Rect(0, (int)((h - w / screenAspect) / 2), w, (int)(w / screenAspect));
            var small = new TransformedBitmap(new CroppedBitmap(decoded, crop), new ScaleTransform(Width / (double)crop.Width, Width / (double)crop.Width));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Pbgra32, null, 0);

            int pw = bgra.PixelWidth, ph = bgra.PixelHeight;
            var pixels = new byte[pw * ph * 4];
            bgra.CopyPixels(pixels, pw * 4, 0);
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(pixels, pw, ph, 3, horizontal: true);
                BoxBlur(pixels, pw, ph, 3, horizontal: false);
            }

            var result = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Pbgra32, null, pixels, pw * 4);
            result.Freeze();
            return result;
        }
        catch (Exception)
        {
            return null; // unreadable picture: glass falls back to a plain tint
        }
    }

    private static void BoxBlur(byte[] px, int w, int h, int radius, bool horizontal)
    {
        var lines = horizontal ? h : w;
        var length = horizontal ? w : h;
        var buffer = new int[length * 4];
        for (var line = 0; line < lines; line++)
        {
            int Index(int i) => horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
            for (var i = 0; i < length; i++)
            {
                var o = Index(i);
                for (var c = 0; c < 4; c++)
                    buffer[i * 4 + c] = px[o + c];
            }

            for (var i = 0; i < length; i++)
            {
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var k = Math.Max(0, i - radius); k <= Math.Min(length - 1, i + radius); k++, n++)
                {
                    b += buffer[k * 4];
                    g += buffer[k * 4 + 1];
                    r += buffer[k * 4 + 2];
                    a += buffer[k * 4 + 3];
                }

                var o = Index(i);
                px[o] = (byte)(b / n);
                px[o + 1] = (byte)(g / n);
                px[o + 2] = (byte)(r / n);
                px[o + 3] = (byte)(a / n);
            }
        }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}

/// <summary>
/// A rounded panel that looks like frosted ("liquid") glass: the blurred wallpaper behind it, a soft tint,
/// a light sheen at the top and a bright hairline edge. With <see cref="Glass"/> off it's a normal Border.
/// </summary>
internal class GlassBorder : Border
{
    private Window? _window;

    public GlassBorder()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window is not null)
                _window.LocationChanged += OnMoved;
        };
        Unloaded += (_, _) =>
        {
            if (_window is not null)
                _window.LocationChanged -= OnMoved;
        };
    }

    public bool Glass { get; set; } = true;

    /// <summary>Color laid over the blurred wallpaper (keeps white text readable).</summary>
    public Color Tint { get; set; } = Color.FromArgb(0x48, 0x14, 0x14, 0x1C);

    private void OnMoved(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Redraws every glass panel in a window (e.g. after the wallpaper changed).</summary>
    public static void RefreshAll(DependencyObject root)
    {
        if (root is GlassBorder glass)
            glass.InvalidateVisual();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshAll(VisualTreeHelper.GetChild(root, i));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Glass || Background is ImageBrush)
        {
            base.OnRender(dc); // plain mode, or album art showing
            return;
        }

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        var radius = CornerRadius.TopLeft;
        dc.PushClip(new RectangleGeometry(bounds, radius, radius));

        // The wallpaper behind this panel, mapped from screen pixels into our own coordinates
        // (this also works when the panel is scaled down to fit a small screen).
        if (WallpaperBackdrop.Image is { } backdrop && PresentationSource.FromVisual(this) is not null)
        {
            var p0 = PointToScreen(new Point(0, 0));
            var p1 = PointToScreen(new Point(100, 100));
            var sx = (p1.X - p0.X) / 100;
            var sy = (p1.Y - p0.Y) / 100;
            if (sx > 0 && sy > 0)
                dc.DrawImage(backdrop, new Rect(-p0.X / sx, -p0.Y / sy, ScreenWidth / sx, ScreenHeight / sy));
        }
        else
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0x30, 0x30, 0x40)), null, bounds);
        }

        dc.DrawRectangle(new SolidColorBrush(Tint), null, bounds);
        var sheen = new LinearGradientBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90);
        dc.DrawRectangle(sheen, null, new Rect(0, 0, ActualWidth, Math.Min(ActualHeight, 60)));
        dc.Pop();

        var edge = new LinearGradientBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 90);
        var inset = new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
        dc.DrawRoundedRectangle(null, new Pen(edge, 1), inset, radius, radius);
    }

    private static int ScreenWidth => GetSystemMetrics(0);

    private static int ScreenHeight => GetSystemMetrics(1);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
