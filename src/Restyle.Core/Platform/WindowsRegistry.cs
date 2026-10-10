using Microsoft.Win32;

namespace Restyle.Core.Platform;

/// <summary>Real registry access. Reads from HKCU or HKLM; writes only to HKCU.</summary>
public sealed class WindowsRegistry : IRegistry
{
    private static RegistryKey Root(RegHive hive) =>
        hive == RegHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

    public object? Get(RegHive hive, string keyPath, string name)
    {
        using var key = Root(hive).OpenSubKey(keyPath, writable: false);
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Set(string keyPath, string name, object value, RegistryValueKind kind)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{keyPath}");
        key.SetValue(name, value, kind);
    }

    public void Delete(string keyPath, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string keyPath)
    {
        using var key = Root(hive).OpenSubKey(keyPath, writable: false);
        return key?.GetValueNames() ?? [];
    }
}
