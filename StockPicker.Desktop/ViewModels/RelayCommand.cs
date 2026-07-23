using System.Windows.Input;

namespace StockPicker.Desktop.ViewModels
{
    /// <summary>
    /// Simple ICommand implementation that relays Execute/CanExecute to delegates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ICommand"/> is <c>System.Windows.Input.ICommand</c> in the shared BCL —
    /// Avalonia binds to the same type, so the interface is unchanged.
    /// </para>
    /// <para>
    /// WPF-ADAPTATION: WPF's original implementation routed <see cref="CanExecuteChanged"/>
    /// through <c>System.Windows.Input.CommandManager.RequerySuggested</c>, which does not
    /// exist in Avalonia. Here we back the event with a plain delegate and re-query on demand
    /// via <see cref="RaiseCanExecuteChanged"/> (call it after mutating state that affects
    /// <c>CanExecute</c>). Avalonia has no automatic requery, so callers are responsible for
    /// raising the change explicitly — this matches Avalonia/CommunityToolkit conventions.
    /// </para>
    /// </remarks>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        { }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public event EventHandler? CanExecuteChanged;

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
