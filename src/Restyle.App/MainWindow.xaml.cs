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

    public MainWindow()
    {
        InitializeComponent();
        Services.Engine.History.Changed += UpdateUndo;
        UpdateUndo();
        NavPresets.IsChecked = true;
    }

    public static MainWindow Instance => (MainWindow)Application.Current.MainWindow;

    private static AppServices Services => AppServices.Current;

    public void Navigate(string page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = page switch
            {
                "presets" => new PresetsPage(),
                "customize" => new CustomizePage(),
                "dock" => new DockPage(),
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
