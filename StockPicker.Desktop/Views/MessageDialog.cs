using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Minimal code-built modal message / confirmation dialog.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION: Avalonia has no <c>MessageBox</c>. The WPF <c>MessageBox.Show(...)</c> calls
/// in the MainWindow code-behind (Yes/No confirmations for destructive removes; informational
/// "nothing selected" / "nothing to save" prompts) are replaced with this tiny owner-modal
/// window opened via <c>await ShowDialog&lt;bool&gt;(owner)</c> (true = Yes/OK). It is built in
/// code (no XAML) to stay self-contained and keep the dialog set unchanged.
/// </remarks>
public static class MessageDialog
{
    /// <summary>Yes/No confirmation. Returns true when the user confirms.</summary>
    public static Task<bool> ConfirmAsync(Window owner, string title, string message)
        => ShowAsync(owner, title, message, confirm: true);

    /// <summary>Informational message with a single OK button. Always returns true.</summary>
    public static Task<bool> InfoAsync(Window owner, string title, string message)
        => ShowAsync(owner, title, message, confirm: false);

    private static Task<bool> ShowAsync(Window owner, string title, string message, bool confirm)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16),
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var win = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var ok = new Button
        {
            Content = confirm ? "Yes" : "OK",
            IsDefault = true,
            MinWidth = 74,
        };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => win.Close(true);
        buttons.Children.Add(ok);

        if (confirm)
        {
            var no = new Button { Content = "No", IsCancel = true, MinWidth = 74 };
            no.Click += (_, _) => win.Close(false);
            buttons.Children.Add(no);
        }

        win.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children = { text, buttons },
        };

        return win.ShowDialog<bool>(owner);
    }
}
