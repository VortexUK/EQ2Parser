using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EQ2Parser.App.Services;

/// <summary>
/// ObservableCollection whose <see cref="ReplaceAll"/> swaps the entire
/// contents behind ONE Reset notification. A virtualized ItemsControl
/// answers Reset by regenerating just the visible containers; per-item
/// Clear+Add on a big list instead pushes hundreds of individual changes
/// through the container generator — the parse tree's laggy collapse.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
