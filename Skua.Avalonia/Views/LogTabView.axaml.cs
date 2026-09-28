using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>A log's lines, following new ones while scrolled to the end; ⌘C copies the selected lines.</summary>
/// <remarks>Ports <c>ListBoxScrollToCaretBehavior</c> and <c>ListBoxCopySelectedBehavior</c>.</remarks>
public partial class LogTabView : UserControl
{
    private INotifyCollectionChanged? _logs;

    public LogTabView()
    {
        InitializeComponent();
        Lines.KeyDown += OnKeyDown;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_logs is not null)
            _logs.CollectionChanged -= OnLogsChanged;
        _logs = (DataContext as LogTabViewModel)?.Logs;
        if (_logs is not null)
            _logs.CollectionChanged += OnLogsChanged;
    }

    /// <remarks>Core adds lines on the UI thread (<c>LogTabViewModel.AddLog</c> dispatches them).</remarks>
    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || Lines.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
            return;
        bool atEnd = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
        if (atEnd)
            Dispatcher.UIThread.Post(() => scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Meta) || Lines.SelectedItems is not { Count: > 0 } selected)
            return;
        Ioc.Default.GetRequiredService<IClipboardService>().SetText(string.Join(Environment.NewLine, selected.OfType<string>()));
        e.Handled = true;
    }
}
