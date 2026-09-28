using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views;

/// <summary>
/// Loads a shop or quests by ID, and lists the quests in <c>QuestData.json</c>, searched as the Windows view searches them; ⌘L loads the
/// selected quests, ⌘C copies their IDs and ⇧⌘C their names.
/// </summary>
public partial class LoaderView : UserControl
{
    public static readonly DirectProperty<LoaderView, IList<object>> SelectedQuestsProperty =
        AvaloniaProperty.RegisterDirect<LoaderView, IList<object>>(nameof(SelectedQuests), v => v.SelectedQuests);

    public static readonly DirectProperty<LoaderView, bool> OneQuestSelectedProperty =
        AvaloniaProperty.RegisterDirect<LoaderView, bool>(nameof(OneQuestSelected), v => v.OneQuestSelected);

    private readonly SearchFilter _filter;
    private IList<object> _selectedQuests = [];
    private bool _oneQuestSelected;
    private bool _questsRead;

    public LoaderView()
    {
        InitializeComponent();
        _filter = new SearchFilter(Quests, SearchBox, (item, search) => item.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
        Quests.SelectionChanged += (_, _) =>
        {
            SetAndRaise(SelectedQuestsProperty, ref _selectedQuests, [.. Quests.SelectedItems?.Cast<object>() ?? []]);
            SetAndRaise(OneQuestSelectedProperty, ref _oneQuestSelected, _selectedQuests.Count == 1);
        };
        Quests.KeyDown += OnQuestsKeyDown;
    }

    /// <summary>The quests selected in the list, for the buttons' commands.</summary>
    public IList<object> SelectedQuests => _selectedQuests;

    public bool OneQuestSelected => _oneQuestSelected;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _filter.Source = (DataContext as LoaderViewModel)?.QuestIDs;
    }

    /// <summary>As on Windows, the quest list is read from the file once the view first shows.</summary>
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (_questsRead || DataContext is not LoaderViewModel viewModel)
            return;
        _questsRead = true;
        try
        {
            await viewModel.GetQuestsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            // An unreadable QuestData.json leaves the list empty rather than taking the app, and its game, down.
            Trace.WriteLine($"The Loader couldn't read QuestData.json: {ex.Message}");
        }
    }

    private void OnQuestsKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not LoaderViewModel viewModel || !e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.L when !shift:
                viewModel.LoadQuestsCommand.Execute(SelectedQuests);
                break;
            case Key.C when shift:
                viewModel.CopyQuestsNamesCommand.Execute(SelectedQuests);
                break;
            case Key.C:
                viewModel.CopyQuestsIDsCommand.Execute(SelectedQuests);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
