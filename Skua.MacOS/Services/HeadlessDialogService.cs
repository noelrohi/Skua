using System.Diagnostics;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.MacOS.Services;

/// <summary>
/// Script Dialogs with no window to show them in: an OK-only message box is a Notice, which never waits, and a yes/no or buttons message box
/// is a Question for the <see cref="ScriptDialogBroker"/>. Other dialogs are never surfaced.
/// </summary>
public sealed class HeadlessDialogService : IDialogService
{
    private static readonly string[] YesNo = ["Yes", "No"];

    private readonly ScriptDialogBroker _broker;

    public HeadlessDialogService(ScriptDialogBroker broker)
    {
        _broker = broker;
    }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => NotSurfaced(typeof(TViewModel).Name);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title) where TViewModel : class => NotSurfaced(Title);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class => NotSurfaced(typeof(TViewModel).Name);

    public void ShowMessageBox(string message, string caption) => _broker.Notice(caption, message);

    /// <returns>Null for a Notice, and for a Question that got the fallback; else whether the answer was Yes.</returns>
    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        if (!yesAndNo)
        {
            _broker.Notice(caption, message);
            return null;
        }
        return _broker.Ask(caption, message, YesNo) is { } choice ? choice == 0 : null;
    }

    /// <returns>The chosen button, or <see cref="DialogResult.Cancelled"/> for the fallback and for a message box without buttons, which is a Notice.</returns>
    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        if (buttons.Length == 0)
        {
            _broker.Notice(caption, message);
            return DialogResult.Cancelled;
        }
        return _broker.Ask(caption, message, buttons) is { } choice ? new DialogResult(buttons[choice], choice) : DialogResult.Cancelled;
    }

    private static bool? NotSurfaced(string name)
    {
        Trace.WriteLine($"Dialog '{name}' isn't surfaced headless.");
        return null;
    }
}
