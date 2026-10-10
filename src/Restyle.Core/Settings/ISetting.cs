using System.Text.Json.Nodes;
using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

/// <summary>
/// One thing the app can change. The engine, presets and backups only see this
/// untyped (JSON) view; concrete settings derive from <see cref="Setting{T}"/>.
/// </summary>
public interface ISetting
{
    /// <summary>Stable key used in preset/backup JSON, e.g. "appsTheme".</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>Plain-language explanation, shown as a tooltip.</summary>
    string Description { get; }

    bool IsSupported { get; }

    /// <summary>Why the setting is unavailable on this PC (null when supported).</summary>
    string? UnsupportedReason { get; }

    /// <summary>True if Explorer must restart before the change is visible.</summary>
    bool RequiresExplorerRestart { get; }

    /// <summary>WM_SETTINGCHANGE areas to broadcast after applying (the engine batches these).</summary>
    IReadOnlyList<string> BroadcastAreas { get; }

    JsonNode? ReadJson();

    void ApplyJson(JsonNode? value);

    /// <summary>Human-readable form of a value, e.g. "Dark" or "Centered".</summary>
    string DescribeJson(JsonNode? value);

    /// <summary>True if applying <paramref name="target"/> would change nothing (so it can be skipped).</summary>
    bool IsSameJson(JsonNode? current, JsonNode? target);
}

/// <summary>Everything a setting needs to talk to Windows. Swapped for fakes in tests.</summary>
public sealed class SettingContext(IRegistry registry, ISystemApi system, OsInfo os)
{
    public IRegistry Registry { get; } = registry;

    public ISystemApi System { get; } = system;

    public OsInfo Os { get; } = os;

    /// <summary>Turns preset paths like "builtin:gnome-blobs" into real files. Defaults to expanding %VARS%.</summary>
    public Func<string, string> ResolvePath { get; set; } = Environment.ExpandEnvironmentVariables;

    /// <summary>Where imported cursor packs are copied to.</summary>
    public string DataDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle");

    public static SettingContext ForCurrentMachine() =>
        new(new WindowsRegistry(), new WindowsSystemApi(), OsInfo.Current());
}

/// <summary>Thrown when Windows blocks a change; carries a friendly message and an optional Settings deep link.</summary>
public sealed class SettingBlockedException(string message, string? settingsUri = null, Exception? inner = null)
    : Exception(message, inner)
{
    public string? SettingsUri { get; } = settingsUri;
}
