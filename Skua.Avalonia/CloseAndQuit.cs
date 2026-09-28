using Avalonia.Controls;
using Skua.Control;

namespace Skua.Avalonia;

/// <summary>
/// The Mac App's close and quit rules (ADR 0006). Closing the main window only hides it: the app and its Engine keep playing, headless,
/// and the Dock icon shows it again. Quitting stops the Engine for every Control Surface, so it asks first while a Script runs.
/// </summary>
public sealed class CloseAndQuit
{
    private readonly Window _window;
    private readonly IEngineRpc _engine;
    private readonly string _engineName;
    private readonly Action _shutdown;
    private bool _asking;

    /// <param name="window">The main window, which closing hides.</param>
    /// <param name="engine">The Engine the app hosts.</param>
    /// <param name="engineName">The Engine Name it serves.</param>
    /// <param name="shutdown">Ends the app's lifetime; the host then stops the Engine.</param>
    public CloseAndQuit(Window window, IEngineRpc engine, string engineName, Action shutdown)
    {
        _window = window;
        _engine = engine;
        _engineName = engineName;
        _shutdown = shutdown;
        window.Closing += OnClosing;
    }

    /// <summary>Whether the app is quitting: it no longer asks, and lets the main window close.</summary>
    public bool IsQuitting { get; private set; }

    /// <summary>The question on screen while a quit waits for an answer, or null.</summary>
    public ConfirmDialog? Pending { get; private set; }

    /// <summary>Shows the main window again, as clicking the Dock icon does.</summary>
    public void Reopen()
    {
        _window.Show();
        _window.Activate();
    }

    /// <summary>
    /// Quits the app. With <paramref name="ask"/> (Cmd-Q), a running Script makes it ask first, over the main window; without it (SIGTERM),
    /// it quits at once, even while asking.
    /// </summary>
    /// <returns>Whether it quit; false when the developer chose to keep playing.</returns>
    public async Task<bool> QuitAsync(bool ask)
    {
        if (IsQuitting)
            return true;
        if (ask)
        {
            if (_asking)
                return false;
            _asking = true;
            try
            {
                if (await QuestionAsync() is { } question)
                {
                    Reopen();
                    Pending = new ConfirmDialog("Quit Skua?", question, "Quit");
                    bool quit = await Pending.ShowDialog<bool>(_window);
                    Pending = null;
                    // A SIGTERM may have quit in the meantime.
                    if (!quit || IsQuitting)
                        return IsQuitting;
                }
            }
            finally
            {
                _asking = false;
            }
        }
        IsQuitting = true;
        Pending?.Close(false);
        _shutdown();
        return true;
    }

    /// <summary>What to ask before quitting, or null when nothing runs that quitting would stop.</summary>
    private async Task<string?> QuestionAsync()
    {
        ScriptStatusDto status;
        try
        {
            status = await Task.Run(() => _engine.ScriptStatusAsync(CancellationToken.None));
        }
        catch (Exception)
        {
            return null;
        }
        if (status.State is not (ScriptState.Compiling or ScriptState.Running))
            return null;
        string script = status.Run?.Script is { } name ? $"The Script {name}" : "A Script";
        return $"{script} is running. Quitting stops it, closes the game and stops Engine '{_engineName}', for skua and MCP too.";
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (IsQuitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;
        e.Cancel = true;
        _window.Hide();
    }
}
