using System.Text.Json;
using System.Text.Json.Nodes;
using Restyle.Core.Platform;

namespace Restyle.Core.Settings;

/// <summary>
/// Base class for a typed setting. To add a new setting: derive from this (or a helper
/// like <see cref="DwordToggleSetting"/>), implement Read/Apply, and register it in
/// <see cref="SettingsCatalog"/>.
/// </summary>
public abstract class Setting<T>(SettingContext context) : ISetting
{
    protected SettingContext Context { get; } = context;

    protected IRegistry Registry => Context.Registry;

    protected ISystemApi System => Context.System;

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    /// <summary>Minimum Windows build, or 0 for "any supported Windows".</summary>
    protected virtual int MinimumBuild => 0;

    public bool IsSupported => Context.Os.Build >= MinimumBuild;

    public string? UnsupportedReason => IsSupported
        ? null
        : MinimumBuild >= OsInfo.Win11
            ? "Only available on Windows 11."
            : $"Needs a newer version of Windows (build {MinimumBuild} or later).";

    public virtual bool RequiresExplorerRestart => false;

    public virtual IReadOnlyList<string> BroadcastAreas => [];

    public abstract T Read();

    public abstract void Apply(T value);

    public virtual string Describe(T value) => value?.ToString() ?? "(none)";

    public JsonNode? ReadJson() => JsonSerializer.SerializeToNode(Read(), RestyleJson.Options);

    public void ApplyJson(JsonNode? value) => Apply(Parse(value));

    public string DescribeJson(JsonNode? value)
    {
        try
        {
            return Describe(Parse(value));
        }
        catch (Exception)
        {
            return value?.ToJsonString() ?? "(none)";
        }
    }

    /// <summary>Override when a short preset value (e.g. a scheme name) should match a fuller stored value.</summary>
    protected virtual bool IsSame(T current, T target) => EqualityComparer<T>.Default.Equals(current, target);

    public bool IsSameJson(JsonNode? current, JsonNode? target)
    {
        if (current is null || target is null)
            return current is null && target is null;
        try
        {
            return IsSame(Parse(current), Parse(target));
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected static T Parse(JsonNode? value) =>
        value.Deserialize<T>(RestyleJson.Options)
        ?? throw new ArgumentException("A value is required.");
}

/// <summary>An on/off setting stored as a REG_DWORD under HKCU.</summary>
public abstract class DwordToggleSetting(SettingContext context) : Setting<bool>(context)
{
    protected abstract string KeyPath { get; }

    protected abstract string ValueName { get; }

    /// <summary>What Windows assumes when the value doesn't exist.</summary>
    protected abstract bool DefaultValue { get; }

    /// <summary>True when the registry stores the opposite (e.g. HideIcons=1 means "icons off").</summary>
    protected virtual bool Inverted => false;

    public override bool Read()
    {
        var raw = Registry.GetDword(KeyPath, ValueName);
        return raw is null ? DefaultValue : (raw.Value != 0) ^ Inverted;
    }

    public override void Apply(bool value)
    {
        try
        {
            Registry.SetDword(KeyPath, ValueName, value ^ Inverted ? 1 : 0);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw Blocked(ex);
        }
    }

    /// <summary>Override to give a friendlier explanation when Windows blocks the write.</summary>
    protected virtual Exception Blocked(UnauthorizedAccessException ex) => ex;

    public override string Describe(bool value) => value ? "On" : "Off";
}

/// <summary>Common registry locations.</summary>
internal static class Keys
{
    public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    public const string ExplorerAccent = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    public const string Dwm = @"Software\Microsoft\Windows\DWM";
    public const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";
    public const string Desktop = @"Control Panel\Desktop";
    public const string Cursors = @"Control Panel\Cursors";
    public const string UserCursorSchemes = @"Control Panel\Cursors\Schemes";
    public const string SystemCursorSchemes = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\Cursors\Schemes";

    public const string ColorArea = "ImmersiveColorSet";
    public const string TrayArea = "TraySettings";
}
