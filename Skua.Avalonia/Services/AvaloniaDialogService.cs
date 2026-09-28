using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.MacOS.Services;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's dialogs in the Mac App. Message boxes are Script Dialogs of the Engine's one <see cref="ScriptDialogBroker"/>, as headless, so the
/// window, <c>dialog_answer</c> and <c>skua script start --follow</c> can all answer a Question and the first answer wins; the window shows
/// them through <see cref="ScriptDialogsViewModel"/>. <c>ShowDialog</c> shows the view model's view in a real dialog.
/// </summary>
/// <remarks>
/// A Question raised on the UI thread, such as the Scripts panel's "stop the running Script?", never blocks it on the broker: the thread keeps
/// running the app in a nested frame until the Question is answered, so the window stays live and can answer it. Such a Question belongs to
/// no run, whatever runs, and waits <see cref="ScriptDialogBroker.OutsideRun"/>'s timeout. Any other thread waits as headless.
/// </remarks>
public sealed class AvaloniaDialogService : IDialogService
{
    private static readonly string[] YesNo = ["Yes", "No"];

    private readonly ScriptDialogBroker _broker;

    /// <summary>Cancelled as the app exits, so a Question the UI thread waits on gets the fallback and its nested frame ends.</summary>
    private readonly CancellationTokenSource _exiting = new();

    private bool _watchingExit;

    public AvaloniaDialogService(ScriptDialogBroker broker)
    {
        _broker = broker;
    }

    /// <summary>Runs on each dialog this service makes, before it shows; the app gives each its native menu.</summary>
    public Action<Window>? WindowCreated { get; set; }

    public bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class => Show(viewModel, null, null);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, string Title) where TViewModel : class => Show(viewModel, Title, null);

    public bool? ShowDialog<TViewModel>(TViewModel viewModel, Action<TViewModel> callback) where TViewModel : class =>
        Show(viewModel, null, () => callback(viewModel));

    public void ShowMessageBox(string message, string caption) => _broker.Notice(caption, message);

    /// <returns>Null for a Notice, and for a Question that got the fallback; else whether the answer was Yes.</returns>
    public bool? ShowMessageBox(string message, string caption, bool yesAndNo)
    {
        if (!yesAndNo)
        {
            _broker.Notice(caption, message);
            return null;
        }
        return Ask(caption, message, YesNo) is { } choice ? choice == 0 : null;
    }

    /// <returns>The chosen button, or <see cref="DialogResult.Cancelled"/> for the fallback and for a message box without buttons, which is a Notice.</returns>
    public DialogResult ShowMessageBox(string message, string caption, params string[] buttons)
    {
        if (buttons.Length == 0)
        {
            _broker.Notice(caption, message);
            return DialogResult.Cancelled;
        }
        return Ask(caption, message, buttons) is { } choice ? new DialogResult(buttons[choice], choice) : DialogResult.Cancelled;
    }

    /// <summary>
    /// The app's own yes/no Question, raised from any thread without blocking it, such as a start-up check's. It belongs to no run, whatever
    /// runs, and waits <see cref="ScriptDialogBroker.OutsideRun"/>'s timeout.
    /// </summary>
    /// <returns>Null for the fallback; else whether the answer was Yes.</returns>
    public async Task<bool?> AskAsync(string message, string caption)
    {
        Dispatcher.UIThread.Post(WatchExit);
        return await _broker.AskAsync(caption, message, YesNo, ScriptDialogBroker.OutsideRun, _exiting.Token) is { } choice ? choice == 0 : null;
    }

    /// <summary>The app's own Notice, which comes from no Script, whatever runs.</summary>
    public void Notify(string message, string caption) => _broker.Notice(caption, message, ScriptDialogBroker.OutsideRun);

    private int? Ask(string caption, string text, IReadOnlyList<string> choices)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return _broker.Ask(caption, text, choices);
        WatchExit();
        return UiThread.Wait(() => _broker.AskAsync(caption, text, choices, ScriptDialogBroker.OutsideRun, _exiting.Token));
    }

    /// <summary>
    /// Shows the view model's view in a dialog over the active window and waits until it closes; returns what the view closed it with
    /// (<see cref="DialogWindow.Close(global::Avalonia.Controls.Control, bool?)"/>), or null. A view model without a view isn't shown, as headless.
    /// </summary>
    private bool? Show(object viewModel, string? title, Action? closed)
    {
        if (!ViewLocator.HasView(viewModel))
        {
            Trace.WriteLine($"Dialog '{title ?? viewModel.GetType().Name}' has no view in the Mac App yet.");
            return null;
        }
        return UiThread.Wait(async () =>
        {
            DialogWindow dialog = new(viewModel, title);
            WindowCreated?.Invoke(dialog);
            bool? result;
            if (Windows.Active() is { IsVisible: true } owner)
            {
                result = await dialog.ShowDialog<bool?>(owner);
            }
            else
            {
                // No window to be modal to, e.g. with the main window closed: shown on its own.
                TaskCompletionSource<bool?> done = new();
                dialog.Closed += (_, _) => done.TrySetResult(dialog.Result);
                dialog.Show();
                result = await done.Task;
            }
            // As on Windows, the callback runs as the dialog closes, however it closed.
            closed?.Invoke();
            return result;
        });
    }

    private void WatchExit()
    {
        if (_watchingExit)
            return;
        _watchingExit = true;
        if (Application.Current?.ApplicationLifetime is IControlledApplicationLifetime lifetime)
            lifetime.Exit += (_, _) => _exiting.Cancel();
        Dispatcher.UIThread.ShutdownStarted += (_, _) => _exiting.Cancel();
    }
}
