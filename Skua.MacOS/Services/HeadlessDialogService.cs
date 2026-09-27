using System.Diagnostics;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.MacOS.Services;

/// <summary>
/// Script Dialogs with nobody to show them to: nothing ever waits, and every Question gets the fallback answer.
/// </summary>
public sealed class HeadlessDialogService : IDialogService
{
    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => NotSurfaced(typeof(TViewModel).Name);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title) where TViewModel : class => NotSurfaced(Title);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class => NotSurfaced(typeof(TViewModel).Name);

    public void ShowMessageBox(string message, string caption) => Trace.WriteLine($"Notice '{caption}': {message}");

    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        Trace.WriteLine($"{(yesAndNo ? "Question" : "Notice")} '{caption}' got the fallback answer: {message}");
        return null;
    }

    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        Trace.WriteLine($"Question '{caption}' got the fallback answer: {message}");
        return DialogResult.Cancelled;
    }

    private static bool? NotSurfaced(string name)
    {
        Trace.WriteLine($"Dialog '{name}' isn't surfaced headless.");
        return null;
    }
}
