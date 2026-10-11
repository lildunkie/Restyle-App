#if STORE
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
#else
using Restyle.Core.Dock;
using Restyle.Core.Platform;
#endif

namespace Restyle.App.Docking;

/// <summary>
/// "Start the dock when I sign in". The download version uses the per-user Run key; the Microsoft
/// Store version must use a packaged StartupTask (declared in the Store project's AppxManifest).
/// </summary>
internal static class DockAutoStart
{
#if STORE
    private const string TaskId = "RestyleDock";

    public static async Task<bool> IsEnabledAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    /// <summary>Returns a message to show the user if Windows didn't allow the change, else null.</summary>
    public static async Task<string?> SetEnabledAsync(bool enabled)
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (!enabled)
        {
            task.Disable();
            return null;
        }

        return await task.RequestEnableAsync() switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
            StartupTaskState.DisabledByUser =>
                "Windows has Restyle turned off in Settings > Apps > Startup. Turn it on there and it will start with Windows.",
            _ => "Windows didn't allow Restyle to start automatically (it may be blocked by your organization).",
        };
    }

    /// <summary>True when Windows started us at sign-in through the startup task (which can't pass arguments).</summary>
    public static bool LaunchedAtSignIn()
    {
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception)
        {
            return false;
        }
    }
#else
    private static readonly WindowsRegistry Registry = new();

    public static Task<bool> IsEnabledAsync() => Task.FromResult(DockStartup.IsEnabled(Registry));

    public static Task<string?> SetEnabledAsync(bool enabled)
    {
        if (enabled)
            DockStartup.Enable(Registry, Environment.ProcessPath!);
        else
            DockStartup.Disable(Registry);
        return Task.FromResult<string?>(null);
    }

    public static bool LaunchedAtSignIn() => false;
#endif
}
