using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Restyle.Core.Presets;

/// <summary>
/// A named set of setting values, stored as a small JSON file. Only the settings listed
/// are changed when applied; anything left out stays as it is.
/// </summary>
public sealed class Preset
{
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Sort order on the Presets page (lower first).</summary>
    public int Order { get; set; } = 100;

    public Dictionary<string, JsonNode?> Settings { get; set; } = [];

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public static Preset Parse(string json)
    {
        var preset = JsonSerializer.Deserialize<Preset>(json, RestyleJson.Options)
            ?? throw new InvalidDataException("The preset file is empty.");
        if (string.IsNullOrWhiteSpace(preset.Name))
            throw new InvalidDataException("The preset needs a \"name\".");
        if (preset.Settings.Count == 0)
            throw new InvalidDataException("The preset doesn't change any settings.");
        return preset;
    }

    public string ToJson() => JsonSerializer.Serialize(this, RestyleJson.Options);

    /// <summary>Reads a value for previews, e.g. GetString("accentColor").</summary>
    public string? GetString(string settingId) => Settings.GetValueOrDefault(settingId) switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => (o["color"] ?? o["path"] ?? o["name"])?.GetValue<string>(),
        _ => null,
    };
}
