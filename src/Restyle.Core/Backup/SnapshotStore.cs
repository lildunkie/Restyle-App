using System.Text.Json;
using System.Text.Json.Nodes;
using Restyle.Core.Engine;

namespace Restyle.Core.Backup;

public sealed class Snapshot
{
    public string Name { get; set; } = "";

    public DateTimeOffset Created { get; set; }

    public int OsBuild { get; set; }

    public Dictionary<string, JsonNode?> Settings { get; set; } = [];

    /// <summary>File this snapshot was loaded from (not serialized).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? FilePath { get; set; }
}

/// <summary>
/// Stores the "original settings" snapshot taken on first launch, plus any backups the user makes.
/// Layout: {dir}\original.json and {dir}\backups\*.json.
/// </summary>
public sealed class SnapshotStore(string directory)
{
    public string Directory { get; } = directory;

    public string OriginalPath => Path.Combine(Directory, "original.json");

    public string BackupsDirectory => Path.Combine(Directory, "backups");

    public Snapshot? LoadOriginal() => File.Exists(OriginalPath) ? Load(OriginalPath) : null;

    /// <summary>Saves the original snapshot if one doesn't exist yet. Returns true if it was created now.</summary>
    public bool EnsureOriginal(SettingsEngine engine)
    {
        if (File.Exists(OriginalPath))
            return false;

        Write(OriginalPath, Create("My original settings", engine));
        return true;
    }

    /// <summary>
    /// Settings added in later versions aren't in an older "original" snapshot. Add them the first time
    /// this version runs, with the value <paramref name="valueFor"/> gives (their state before Restyle).
    /// </summary>
    public void AddMissingToOriginal(IEnumerable<string> ids, Func<string, JsonNode?> valueFor)
    {
        if (LoadOriginal() is not { } original)
            return;
        var added = false;
        foreach (var id in ids.Where(id => !original.Settings.ContainsKey(id)))
        {
            original.Settings[id] = valueFor(id);
            added = true;
        }

        if (added)
            Write(OriginalPath, original);
    }

    public IReadOnlyList<Snapshot> ListBackups()
    {
        if (!System.IO.Directory.Exists(BackupsDirectory))
            return [];

        var list = new List<Snapshot>();
        foreach (var file in System.IO.Directory.EnumerateFiles(BackupsDirectory, "*.json"))
        {
            try
            {
                list.Add(Load(file));
            }
            catch (Exception)
            {
                // Ignore unreadable files instead of hiding every backup.
            }
        }

        return list.OrderByDescending(s => s.Created).ToList();
    }

    public Snapshot SaveBackup(string name, SettingsEngine engine)
    {
        var snapshot = Create(name, engine);
        var path = Path.Combine(BackupsDirectory, $"{snapshot.Created:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24] + ".json");
        Write(path, snapshot);
        snapshot.FilePath = path;
        return snapshot;
    }

    public void DeleteBackup(Snapshot snapshot)
    {
        if (snapshot.FilePath is not null && snapshot.FilePath.StartsWith(BackupsDirectory, StringComparison.OrdinalIgnoreCase))
            File.Delete(snapshot.FilePath);
    }

    public static Snapshot Load(string path)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path), RestyleJson.Options)
            ?? throw new InvalidDataException($"Empty snapshot: {path}");
        snapshot.FilePath = path;
        return snapshot;
    }

    private static Snapshot Create(string name, SettingsEngine engine) => new()
    {
        Name = name,
        Created = DateTimeOffset.Now,
        OsBuild = engine.Catalog.Context.Os.Build,
        Settings = engine.Capture(),
    };

    private static void Write(string path, Snapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, RestyleJson.Options));
    }
}
