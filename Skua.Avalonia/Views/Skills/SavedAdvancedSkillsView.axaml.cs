using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using Skua.Core.Interfaces;
using Skua.Core.Models.Skills;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Views.Skills;

/// <summary>
/// The saved skill sets, filtered by the search. Double-clicking a set edits it; ⌘C copies the selected sets to the clipboard, and ⌘V pastes
/// sets from it into the saved ones, replacing any with the same class and mode.
/// </summary>
/// <remarks>
/// Ports the WPF control's filtered collection view and its copy and paste. The copy goes through <see cref="IClipboardService"/> as text
/// (<see cref="SkillSetText"/>), and a paste saves the sets, where WPF's added them to the list alone until it next reloaded. Core says the
/// sets changed on the thread that saved them, so the list is refilled on the UI thread.
/// </remarks>
public partial class SavedAdvancedSkillsView : UserControl
{
    private SavedAdvancedSkillsViewModel? _model;

    public SavedAdvancedSkillsView()
    {
        InitializeComponent();
        SearchBox.TextChanged += (_, _) => Refill();
        SkillsList.KeyDown += OnListKeyDown;
        SkillsList.DoubleTapped += (_, _) => _model?.EditSelectedCommand.Execute(null);
        CopyItem.Click += (_, _) => Copy();
        PasteItem.Click += (_, _) => Paste();
    }

    /// <summary>The clipboard the sets are copied to and pasted from; the app's own unless a test gives it another.</summary>
    public IClipboardService Clipboard { get; set; } = Ioc.Default.GetRequiredService<IClipboardService>();

    /// <summary>The sets the list shows.</summary>
    public IReadOnlyList<AdvancedSkill> Shown { get; private set; } = [];

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = DataContext as SavedAdvancedSkillsViewModel;
        if (_model is not null)
            _model.PropertyChanged += OnModelChanged;
        Refill();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SavedAdvancedSkillsViewModel.LoadedSkills))
            Dispatcher.UIThread.Post(Refill);
    }

    /// <summary>
    /// Shows the sets that match the search, keeping the selected ones selected, and the focus on the list if it had it: Core makes new sets
    /// each time it reloads them, and the old rows go.
    /// </summary>
    private void Refill()
    {
        string search = SearchBox.Text ?? string.Empty;
        bool focused = SkillsList.IsKeyboardFocusWithin;
        List<AdvancedSkill> selected = SkillsList.SelectedItems?.OfType<AdvancedSkill>().ToList() ?? [];
        List<AdvancedSkill> shown;
        try
        {
            shown = _model is null ? [] : _model.LoadedSkills.Where(s => s.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (InvalidOperationException)
        {
            // Core reloads its list on the thread that saved it, and says so when it's done.
            return;
        }
        Shown = shown;
        SkillsList.ItemsSource = Shown;
        // A set is equal to another with its class and mode, so the list keeps some selected across the new source, and adding them again
        // would select them twice.
        SkillsList.SelectedItems?.Clear();
        foreach (AdvancedSkill set in Shown.Where(selected.Contains))
            SkillsList.SelectedItems?.Add(set);
        if (focused)
            SkillsList.Focus();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Meta || e.Key is not (Key.C or Key.V))
            return;
        if (e.Key == Key.C)
            Copy();
        else
            Paste();
        e.Handled = true;
    }

    private void Copy()
    {
        List<AdvancedSkill> selected = SkillsList.SelectedItems?.OfType<AdvancedSkill>().ToList() ?? [];
        if (selected.Count > 0)
            Clipboard.SetText(SkillSetText.From(selected));
    }

    private void Paste()
    {
        IReadOnlyList<AdvancedSkill> sets = SkillSetText.Parse(Clipboard.GetText());
        if (sets.Count == 0)
            return;
        IAdvancedSkillContainer container = Ioc.Default.GetRequiredService<IAdvancedSkillContainer>();
        // Saved once for them all: each save reloads Core's list from the file on another thread.
        foreach (AdvancedSkill set in sets)
        {
            int index = container.LoadedSkills.IndexOf(set);
            if (index >= 0)
                container.LoadedSkills[index] = set;
            else
                container.LoadedSkills.Add(set);
        }
        container.Save();
    }
}
