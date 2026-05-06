using System.Windows;

namespace StockPicker.Views
{
    /// <summary>
    /// Application settings dialog.
    /// DataContext is set by the caller (MainWindow) to the existing MainViewModel,
    /// so all controls bind directly to the shared view-model without duplication.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
