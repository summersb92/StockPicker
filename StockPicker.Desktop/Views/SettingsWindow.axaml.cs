using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Application settings dialog.
/// DataContext is set by the caller (MainWindow) to the existing MainViewModel,
/// so all controls bind directly to the shared view-model without duplication.
/// </summary>
/// <remarks>
/// WPF-ADAPTATION result contract: this dialog carries no return value — every setting
/// auto-saves through the bound view-model. Opened with a fire-and-forget
/// <c>await ShowDialog(owner)</c> (or <c>ShowDialog&lt;object?&gt;</c>); the result is
/// unused. <see cref="Close_Click"/> just closes the window (WPF <c>Close()</c> is unchanged;
/// there is no <c>DialogResult</c> in Avalonia).
/// </remarks>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
