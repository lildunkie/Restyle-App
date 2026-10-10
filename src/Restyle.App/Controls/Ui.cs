using System.Windows;
using System.Windows.Controls;

namespace Restyle.App.Controls;

/// <summary>Small helpers for building UI in code with the app's styles.</summary>
public static class Ui
{
    /// <summary>A wrapping tooltip (WPF tooltips don't wrap plain strings).</summary>
    public static ToolTip Tip(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 },
    };

    public static TextBlock Text(string text, string? style = null, Thickness? margin = null)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (style is not null)
            block.Style = (Style)Application.Current.FindResource(style);
        if (margin is Thickness m)
            block.Margin = m;
        return block;
    }

    public static Button Button(string text, RoutedEventHandler click, bool accent = false, string? tip = null, bool enabled = true)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 6), IsEnabled = enabled };
        if (accent)
            button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        if (tip is not null)
            button.ToolTip = Tip(tip);
        button.Click += click;
        return button;
    }

    public static Border Card(UIElement child, Thickness? margin = null) => new()
    {
        Style = (Style)Application.Current.FindResource("Card"),
        Child = child,
        Margin = margin ?? new Thickness(0, 0, 0, 4),
    };

    public static string FriendlyTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today ? $"Today at {local:t}" : local.ToString("g");
    }
}
