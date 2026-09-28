using System.Collections;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Skua.Avalonia.Views;

/// <summary>
/// A list's items filtered by a search box, as the WPF views filter theirs with a collection view: with no search the list shows the view
/// model's collection itself, and with one a copy of the items that match, made again when the search or the collection changes.
/// </summary>
internal sealed class SearchFilter
{
    private readonly ItemsControl _list;
    private readonly TextBox _search;
    private readonly Func<object, string, bool> _matches;
    private IEnumerable? _source;
    private bool _refreshQueued;

    public SearchFilter(ItemsControl list, TextBox search, Func<object, string, bool> matches)
    {
        _list = list;
        _search = search;
        _matches = matches;
        _search.TextChanged += (_, _) => Refresh();
    }

    /// <summary>The view model's items, or null when the view has none.</summary>
    public IEnumerable? Source
    {
        get => _source;
        set
        {
            if (_source is INotifyCollectionChanged old)
                old.CollectionChanged -= OnSourceChanged;
            _source = value;
            if (_source is INotifyCollectionChanged changing)
                changing.CollectionChanged += OnSourceChanged;
            Refresh();
        }
    }

    public void Refresh()
    {
        _refreshQueued = false;
        string search = _search.Text?.Trim() ?? "";
        if (_source is null || search.Length == 0)
        {
            if (!ReferenceEquals(_list.ItemsSource, _source))
                _list.ItemsSource = _source;
            return;
        }
        try
        {
            _list.ItemsSource = _source.Cast<object>().Where(item => _matches(item, search)).ToList();
        }
        catch (InvalidOperationException)
        {
            // Another thread is changing the collection; its change comes back here once done.
        }
    }

    /// <summary>A searched list is a copy, so it is made again, once per burst of changes, on the UI thread.</summary>
    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshQueued || ReferenceEquals(_list.ItemsSource, _source))
            return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(Refresh);
    }
}
