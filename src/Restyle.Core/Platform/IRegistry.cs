using Microsoft.Win32;

namespace Restyle.Core.Platform;

public enum RegHive
{
    CurrentUser,
    LocalMachine,
}

/// <summary>
/// Minimal registry abstraction. Writes are only allowed to HKEY_CURRENT_USER,
/// which guarantees the app never needs admin rights for a setting change.
/// </summary>
public interface IRegistry
{
    /// <summary>Reads a value without expanding environment variables. Returns null if the key or value is missing.</summary>
    object? Get(RegHive hive, string keyPath, string name);

    /// <summary>Writes a value under HKEY_CURRENT_USER, creating the key if needed.</summary>
    void Set(string keyPath, string name, object value, RegistryValueKind kind);

    /// <summary>Deletes a value under HKEY_CURRENT_USER if it exists.</summary>
    void Delete(string keyPath, string name);

    /// <summary>Lists value names under a key (empty if the key is missing).</summary>
    IReadOnlyList<string> GetValueNames(RegHive hive, string keyPath);
}

public static class RegistryExtensions
{
    public static int? GetDword(this IRegistry reg, string keyPath, string name, RegHive hive = RegHive.CurrentUser)
    {
        return reg.Get(hive, keyPath, name) switch
        {
            int i => i,
            long l => unchecked((int)l),
            string s when int.TryParse(s, out var p) => p,
            _ => null,
        };
    }

    public static string? GetString(this IRegistry reg, string keyPath, string name, RegHive hive = RegHive.CurrentUser)
        => reg.Get(hive, keyPath, name) switch
        {
            string s => s,
            null => null,
            var other => other.ToString(),
        };

    public static void SetDword(this IRegistry reg, string keyPath, string name, int value)
        => reg.Set(keyPath, name, value, RegistryValueKind.DWord);
}
