using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Views;

/// <summary>
/// A list that follows a view model's growing collection, such as a packet log, showing the items the view's filter keeps. Ports the WPF
/// views' filtered collection views and <c>ListBoxScrollToCaretBehavior</c> for a collection that may grow on any thread: new items are
/// appended on the UI thread, keeping the selection, and followed while the list is scrolled to its end; a removal, a clear or
/// <see cref="Refresh"/> (the search or a filter changed) filters every item again.
/// </summary>
internal sealed class FollowingList
{
    private readonly ListBox _list;
    private readonly Func<object, bool> _keep;
    private readonly object _lock = new();
    private IList? _source;
    private ObservableCollection<object> _shown = [];
    /// <summary>How many of the source's items the list has looked at.</summary>
    private int _seen;
    private bool _rebuild;
    private bool _queued;

    public FollowingList(ListBox list, Func<object, bool> keep)
    {
        _list = list;
        _keep = keep;
        _list.ItemsSource = _shown;
    }

    /// <summary>The view model's items, or null when the view has none.</summary>
    public IList? Source
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

    /// <summary>The items the list shows.</summary>
    public IReadOnlyList<object> Shown => _shown;

    /// <summary>Filters every item again, on the UI thread.</summary>
    public void Refresh()
    {
        object[] items = Snapshot();
        _seen = items.Length;
        _shown = new ObservableCollection<object>(items.Where(_keep));
        _list.ItemsSource = _shown;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        lock (_lock)
        {
            _rebuild |= e.Action != NotifyCollectionChangedAction.Add;
            if (_queued)
                return;
            _queued = true;
        }
        if (Dispatcher.UIThread.CheckAccess())
            Sync();
        else
            Dispatcher.UIThread.Post(Sync);
    }

    private void Sync()
    {
        bool rebuild;
        lock (_lock)
        {
            rebuild = _rebuild;
            _rebuild = false;
            _queued = false;
        }
        if (_source is not { } source)
            return;
        if (rebuild || source.Count < _seen)
        {
            Refresh();
            return;
        }

        ScrollViewer? scroll = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        bool atEnd = scroll is null || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
        bool added = false;
        try
        {
            for (int count = source.Count; _seen < count; _seen++)
            {
                object? item = source[_seen];
                if (item is not null && _keep(item))
                {
                    _shown.Add(item);
                    added = true;
                }
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Cleared on another thread meanwhile; its change comes back here.
        }
        if (added && atEnd && scroll is not null)
            Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>The source's items, copied while another thread may be adding to it.</summary>
    private object[] Snapshot()
    {
        if (_source is not { } source)
            return [];
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                object[] items = new object[source.Count];
                source.CopyTo(items, 0);
                return items;
            }
            catch (ArgumentException) when (attempt < 10)
            {
                // It grew between the count and the copy.
            }
        }
    }

    /// <summary>
    /// Ports <c>ListBoxCopySelectedBehavior</c> and <c>ListBoxUnselectAllMenuBehavior</c>: ⌘C copies the selected items, a line each, and the
    /// list's context menu copies them or unselects them all.
    /// </summary>
    public static void CopyAndUnselect(ListBox list)
    {
        list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Meta) || list.SelectedItems is not { Count: > 0 })
                return;
            CopySelected(list);
            e.Handled = true;
        };
        MenuItem copy = new() { Header = "Copy" };
        copy.Click += (_, _) => CopySelected(list);
        MenuItem unselectAll = new() { Header = "Unselect All" };
        unselectAll.Click += (_, _) => list.UnselectAll();
        list.ContextMenu = new ContextMenu { Items = { copy, unselectAll } };
    }

    private static void CopySelected(ListBox list)
    {
        if (list.SelectedItems is not { Count: > 0 } selected)
            return;
        Ioc.Default.GetRequiredService<IClipboardService>().SetText(string.Join(Environment.NewLine, selected.Cast<object>().Select(i => i.ToString())));
    }
}
