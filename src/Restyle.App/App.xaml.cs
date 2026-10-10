using System.Windows;
using Restyle.App.Docking;
using Restyle.App.Services;

namespace Restyle.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Restyle draws simple, mostly static UI. Software rendering looks identical and avoids
        // loading the GPU driver's Direct3D stack, which on its own costs 25-70 MB per process.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        if (e.Args.Contains("--dock", StringComparer.OrdinalIgnoreCase) || DockAutoStart.LaunchedAtSignIn())
        {
            DockHost.Run(this);
            return;
        }

        // Windows 11 Fluent look that follows light/dark mode automatically.
        ThemeMode = ThemeMode.System;

        bool firstRun;
        try
        {
            // Safety net: before the user changes anything, remember how things were.
            var services = AppServices.Current;
            firstRun = services.Snapshots.EnsureOriginal(services.Engine);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Restyle couldn't save a backup of your current settings, so it won't make any changes.\n\n{(ex.InnerException ?? ex).Message}",
                "Restyle", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (firstRun)
        {
            window.ShowMessage(
                "Welcome! Restyle saved a copy of your current settings, so you can always go back with Backups > Restore my original settings.",
                MessageKind.Success);
        }
    }
}
