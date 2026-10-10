using System.Windows;
using System.Windows.Controls;
using Restyle.App.Controls;
using Restyle.App.Dialogs;
using Restyle.App.Services;
using Restyle.Core.Backup;

namespace Restyle.App.Pages;

/// <summary>Undo, restore the original settings, named backups, and the recent change list.</summary>
public sealed class BackupsPage : UserControl, IPage
{
    private readonly StackPanel _root = new() { Margin = new Thickness(28, 20, 28, 28), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };

    public BackupsPage()
    {
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Refresh();
    }

    private static AppServices S => AppServices.Current;

    public void Refresh()
    {
        _root.Children.Clear();
        _root.Children.Add(Ui.Text("Backups", "PageTitle"));
        _root.Children.Add(Ui.Text("Every change Restyle makes can be undone. Your original settings were saved the first time you opened Restyle.", "PageSubtitle"));

        var last = S.Engine.History.Last;
        _root.Children.Add(ActionCard(
            "Undo last change",
            last is null ? "Nothing to undo yet." : $"{last.Label} ({Ui.FriendlyTime(last.Time)})",
            Ui.Button("Undo", (_, _) => MainWindow.Instance.Run(S.Engine.UndoLast, $"Undone: {last?.Label}"), enabled: last is not null)));

        var original = S.Snapshots.LoadOriginal();
        _root.Children.Add(ActionCard(
            "Restore my original settings",
            original is null
                ? "No original snapshot found."
                : $"Puts everything Restyle can change back to how it was on {original.Created.ToLocalTime():D}.",
            Ui.Button("Restore…", (_, _) => Restore(original!, "Restore original settings"), accent: true, enabled: original is not null)));

        _root.Children.Add(Ui.Text("Your backups", "SectionHeader"));
        var create = new DockPanel();
        var save = Ui.Button("Back up my current settings…", (_, _) => CreateBackup(),
            tip: "Saves a snapshot of all your current settings that you can restore later.");
        create.Children.Add(save);
        _root.Children.Add(create);

        var backups = S.Snapshots.ListBackups();
        if (backups.Count == 0)
            _root.Children.Add(Ui.Text("No backups yet.", "Hint", new Thickness(2, 10, 0, 0)));
        foreach (var backup in backups)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(Ui.Button("Restore…", (_, _) => Restore(backup, $"Restore backup \"{backup.Name}\"")));
            var delete = Ui.Button("Delete", (_, _) => DeleteBackup(backup));
            delete.Margin = new Thickness(0);
            buttons.Children.Add(delete);
            var card = ActionCard(backup.Name, Ui.FriendlyTime(backup.Created), buttons);
            card.Margin = new Thickness(0, 8, 0, 0);
            _root.Children.Add(card);
        }

        _root.Children.Add(Ui.Text("Recent changes", "SectionHeader"));
        var history = S.Engine.History.Batches.Reverse().Take(15).ToList();
        if (history.Count == 0)
            _root.Children.Add(Ui.Text("Changes you make will be listed here.", "Hint", new Thickness(2, 0, 0, 0)));
        foreach (var batch in history)
        {
            var names = string.Join(", ", batch.Changes.Select(c => S.Catalog.Find(c.SettingId)?.Name ?? c.SettingId));
            _root.Children.Add(Ui.Text($"{Ui.FriendlyTime(batch.Time)}  ·  {batch.Label}  ·  {names}", "Hint", new Thickness(2, 2, 0, 2)));
        }
    }

    private static Border ActionCard(string title, string detail, UIElement action)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(Ui.Text(title));
        text.Children.Add(Ui.Text(detail, "Hint"));
        grid.Children.Add(text);
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return Ui.Card(grid);
    }

    private void Restore(Snapshot snapshot, string label)
    {
        var plan = S.Engine.Plan(snapshot.Settings);
        if (plan.IsEmpty)
        {
            MainWindow.Instance.ShowMessage("Your settings already match this backup. Nothing to restore.", MessageKind.Info);
            return;
        }

        var lines = string.Join("\n", plan.Changes.Select(c => $"• {c.Setting.Name}: {c.BeforeText} → {c.AfterText}"));
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"This will change {plan.Changes.Count} setting(s):\n\n{lines}\n\nYou can undo this afterwards. Continue?",
            label, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
            MainWindow.Instance.Run(() => S.Engine.Apply(plan, label), "Restored. Use Undo last change if you want to go back.");
    }

    private void CreateBackup()
    {
        var name = InputDialog.Ask(Window.GetWindow(this), "Back up settings", "Name this backup:", $"Backup {DateTime.Now:g}");
        if (name is null)
            return;

        try
        {
            S.Snapshots.SaveBackup(name, S.Engine);
            MainWindow.Instance.ShowMessage($"Backed up your current settings as \"{name}\".", MessageKind.Success);
        }
        catch (Exception ex)
        {
            MainWindow.Instance.ShowMessage($"Couldn't save the backup: {ex.Message}", MessageKind.Error);
        }

        Refresh();
    }

    private void DeleteBackup(Snapshot backup)
    {
        var answer = MessageBox.Show(Window.GetWindow(this), $"Delete the backup \"{backup.Name}\"? Your current settings won't change.",
            "Delete backup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        S.Snapshots.DeleteBackup(backup);
        Refresh();
    }
}
