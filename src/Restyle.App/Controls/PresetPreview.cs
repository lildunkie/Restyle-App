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

        dc.Pop();
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
