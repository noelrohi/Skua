using System.Globalization;
using Skua.Core.Interfaces;
using Skua.Core.Options;
using Skua.Core.ViewModels;
using Skua.Engine;

namespace Skua.Avalonia.Services;

/// <summary>
/// A Script's options in the Mac App: its options window is Core's editor (<see cref="OptionContainerViewModel"/>), shown with
/// <see cref="IDialogService"/>'s <c>ShowDialog</c> as on Windows, and it saves to the options file that the Engine's
/// <c>script_options</c> and the next run read. <c>skua-engine</c> keeps <c>HeadlessScriptOptionContainer</c>.
/// </summary>
/// <remarks>
/// Two changes from Core's own window, both for an Engine that agents drive too. It shows the values as they are, where Core first resets
/// them to the defaults and reloads them, which a running Script could read in between. And closing it saves only the options the
/// developer changed, over the file as it is then, so a value an agent stored with <c>skua script start --option</c> while the editor was
/// open survives; a saved choice that no longer exists, reset by the load, is saved at its default too. A run nobody asked for, such as CoreBots' restart after a relogin, opens no window and goes on with the saved options (#144).
/// </remarks>
public sealed class AvaloniaScriptOptionContainer : ScriptOptionContainer, IScriptOptionContainer
{
    private readonly IDialogService _dialogs;
    private readonly EngineScripts _scripts;

    public AvaloniaScriptOptionContainer(IDialogService dialogs, EngineScripts scripts)
        : base(dialogs)
    {
        _dialogs = dialogs;
        _scripts = scripts;
    }

    /// <summary>Shows the options editor and waits until it closes, except during an agent's start; closing it, however it closes, saves what changed, as on Windows.</summary>
    public new void Configure()
    {
        if (_scripts.RunningUnasked)
            return;
        // Core waits here at the start of a Script with a removed choice; an agent's start must not wait on a person, with the Engine busy.
        // The Script runs with the default, as with the editor Core opens once a Script has started.
        if (_scripts.StartingForControlSurface)
            _ = Task.Run(ShowEditor);
        else
            ShowEditor();
    }

    private void ShowEditor()
    {
        OptionContainerViewModel editor = new(this);
        Dictionary<IOption, string> shown = editor.Options.ToDictionary(o => o.Option, Edited);
        bool hadResets = ResetOptions.Count > 0;
        _dialogs.ShowDialog(editor, closed => SaveChanges(closed, shown, hadResets));
    }

    private void SaveChanges(OptionContainerViewModel editor, Dictionary<IOption, string> shown, bool hadResets)
    {
        List<(IOption Option, string Value)> changed = editor.Options
            .Select(o => (o.Option, Value: Edited(o)))
            .Where(o => o.Value != shown[o.Option])
            .ToList();
        if (changed.Count == 0 && !hadResets)
            return;

        Load();
        foreach ((IOption option, string value) in changed)
            OptionValues[option] = value;
        // A transient option changes only this run's value, as Core's Set leaves it unsaved.
        if (hadResets || changed.Any(c => !c.Option.Transient))
            Save();
        // The file holds them now, so the editor doesn't report them again before the next load.
        ResetOptions.Clear();
    }

    /// <summary>The editor's value as Core stores it: an enum by its name with spaces, anything else with the current culture.</summary>
    private static string Edited(OptionContainerItemViewModel item) =>
        (item.Type.IsEnum ? item.SelectedValue : Convert.ToString(item.Value, CultureInfo.CurrentCulture)) ?? string.Empty;
}
