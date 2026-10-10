using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

/// <summary>
/// A mouse pointer scheme. Presets can just name one ("Windows Black"); backups store the
/// exact file for every pointer role so a restore puts back precisely what was there.
/// </summary>
[JsonConverter(typeof(CursorSchemeJsonConverter))]
public sealed record CursorScheme(string Name, Dictionary<string, string>? Cursors = null, int? Source = null);

public sealed class CursorSchemeSetting(SettingContext context) : Setting<CursorScheme>(context)
{
    public const string WindowsDefault = "Windows Default";

    /// <summary>Pointer roles in the order Windows stores them in a scheme's comma-separated list.</summary>
    public static readonly string[] Roles =
    [
        "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No",
        "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Pin", "Person",
    ];

    // "Scheme Source" values used by the Mouse control panel.
    private const int SourceDefault = 0;
    private const int SourceUser = 1;
    private const int SourceSystem = 2;

    public override string Id => "cursorScheme";
    public override string Name => "Mouse pointers";
    public override string Description =>
        "The look of the mouse pointer. Pick a Windows scheme or import a cursor pack you downloaded.";

    public override CursorScheme Read()
    {
        var cursors = Roles.ToDictionary(r => r, r => Registry.GetString(Keys.Cursors, r) ?? "");
        return new CursorScheme(
            Registry.GetString(Keys.Cursors, "") ?? "",
            cursors,
            Registry.GetDword(Keys.Cursors, "Scheme Source"));
    }

    public override void Apply(CursorScheme value)
    {
        var cursors = value.Cursors;
        var source = value.Source;

        if (cursors is null)
        {
            if (FindScheme(Registry, value.Name) is var (found, foundSource))
            {
                cursors = found;
                source ??= foundSource;
            }
            else if (IsWindowsDefault(value.Name))
            {
                // Empty paths make Windows use its built-in pointers.
                cursors = Roles.ToDictionary(r => r, _ => "");
                source ??= SourceDefault;
            }
            else
            {
                throw new KeyNotFoundException($"The pointer scheme \"{value.Name}\" isn't installed on this PC.");
            }
        }

        foreach (var role in Roles)
            Registry.Set(Keys.Cursors, role, cursors.GetValueOrDefault(role, ""), RegistryValueKind.ExpandString);
        Registry.Set(Keys.Cursors, "", value.Name, RegistryValueKind.String);
        Registry.SetDword(Keys.Cursors, "Scheme Source", source ?? SourceUser);

        System.ReloadCursors();
    }

    public override string Describe(CursorScheme value) =>
        string.IsNullOrEmpty(value.Name) ? WindowsDefault : value.Name;

    protected override bool IsSame(CursorScheme current, CursorScheme target)
    {
        var sameName = string.Equals(Describe(current), Describe(target), StringComparison.OrdinalIgnoreCase);
        if (target.Cursors is null)
            return sameName;

        return sameName && Roles.All(r => string.Equals(
            current.Cursors?.GetValueOrDefault(r, "") ?? "",
            target.Cursors.GetValueOrDefault(r, ""),
            StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWindowsDefault(string name) =>
        name.Length == 0 || name.Equals(WindowsDefault, StringComparison.OrdinalIgnoreCase);

    /// <summary>All scheme names installed for this user and system-wide.</summary>
    public static IReadOnlyList<string> ListSchemes(IRegistry registry) =>
        registry.GetValueNames(RegHive.CurrentUser, Keys.UserCursorSchemes)
            .Concat(registry.GetValueNames(RegHive.LocalMachine, Keys.SystemCursorSchemes))
            .Append(WindowsDefault)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static (Dictionary<string, string> Cursors, int Source)? FindScheme(IRegistry registry, string name)
    {
        if (name.Length == 0)
            return null;

        var list = registry.GetString(Keys.UserCursorSchemes, name);
        var source = SourceUser;
        if (list is null)
        {
            list = registry.GetString(Keys.SystemCursorSchemes, name, RegHive.LocalMachine);
            source = SourceSystem;
        }

        if (list is null)
            return null;

        var parts = list.Split(',');
        var cursors = new Dictionary<string, string>();
        for (var i = 0; i < Roles.Length; i++)
            cursors[Roles[i]] = i < parts.Length ? parts[i].Trim() : "";
        return (cursors, source);
    }
}

internal sealed class CursorSchemeJsonConverter : JsonConverter<CursorScheme>
{
    public override CursorScheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new CursorScheme(reader.GetString() ?? "");

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Cursor scheme must be a scheme name or an object.");

        var name = "";
        Dictionary<string, string>? cursors = null;
        int? source = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var prop = reader.GetString();
            reader.Read();
            switch (prop?.ToLowerInvariant())
            {
                case "name": name = reader.GetString() ?? ""; break;
                case "cursors": cursors = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options); break;
                case "source": source = reader.TokenType == JsonTokenType.Null ? null : reader.GetInt32(); break;
                default: reader.Skip(); break;
            }
        }

        return new CursorScheme(name, cursors, source);
    }

    public override void Write(Utf8JsonWriter writer, CursorScheme value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        if (value.Cursors is not null)
        {
            writer.WritePropertyName("cursors");
            JsonSerializer.Serialize(writer, value.Cursors, options);
        }
        if (value.Source is int s)
            writer.WriteNumber("source", s);
        writer.WriteEndObject();
    }
}
