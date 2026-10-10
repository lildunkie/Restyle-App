using Restyle.Core.Dock;
using Restyle.Core.Platform;

namespace Restyle.App.Docking;

/// <summary>"Start the dock when I sign in", via the per-user Run key.</summary>
internal static class DockAutoStart
{
    private static readonly WindowsRegistry Registry = new();

    public static Task<bool> IsEnabledAsync() => Task.FromResult(DockStartup.IsEnabled(Registry));

    /// <summary>Returns a message to show the user if the change wasn't allowed, else null.</summary>
    public static Task<string?> SetEnabledAsync(bool enabled)
    {
        if (enabled)
            DockStartup.Enable(Registry, Environment.ProcessPath!);
        else
            DockStartup.Disable(Registry);
        return Task.FromResult<string?>(null);
    }

    public static bool LaunchedAtSignIn() => false;
}
