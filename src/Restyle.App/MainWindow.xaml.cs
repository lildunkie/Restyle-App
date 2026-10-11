using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Restyle.App.Pages;
using Restyle.App.Services;
using Restyle.Core.Engine;

namespace Restyle.App;

public enum MessageKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Pages re-read current values when shown and after every change (e.g. an undo).</summary>
public interface IPage
{
    void Refresh();
}

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private Action? _bannerAction;
    private string _currentPage = "presets";

    public MainWindow(string? startPage = null)
    {
        InitializeComponent();
        Services.Engine.History.Changed += UpdateUndo;
        UpdateUndo();
        ShowPage(startPage ?? "presets");

        // Offer a restart when a newer Restyle has been installed while this window is open.
        var updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        updateTimer.Tick += (_, _) =>
        {
            if (!UpdateCheck.UpdateReady())
                return;
            updateTimer.Stop();
            ShowMessage("Restyle has been updated. Restart it to use the new version.", MessageKind.Info, "Restart now", () =>
            {
                // Leave immediately: this old copy's normal shutdown needs parts of the program file that were just replaced.
                UpdateCheck.Relaunch($"--page {_currentPage}");
                Environment.Exit(0);
            });
        };
        updateTimer.Start();
    }

    public static MainWindow Instance => (MainWindow)Application.Current.MainWindow;

    /// <summary>Restyle was opened again: show this window instead of starting a second copy.</summary>
    public void BringToFront(string? page)
    {
        if (PresentationSource.FromVisual(this) is null)
        {
            // This window is already gone (we're on our way out): step aside so the new copy can open.
            Environment.Exit(0);
            return;
        }

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true; // Windows only lets a background app come forward like this
        Topmost = false;
        if (page is { Length: > 0 })
            ShowPage(page);
        else
            (PageHost.Content as IPage)?.Refresh(); // show the current state (another window may have changed things)
    }

    private static AppServices Services => AppServices.Current;

    /// <summary>Opens a page by name ("presets", "appearance", "taskbar", "dock", "widgets", "backups", "about").</summary>
    public void ShowPage(string page)
    {
        page = page == "customize" ? "appearance" : page; // the old name for Appearance
        var item = Nav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == page)
            ?? Nav.Children.OfType<RadioButton>().First();
        if (item.IsChecked == true)
            Navigate((string)item.Tag);
        else
            item.IsChecked = true; // navigates via Nav_Checked
    }

    public void Navigate(string page)
    {
        _currentPage = page;
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "presets" => new PresetsPage(),
                "appearance" => new CustomizePage(),
                "taskbar" => new TaskbarPage(),
                "dock" => new DockPage(),
                "widgets" => new WidgetsPage(),
                "backups" => new BackupsPage(),
                _ => new AboutPage(),
            };
            _pages[page] = view;
        }
        else
        {
            (view as IPage)?.Refresh();
        }

        PageHost.Content = view;
    }

    /// <summary>
    /// Runs a change through the engine, then reports the outcome, refreshes the page and,
    /// only if a changed setting needs it, asks before restarting Explorer.
    /// </summary>
    public void Run(Func<ApplyResult?> change, string successText)
    {
        ApplyResult? result;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            result = change();
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, MessageKind.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            (PageHost.Content as IPage)?.Refresh();
        }

        if (result is null)
        {
            ShowMessage("There's nothing to undo.", MessageKind.Info);
            return;
        }

        Report(result, successText);
        if (result.NeedsExplorerRestart)
            AskToRestartExplorer(result);
    }

    public void ShowMessage(string text, MessageKind kind, string? actionText = null, Action? action = null)
    {
        BannerText.Text = text;
        (BannerIcon.Text, var brush) = kind switch
        {
            MessageKind.Success => ("", "SystemFillColorSuccessBackgroundBrush"),
            MessageKind.Warning => ("", "SystemFillColorCautionBackgroundBrush"),
            MessageKind.Error => ("", "SystemFillColorCriticalBackgroundBrush"),
            _ => ("", "SystemFillColorAttentionBackgroundBrush"),
        };
        Banner.SetResourceReference(Border.BackgroundProperty, brush);

        _bannerAction = action;
        BannerAction.Content = actionText;
        BannerAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Banner.Visibility = Visibility.Visible;
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Instance.ShowMessage($"Couldn't open {uri}: {ex.Message}", MessageKind.Error);
        }
    }

    private void Report(ApplyResult result, string successText)
    {
        if (result.Failures.Count == 0)
        {
            if (result.Applied.Count == 0)
                ShowMessage("Already set that way. Nothing needed to change.", MessageKind.Info);
            else
                ShowMessage(successText, MessageKind.Success);
            return;
        }

        var lines = result.Failures.Select(f => $"• {f.Setting.Name}: {f.Message}");
        var intro = result.Applied.Count > 0
            ? $"{result.Applied.Count} change(s) applied, but some didn't work:"
            : "That didn't work:";
        var link = result.Failures.FirstOrDefault(f => f.SettingsUri is not null)?.SettingsUri;
        ShowMessage($"{intro}\n{string.Join("\n", lines)}",
            result.Applied.Count > 0 ? MessageKind.Warning : MessageKind.Error,
            link is null ? null : "Open Settings",
            link is null ? null : () => OpenUri(link));
    }

    private void AskToRestartExplorer(ApplyResult result)
    {
        var names = string.Join(", ", result.Applied.Where(c => c.Setting.RequiresExplorerRestart)
            .Select(c => $"{c.Setting.Name}: {c.AfterText}"));
        var answer = MessageBox.Show(this,
            $"To show this change ({names}), Windows Explorer needs to restart.\n\n" +
            "Your taskbar will disappear for a moment, and any open File Explorer windows will close. " +
            "Other apps aren't affected.\n\nRestart Explorer now?",
            "Restart Explorer?", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
            RestartExplorer();
        else
            ShowMessage("Saved. You'll see it the next time you sign in, or when you restart Explorer.",
                MessageKind.Info, "Restart Explorer now", RestartExplorer);
    }

    private async void RestartExplorer()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await Task.Run(Services.Catalog.Context.System.RestartExplorer);
            ShowMessage("Explorer restarted. Your changes are now visible.", MessageKind.Success);
        }
        catch (Exception ex)
        {
            ShowMessage($"Couldn't restart Explorer: {ex.Message}. Signing out and back in will also apply the change.", MessageKind.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void UpdateUndo()
    {
        var last = Services.Engine.History.Last;
        UndoButton.IsEnabled = last is not null;
        UndoLabel.Text = last?.Label ?? "Nothing to undo";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var label = Services.Engine.History.Last?.Label;
        Run(Services.Engine.UndoLast, $"Undone: {label}");
    }

    private void Nav_Checked(object sender, RoutedEventArgs e) =>
        Navigate((string)((FrameworkElement)sender).Tag);

    private void CloseBanner_Click(object sender, RoutedEventArgs e) => Banner.Visibility = Visibility.Collapsed;

    private void BannerAction_Click(object sender, RoutedEventArgs e)
    {
        Banner.Visibility = Visibility.Collapsed;
        _bannerAction?.Invoke();
    }
}
