using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Restyle.App.Services;

namespace Restyle.App.Controls;

/// <summary>
/// A tiny drawing of a desktop (wallpaper, a window, the taskbar) showing what a preset will look like.
/// Drawn from the setting values, so it works for any preset, including ones you write yourself.
/// </summary>
public sealed class PresetPreview : FrameworkElement
{
    private Dictionary<string, JsonNode?> _values = [];

    public PresetPreview()
    {
        Width = 236;
        Height = 133;
    }

    /// <summary>The effective values: current settings with the preset's values on top.</summary>
    public Dictionary<string, JsonNode?> Values
    {
        get => _values;
        set
        {
            _values = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var appsDark = Str("appsTheme") == "dark";
        var systemDark = Str("systemTheme") == "dark";
        var accent = Accent();
        var centered = Str("taskbarAlignment") != "left";
        var transparent = Bool("transparency", true);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 6, 6));

        // Wallpaper: draw a placeholder first and load the thumbnail once the window is up.
        var wallpapers = AppServices.Current.Wallpapers;
        var path = Str("wallpaper", "path");
        if (!wallpapers.TryGetCachedThumbnail(path, out var thumb))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                wallpapers.Thumbnail(path);
                InvalidateVisual();
            });
        }

        if (thumb is not null)
            dc.DrawImage(thumb, Fill(thumb, w, h));
        else
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x68)), null, new Rect(0, 0, w, h));

        // Desktop icons
        if (Bool("desktopIcons", true))
        {
            var iconBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
            for (var i = 0; i < 3; i++)
                dc.DrawRoundedRectangle(iconBrush, null, new Rect(7, 7 + i * 15, 10, 10), 2, 2);
        }

        var dockOn = Bool("dock", false);
        if (dockOn && Bool("desktopWidgets", false))
        {
            switch (Str("widgetLayout"))
            {
                case "glass":
                    DrawGlassClock(dc, w, h, Bool("widgetGlass", false));
                    break;
                case "corners":
                    DrawCorners(dc, w, h, Bool("widgetGlass", false));
                    break;
                default:
                    DrawWidgets(dc, w, h, Bool("widgetGlass", false));
                    break;
            }

            if (Bool("dockBar", true))
                DrawBottom(dc, w, h, systemDark, accent, centered, transparent, dockOn);
            dc.Pop();
            return;
        }

        // A window
        var winRect = new Rect(w * 0.2, h * 0.14, w * 0.56, h * 0.56);
        var winBg = appsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3);
        var lineColor = appsDark ? Color.FromRgb(0x45, 0x45, 0x45) : Color.FromRgb(0xD6, 0xD6, 0xD6);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, Offset(winRect, 1, 2), 5, 5);
        dc.DrawRoundedRectangle(new SolidColorBrush(winBg), null, winRect, 4, 4);
        if (Bool("accentOnTitleBars", false))
        {
            dc.PushClip(new RectangleGeometry(winRect, 4, 4));
            dc.DrawRectangle(new SolidColorBrush(accent), null, new Rect(winRect.X, winRect.Y, winRect.Width, 9));
            dc.Pop();
        }

        var lineBrush = new SolidColorBrush(lineColor);
        for (var i = 0; i < 3; i++)
            dc.DrawRoundedRectangle(lineBrush, null, new Rect(winRect.X + 10, winRect.Y + 18 + i * 10, winRect.Width * (i == 2 ? 0.4 : 0.7), 4), 2, 2);
        dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(winRect.Right - 38, winRect.Bottom - 16, 28, 9), 3, 3);

        DrawBottom(dc, w, h, systemDark, accent, centered, transparent, dockOn);
        dc.Pop();
    }

    /// <summary>The Restyle dock (when it replaces the taskbar) or the Windows taskbar.</summary>
    private void DrawBottom(DrawingContext dc, double w, double h, bool systemDark, Color accent, bool centered, bool transparent, bool dockOn)
    {
        if (dockOn && Bool("dockHideTaskbar", false))
        {
            const int n = 6;
            var s = h * 0.07;
            var g = s * 0.35;
            var dockW = n * s + (n + 1) * g;
            var dock = new Rect((w - dockW) / 2, h - s - 2 * g - 3, dockW, s + 2 * g);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(150, 30, 30, 30)), null, dock, 4, 4);
            var colors = new[] { accent, Color.FromRgb(0xF2, 0xC1, 0x4E), Color.FromRgb(0x4A, 0x90, 0xE2), Color.FromRgb(0x5A, 0xC8, 0x6A), Color.FromRgb(0xE8, 0x6A, 0x5A), Color.FromRgb(0xB0, 0xB0, 0xB0) };
            for (var i = 0; i < n; i++)
                dc.DrawRoundedRectangle(new SolidColorBrush(colors[i]), null, new Rect(dock.X + g + i * (s + g), dock.Y + g, s, s), 2, 2);
            return;
        }

        // Taskbar
        var barHeight = h * 0.12;
        var barColor = systemDark
            ? Bool("accentOnTaskbar", false) ? Darken(accent, 0.45) : Color.FromRgb(0x1C, 0x1C, 0x1C)
            : Color.FromRgb(0xEE, 0xEE, 0xEE);
        barColor.A = transparent ? (byte)215 : (byte)255;
        var bar = new Rect(0, h - barHeight, w, barHeight);
        dc.DrawRectangle(new SolidColorBrush(barColor), null, bar);

        const int icons = 5;
        var size = barHeight * 0.52;
        var gap = size * 0.6;
        var total = icons * size + (icons - 1) * gap;
        var x = centered ? (w - total) / 2 : 8;
        var y = bar.Y + (barHeight - size) / 2;
        var iconColor = systemDark ? Color.FromRgb(0x9A, 0x9A, 0x9A) : Color.FromRgb(0x7A, 0x7A, 0x7A);
        for (var i = 0; i < icons; i++)
        {
            var brush = new SolidColorBrush(i == 0 ? accent : iconColor);
            dc.DrawRoundedRectangle(brush, null, new Rect(x + i * (size + gap), y, size, size), 2, 2);
        }
    }

    /// <summary>A miniature of the desktop widgets: map and clock on the left, tiles on the right.</summary>
    private static Brush GlassBrush(bool glass) => glass
        ? new SolidColorBrush(Color.FromArgb(70, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(215, 34, 34, 38));

    /// <summary>Miniature of the "glass clock" layout: a big centered clock and two pills above the dock.</summary>
    private static void DrawGlassClock(DrawingContext dc, double w, double h, bool glass)
    {
        var text = new SolidColorBrush(Color.FromArgb(240, 255, 255, 255));
        dc.DrawRoundedRectangle(text, null, new Rect(w * 0.40, h * 0.22, w * 0.20, h * 0.11), 3, 3);
        dc.DrawRoundedRectangle(text, null, new Rect(w * 0.45, h * 0.37, w * 0.10, h * 0.03), 1.5, 1.5);
        dc.DrawRoundedRectangle(text, null, new Rect(w * 0.42, h * 0.42, w * 0.16, h * 0.03), 1.5, 1.5);
        var pill = GlassBrush(glass);
        var edge = glass ? new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 0.6) : null;
        dc.DrawRoundedRectangle(pill, edge, new Rect(w * 0.37, h * 0.62, w * 0.26, h * 0.07), h * 0.035, h * 0.035);
        dc.DrawRoundedRectangle(pill, edge, new Rect(w * 0.37, h * 0.71, w * 0.26, h * 0.06), h * 0.03, h * 0.03);
    }

    /// <summary>Miniature of the "corners" layout: weather, an app column, big date and a two-tone clock.</summary>
    private static void DrawCorners(DrawingContext dc, double w, double h, bool glass)
    {
        var gold = new SolidColorBrush(Color.FromRgb(0xE8, 0xC7, 0x7E));
        var cyan = new SolidColorBrush(Color.FromRgb(0x8F, 0xD3, 0xEA));
        var panel = GlassBrush(glass);
        dc.DrawRoundedRectangle(panel, null, new Rect(w * 0.10, h * 0.05, w * 0.12, h * 0.08), 2, 2);
        dc.DrawRoundedRectangle(panel, null, new Rect(w * 0.10, h * 0.22, w * 0.05, h * 0.50), 3, 3);
        for (var i = 0; i < 12; i++)
            dc.DrawRectangle(gold, null, new Rect(w * (0.70 + i * 0.02), h * (0.14 - 0.01 * (i % 4)), w * 0.006, h * (0.03 + 0.01 * (i % 4))));
        dc.DrawRoundedRectangle(gold, null, new Rect(w * 0.70, h * 0.20, w * 0.24, h * 0.10), 2, 2);
        dc.DrawRoundedRectangle(cyan, null, new Rect(w * 0.82, h * 0.33, w * 0.12, h * 0.12), 2, 2);
        dc.DrawRoundedRectangle(gold, null, new Rect(w * 0.18, h * 0.80, w * 0.09, h * 0.11), 2, 2);
        dc.DrawRoundedRectangle(cyan, null, new Rect(w * 0.30, h * 0.80, w * 0.09, h * 0.11), 2, 2);
    }

    private static void DrawWidgets(DrawingContext dc, double w, double h, bool glass)
    {
        var white = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        var tile = GlassBrush(glass);

        // World map blob and big clock.
        dc.DrawRoundedRectangle(white, null, new Rect(w * 0.12, h * 0.07, w * 0.10, h * 0.13), 4, 4);
        dc.DrawRoundedRectangle(white, null, new Rect(w * 0.23, h * 0.06, w * 0.12, h * 0.09), 4, 4);
        dc.DrawRoundedRectangle(white, null, new Rect(w * 0.26, h * 0.16, w * 0.05, h * 0.09), 3, 3);
        var text = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
        dc.DrawRoundedRectangle(text, null, new Rect(w * 0.07, h * 0.50, w * 0.16, h * 0.08), 2, 2);
        dc.DrawRoundedRectangle(text, null, new Rect(w * 0.07, h * 0.61, w * 0.20, h * 0.035), 1.5, 1.5);
        for (var i = 0; i < 3; i++)
            dc.DrawRoundedRectangle(white, null, new Rect(w * 0.07, h * (0.68 + i * 0.045), w * 0.26, h * 0.022), 1, 1);

        // Tile panel.
        var u = h * 0.105;
        var gap = u * 0.12;
        var x0 = w - 2 * u - gap - w * 0.04;
        var y = h * 0.06;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0x90, 0xE2)), null, new Rect(x0, y, u, u), 3, 3);
        dc.DrawRoundedRectangle(tile, null, new Rect(x0 + u + gap, y, u, u), 3, 3);
        dc.DrawEllipse(null, new Pen(white, 1), new Point(x0 + u * 1.5 + gap, y + u / 2), u * 0.36, u * 0.36);
        y += u + gap * 2;
        var small = (2 * u + gap - 3 * gap) / 4;
        for (var i = 0; i < 4; i++)
            dc.DrawRoundedRectangle(tile, null, new Rect(x0 + i * (small + gap), y, small, small), 2, 2);
        y += small + gap * 2;
        dc.DrawRoundedRectangle(tile, null, new Rect(x0, y, u, u), 3, 3);
        dc.DrawRoundedRectangle(white, null, new Rect(x0 + u * 0.18, y + u * 0.15, u * 0.26, u * 0.7), 3, 3);
        dc.DrawRoundedRectangle(white, null, new Rect(x0 + u * 0.56, y + u * 0.15, u * 0.26, u * 0.7), 3, 3);
        dc.DrawRoundedRectangle(tile, null, new Rect(x0 + u + gap, y, u, u), 3, 3);
        y += u + gap * 2;
        dc.DrawRoundedRectangle(tile, null, new Rect(x0, y, 2 * u + gap, u * 0.75), 3, 3);
    }

    private string? Str(string id, string? property = null) => _values.GetValueOrDefault(id) switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o when property is not null => o[property]?.GetValue<string>(),
        _ => null,
    };

    private bool Bool(string id, bool fallback) =>
        _values.GetValueOrDefault(id) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private Color Accent()
    {
        var hex = Str("accentColor", "color");
        try
        {
            if (!string.IsNullOrEmpty(hex))
                return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
        }

        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    private static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

    private static Rect Offset(Rect r, double dx, double dy) => new(r.X + dx, r.Y + dy, r.Width, r.Height);

    /// <summary>"Fill" scaling: cover the area, crop the overflow.</summary>
    private static Rect Fill(ImageSource image, double w, double h)
    {
        var scale = Math.Max(w / image.Width, h / image.Height);
        var iw = image.Width * scale;
        var ih = image.Height * scale;
        return new Rect((w - iw) / 2, (h - ih) / 2, iw, ih);
    }
}
