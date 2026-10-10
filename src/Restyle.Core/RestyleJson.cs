using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restyle.Core;

/// <summary>Shared JSON options so presets, snapshots and history all read/write the same way.</summary>
public static class RestyleJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
