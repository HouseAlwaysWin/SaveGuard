using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SaveGuard.Views;

/// <summary>A small modal yes/no confirm. Built in code since Avalonia ships no
/// message box. Returns true on the primary action.</summary>
public sealed class ConfirmDialog : Window
{
    private bool _result;

    private ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#1E2128"));

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#E7E9ED")),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var body = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.Parse("#8B93A0")),
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 20),
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(14, 9),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#252934")),
            Foreground = new SolidColorBrush(Color.Parse("#E7E9ED")),
        };
        cancel.Click += (_, _) => { _result = false; Close(); };

        var ok = new Button
        {
            Content = "Confirm",
            Padding = new Thickness(16, 9),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#E0A24E")),
            Foreground = new SolidColorBrush(Color.Parse("#1A1206")),
            FontWeight = FontWeight.SemiBold,
        };
        ok.Click += (_, _) => { _result = true; Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };

        Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel { Children = { heading, body, buttons } },
        };
    }

    public static async Task<bool> Show(Window owner, string title, string message)
    {
        var dlg = new ConfirmDialog(title, message);
        await dlg.ShowDialog(owner);
        return dlg._result;
    }
}
