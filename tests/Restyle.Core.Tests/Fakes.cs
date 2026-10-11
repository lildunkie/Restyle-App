using Microsoft.Win32;
using Restyle.Core.Engine;
using Restyle.Core.Platform;
using Restyle.Core.Settings;

namespace Restyle.Core.Tests;

/// <summary>In-memory registry. Keys and names are case-insensitive like the real one.</summary>
internal sealed class FakeRegistry : IRegistry
{
    private readonly Dictionary<(RegHive, string, string), (object Value, RegistryValueKind Kind, string Name)> _values = [];

    /// <summary>Values (as "key\name") that throw UnauthorizedAccessException on write.</summary>
    public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static (RegHive, string, string) K(RegHive hive, string key, string name) =>
        (hive, key.ToLowerInvariant(), name.ToLowerInvariant());

    public object? Get(RegHive hive, string keyPath, string name) =>
        _values.TryGetValue(K(hive, keyPath, name), out var v) ? v.Value : null;

    public RegistryValueKind? KindOf(string keyPath, string name) =>
        _values.TryGetValue(K(RegHive.CurrentUser, keyPath, name), out var v) ? v.Kind : null;

    public void Set(string keyPath, string name, object value, RegistryValueKind kind)
    {
        if (Blocked.Contains($@"{keyPath}\{name}"))
            throw new UnauthorizedAccessException("Blocked by test");
        _values[K(RegHive.CurrentUser, keyPath, name)] = (value, kind, name);
    }

    /// <summary>Test helper for seeding HKLM (the app itself can never write there).</summary>
    public void SetMachine(string keyPath, string name, object value) =>
        _values[K(RegHive.LocalMachine, keyPath, name)] = (value, RegistryValueKind.String, name);

    public void Delete(string keyPath, string name) => _values.Remove(K(RegHive.CurrentUser, keyPath, name));

    public IReadOnlyList<string> GetValueNames(RegHive hive, string keyPath) =>
        _values.Where(kv => kv.Key.Item1 == hive && kv.Key.Item2 == keyPath.ToLowerInvariant())
            .Select(kv => kv.Value.Name).ToList();
}

internal sealed class FakeSystemApi : ISystemApi
{
    public string Wallpaper { get; set; } = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
    public bool AutoHide { get; set; }
    public int CursorReloads { get; private set; }
    public List<string> Broadcasts { get; } = [];
    public int ExplorerRestarts { get; private set; }

    public string GetWallpaper() => Wallpaper;
    public void SetWallpaper(string path) => Wallpaper = path;
    public void ReloadCursors() => CursorReloads++;
    public bool GetTaskbarAutoHide() => AutoHide;
    public void SetTaskbarAutoHide(bool autoHide) => AutoHide = autoHide;
    public void BroadcastSettingChange(string area) => Broadcasts.Add(area);
    public void RestartExplorer() => ExplorerRestarts++;
}

/// <summary>A fully wired engine on fakes. Build defaults to Windows 11 24H2.</summary>
internal sealed class TestRig
{
    public TestRig(int build = 26100)
    {
        Context = new SettingContext(Registry, System, new OsInfo(build));
        Catalog = new SettingsCatalog(Context);
        Engine = new SettingsEngine(Catalog, History);
    }

    public FakeRegistry Registry { get; } = new();
    public FakeSystemApi System { get; } = new();
    public ChangeHistory History { get; } = new();
    public SettingContext Context { get; }
    public SettingsCatalog Catalog { get; }
    public SettingsEngine Engine { get; }
}

/// <summary>A throwaway folder deleted after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("restyle-test-").FullName;

    public string File(string name, string content = "x")
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
