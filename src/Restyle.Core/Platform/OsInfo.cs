namespace Restyle.Core.Platform;

/// <summary>Which Windows build we're running on, used to hide features that don't exist there.</summary>
public sealed record OsInfo(int Build)
{
    /// <summary>Windows 10 1903 added a separate "system" light/dark mode.</summary>
    public const int Win10_1903 = 18362;

    public const int Win11 = 22000;

    public bool IsWindows11 => Build >= Win11;

    public string DisplayName => IsWindows11 ? $"Windows 11 (build {Build})" : $"Windows 10 (build {Build})";

    /// <summary>.NET reports the real OS version (via RtlGetVersion), no manifest tricks needed.</summary>
    public static OsInfo Current() => new(Environment.OSVersion.Version.Build);
}
