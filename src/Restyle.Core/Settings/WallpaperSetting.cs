using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

public enum WallpaperFit
{
    Fill,
    Fit,
    Stretch,
    Tile,
    Center,
    Span,
}

/// <summary>
/// A wallpaper file and how it's fitted. In preset JSON you can write just a path
/// (e.g. "builtin:gnome-blobs" or "%SystemRoot%\\Web\\Wallpaper\\Windows\\img0.jpg").
/// </summary>
[JsonConverter(typeof(WallpaperJsonConverter))]
public sealed record Wallpaper(string Path, WallpaperFit Fit = WallpaperFit.Fill);

public sealed class WallpaperSetting(SettingContext context) : Setting<Wallpaper>(context)
{
    // Control Panel\Desktop: WallpaperStyle / TileWallpaper pairs, as documented for SPI_SETDESKWALLPAPER.
    private static readonly Dictionary<WallpaperFit, (string Style, string Tile)> Styles = new()
    {
        [WallpaperFit.Fill] = ("10", "0"),
        [WallpaperFit.Fit] = ("6", "0"),
        [WallpaperFit.Stretch] = ("2", "0"),
        [WallpaperFit.Tile] = ("0", "1"),
        [WallpaperFit.Center] = ("0", "0"),
        [WallpaperFit.Span] = ("22", "0"),
    };

    public override string Id => "wallpaper";
    public override string Name => "Wallpaper";
    public override string Description => "The picture on your desktop background.";

    public override Wallpaper Read()
    {
        var style = Registry.GetString(Keys.Desktop, "WallpaperStyle") ?? "10";
        var tile = Registry.GetString(Keys.Desktop, "TileWallpaper") ?? "0";
        // Unknown combinations fall back to Fill (the default KeyValuePair's key).
        var fit = tile == "1"
            ? WallpaperFit.Tile
            : Styles.FirstOrDefault(kv => kv.Value == (style, "0")).Key;
        return new Wallpaper(System.GetWallpaper(), fit);
    }

    public override void Apply(Wallpaper value)
    {
        var path = string.IsNullOrWhiteSpace(value.Path) ? "" : Context.ResolvePath(value.Path);
        if (path.Length > 0 && !File.Exists(path))
            throw new FileNotFoundException($"The wallpaper file no longer exists: {path}", path);

        var (style, tile) = Styles[value.Fit];
        Registry.Set(Keys.Desktop, "WallpaperStyle", style, RegistryValueKind.String);
        Registry.Set(Keys.Desktop, "TileWallpaper", tile, RegistryValueKind.String);
        System.SetWallpaper(path);
    }

    protected override bool IsSame(Wallpaper current, Wallpaper target)
    {
        string Full(string p) => string.IsNullOrWhiteSpace(p) ? "" : Context.ResolvePath(p);
        return current.Fit == target.Fit
            && string.Equals(Full(current.Path), Full(target.Path), StringComparison.OrdinalIgnoreCase);
    }

    public override string Describe(Wallpaper value)
    {
        if (string.IsNullOrWhiteSpace(value.Path))
            return "None (solid color)";
        var name = value.Path.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
            ? value.Path["builtin:".Length..]
            : global::System.IO.Path.GetFileNameWithoutExtension(value.Path);
        return value.Fit == WallpaperFit.Fill ? name : $"{name} ({value.Fit})";
    }
}

internal sealed class WallpaperJsonConverter : JsonConverter<Wallpaper>
{
    public override Wallpaper Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new Wallpaper(reader.GetString() ?? "");

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Wallpaper must be a path string or { \"path\": ..., \"fit\": ... }.");

        var path = "";
        var fit = WallpaperFit.Fill;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.GetString();
            reader.Read();
            switch (prop?.ToLowerInvariant())
            {
                case "path": path = reader.GetString() ?? ""; break;
                case "fit": fit = Enum.Parse<WallpaperFit>(reader.GetString() ?? "fill", ignoreCase: true); break;
                default: reader.Skip(); break;
            }
        }

        return new Wallpaper(path, fit);
    }

    public override void Write(Utf8JsonWriter writer, Wallpaper value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("path", value.Path);
        writer.WriteString("fit", JsonNamingPolicy.CamelCase.ConvertName(value.Fit.ToString()));
        writer.WriteEndObject();
    }
}
