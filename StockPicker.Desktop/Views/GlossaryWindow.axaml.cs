using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using StockPicker.Desktop.ViewModels;

namespace StockPicker.Desktop.Views;

/// <summary>
/// Searchable, category-grouped glossary panel. Reads the canonical
/// <c>StockPicker.Reference.Glossary</c> through <see cref="GlossaryViewModel"/>.
/// </summary>
/// <remarks>
/// Opened owner-modal from <c>MainWindow</c> via <c>await new GlossaryWindow().ShowDialog(this)</c>.
/// Its own <see cref="GlossaryViewModel"/> is created in the constructor (no shared VM state).
/// </remarks>
public partial class GlossaryWindow : Window
{
    public GlossaryWindow()
    {
        InitializeComponent();
        DataContext = new GlossaryViewModel();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
