using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

/// <summary>
/// An accent color. In preset JSON you can write just "#3584E4" or "auto";
/// backups store the full object (including Windows' exact palette) so restores are lossless.
/// </summary>
[JsonConverter(typeof(AccentColorJsonConverter))]
public sealed record AccentColor(string Color, bool Automatic = false, string? Palette = null)
{
    public static AccentColor Auto => new("", Automatic: true);
}

public sealed class AccentColorSetting(SettingContext context) : Setting<AccentColor>(context)
{
    public const string DefaultColor = "#0078D4";

    public override string Id => "accentColor";
    public override string Name => "Accent color";
    public override string Description =>
        "The highlight color used for buttons, links, the Start menu and (optionally) title bars and the taskbar.";
    public override IReadOnlyList<string> BroadcastAreas => [Keys.ColorArea];

    public override AccentColor Read()
    {
        var menu = Registry.GetDword(Keys.ExplorerAccent, "AccentColorMenu");
        var color = menu is int abgr ? ColorMath.FromAbgr(abgr) : DefaultColor;
        var palette = Registry.Get(RegHive.CurrentUser,Keys.ExplorerAccent, "AccentPalette") as byte[];
        var automatic = Registry.GetDword(Keys.Desktop, "AutoColorization") is 1;
        return new AccentColor(color, automatic, palette is { Length: 32 } ? Convert.ToHexString(palette) : null);
    }

    public override void Apply(AccentColor value)
    {
        if (!string.IsNullOrEmpty(value.Color))
        {
            var (r, g, b) = ColorMath.Parse(value.Color);
            var palette = value.Palette is { Length: 64 }
                ? Convert.FromHexString(value.Palette)
                : AccentPaletteBuilder.Build(r, g, b);

            var abgr = ColorMath.ToAbgr(r, g, b);
            var dark1 = ColorMath.ToAbgr(palette[16], palette[17], palette[18]);
            var argb = unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | b));

            Registry.Set(Keys.ExplorerAccent, "AccentPalette", palette, RegistryValueKind.Binary);
            Registry.SetDword(Keys.ExplorerAccent, "AccentColorMenu", abgr);
            Registry.SetDword(Keys.ExplorerAccent, "StartColorMenu", dark1);
            Registry.SetDword(Keys.Dwm, "AccentColor", abgr);
            Registry.SetDword(Keys.Dwm, "ColorizationColor", argb);
            Registry.SetDword(Keys.Dwm, "ColorizationAfterglow", argb);
        }

        Registry.SetDword(Keys.Desktop, "AutoColorization", value.Automatic ? 1 : 0);
    }

    public override string Describe(AccentColor value) =>
        value.Automatic ? "Automatic (picked from wallpaper)" : value.Color.ToUpperInvariant();

    protected override bool IsSame(AccentColor current, AccentColor target) =>
        current.Automatic == target.Automatic
        && (target.Automatic && target.Color.Length == 0
            || string.Equals(current.Color, target.Color, StringComparison.OrdinalIgnoreCase))
        && (target.Palette is null || string.Equals(current.Palette, target.Palette, StringComparison.OrdinalIgnoreCase));
}

public sealed class AccentOnTitleBarsSetting(SettingContext context) : DwordToggleSetting(context)
{
    public override string Id => "accentOnTitleBars";
    public override string Name => "Accent color on title bars";
    public override string Description =>
        "Paints window title bars and borders with your accent color. Already-open windows may update when you next click them.";
    public override IReadOnlyList<string> BroadcastAreas => [Keys.ColorArea];
    protected override string KeyPath => Keys.Dwm;
    protected override string ValueName => "ColorPrevalence";
    protected override bool DefaultValue => false;
}

public sealed class AccentOnTaskbarSetting(SettingContext context) : DwordToggleSetting(context)
{
    public override string Id => "accentOnTaskbar";
    public override string Name => "Accent color on Start and taskbar";
    public override string Description =>
        "Tints the taskbar and Start menu with your accent color. Windows only allows this when 'Windows mode' is Dark.";
    public override IReadOnlyList<string> BroadcastAreas => [Keys.ColorArea];
    protected override string KeyPath => Keys.Personalize;
    protected override string ValueName => "ColorPrevalence";
    protected override bool DefaultValue => false;
}

/// <summary>Builds the 8-shade palette Windows expects (lightest → darkest, then a spare entry).</summary>
public static class AccentPaletteBuilder
{
    private static readonly double[] LightMix = [0.60, 0.35, 0.15];
    private static readonly double[] DarkMix = [0.15, 0.40, 0.65];

    public static byte[] Build(byte r, byte g, byte b)
    {
        var bytes = new byte[32];
        var i = 0;

        foreach (var t in LightMix)
            Put(Mix(r, 255, t), Mix(g, 255, t), Mix(b, 255, t));
        Put(r, g, b);
        foreach (var t in DarkMix)
            Put(Mix(r, 0, t), Mix(g, 0, t), Mix(b, 0, t));
        Put(0xF7, 0x63, 0x0C); // spare entry; matches the stock Windows palette

        return bytes;

        void Put(byte pr, byte pg, byte pb)
        {
            bytes[i++] = pr;
            bytes[i++] = pg;
            bytes[i++] = pb;
            bytes[i++] = 0;
        }
    }

    private static byte Mix(byte from, byte to, double amount) =>
        (byte)Math.Round(from + (to - from) * amount);
}

public static class ColorMath
{
    public static (byte R, byte G, byte B) Parse(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            throw new FormatException($"'{hex}' isn't a color. Use the form #RRGGBB, e.g. #3584E4.");
        return ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    /// <summary>Windows stores accent DWORDs as 0xAABBGGRR.</summary>
    public static int ToAbgr(byte r, byte g, byte b) =>
        unchecked((int)(0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r));

    public static string FromAbgr(int abgr) =>
        ToHex((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
}

internal sealed class AccentColorJsonConverter : JsonConverter<AccentColor>
{
    public override AccentColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            if (s.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return AccentColor.Auto;
            ColorMath.Parse(s); // validate early
            return new AccentColor(s);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Accent color must be \"#RRGGBB\", \"auto\" or an object.");

        string color = "";
        var automatic = false;
        string? palette = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.GetString();
            reader.Read();
            switch (prop?.ToLowerInvariant())
            {
                case "color": color = reader.GetString() ?? ""; break;
                case "automatic": automatic = reader.GetBoolean(); break;
                case "palette": palette = reader.TokenType == JsonTokenType.Null ? null : reader.GetString(); break;
                default: reader.Skip(); break;
            }
        }

        return new AccentColor(color, automatic, palette);
    }

    public override void Write(Utf8JsonWriter writer, AccentColor value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("color", value.Color);
        writer.WriteBoolean("automatic", value.Automatic);
        if (value.Palette is null)
            writer.WriteNull("palette");
        else
            writer.WriteString("palette", value.Palette);
        writer.WriteEndObject();
    }
}
