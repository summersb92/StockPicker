using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace StockPicker.ViewModels
{
    /// <summary>
    /// <see cref="ObservableCollection{T}"/> variant that replaces all items
    /// with a single <see cref="NotifyCollectionChangedAction.Reset"/> notification.
    ///
    /// Using <c>Clear()</c> followed by individual <c>Add()</c> calls forces the
    /// DataGrid to repaint for every item, causing a visible blank-then-refill flash.
    /// <c>ReplaceAll</c> swaps the backing store without leaving the grid empty,
    /// so the repaint happens exactly once and the transition is seamless.
    ///
    /// IMPORTANT: must be called from the UI thread, exactly like any
    /// <see cref="ObservableCollection{T}"/> mutation.
    /// </summary>
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>
        /// Atomically replaces the collection's contents and fires a single Reset notification.
        /// </summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);

            // Raise in the same order ObservableCollection normally would.
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
