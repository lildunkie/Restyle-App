using System.Windows;
using System.Windows.Controls;
using Restyle.App.Controls;

namespace Restyle.App.Dialogs;

/// <summary>A minimal "type a name" dialog.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;
    private bool _ok;

    private InputDialog(string title, string prompt, string initial)
    {
        Title = title;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(Ui.Text(prompt, margin: new Thickness(0, 0, 0, 8)));
        _box = new TextBox { Text = initial };
        root.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var ok = Ui.Button("Save", (_, _) => { _ok = true; Close(); }, accent: true);
        ok.IsDefault = true;
        var cancel = Ui.Button("Cancel", (_, _) => Close());
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    /// <summary>Returns the trimmed text, or null if cancelled/empty.</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = owner };
        dialog.ShowDialog();
        var text = dialog._box.Text.Trim();
        return dialog._ok && text.Length > 0 ? text : null;
    }
}
