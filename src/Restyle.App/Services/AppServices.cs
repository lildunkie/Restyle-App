using System.IO;
using System.Reflection;
using Restyle.Core.Backup;
using Restyle.Core.Engine;
using Restyle.Core.Platform;
using Restyle.Core.Presets;
using Restyle.Core.Settings;

namespace Restyle.App.Services;

/// <summary>Wires up the core library for the app. One instance for the app's lifetime.</summary>
public sealed class AppServices
{
    private AppServices()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Restyle");
        Wallpapers = new WallpaperLibrary(Path.Combine(DataDirectory, "Wallpapers"));

        var context = SettingContext.ForCurrentMachine();
        context.ResolvePath = Wallpapers.Resolve;
        context.DataDirectory = DataDirectory;

        Catalog = new SettingsCatalog(context);
        Engine = new SettingsEngine(Catalog, new ChangeHistory(Path.Combine(DataDirectory, "history.json")));
        Snapshots = new SnapshotStore(DataDirectory);
        Presets = new PresetStore(
            Path.Combine(AppContext.BaseDirectory, "Presets"),
            Path.Combine(DataDirectory, "Presets"),
            ReadEmbeddedPresets);
    }

    public static AppServices Current { get; } = new();

    public string DataDirectory { get; }

    public OsInfo Os => Catalog.Context.Os;

    public SettingsCatalog Catalog { get; }

    public SettingsEngine Engine { get; }

    public SnapshotStore Snapshots { get; }

    public PresetStore Presets { get; }

    public WallpaperLibrary Wallpapers { get; }

    private static IEnumerable<string> ReadEmbeddedPresets()
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Presets.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return reader.ReadToEnd();
        }
    }
}
