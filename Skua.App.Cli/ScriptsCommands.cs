using Skua.Control;
using Skua.Engine;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua scripts</c>: the Script Source operations on the data folder, which need no Engine, so they run in this process and start none.
/// </summary>
internal static class ScriptsCommands
{
    public static Task<ScriptsSearchResult> SearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        new DataFolderScripts(cancellationToken).SearchAsync(query, tag, cancellationToken);

    public static Task<ScriptsListResult> ListAsync(string? folder, CancellationToken cancellationToken) =>
        new DataFolderScripts(cancellationToken).ListAsync(folder, cancellationToken);

    public static async Task<ScriptsUpdateResult> UpdateAsync(bool verify, CancellationToken cancellationToken)
    {
        await EnsureNoScriptRunsAsync("update the Scripts", cancellationToken);
        return await new DataFolderScripts(cancellationToken).UpdateAsync(verify);
    }

    public static Task<ScriptsNewResult> NewAsync(string? since) => new DataFolderScripts(CancellationToken.None).NewAsync(since);

    public static ScriptSourceResult Source() => new DataFolderScripts(CancellationToken.None).Source();

    public static async Task<ScriptSourceResult> SetSourceAsync(string? source, CancellationToken cancellationToken)
    {
        await EnsureNoScriptRunsAsync("change the Script Source", cancellationToken);
        return await new DataFolderScripts(cancellationToken).SetSourceAsync(source);
    }

    /// <summary>
    /// Refuses <paramref name="action"/> while an Engine of the data folder runs a Script, since they all share its Scripts. An Engine whose
    /// status can't be read in time is let be.
    /// </summary>
    private static async Task EnsureNoScriptRunsAsync(string action, CancellationToken cancellationToken)
    {
        foreach (EngineListEntry entry in await EngineCommands.ListAsync(cancellationToken))
        {
            if (entry.Status?.Script is { State: not ScriptState.Idle } script)
            {
                string name = entry.Engine.Name;
                throw new ControlException(ErrorCode.ScriptRunning,
                    $"Can't {action} while Engine '{name}' runs {script.Run?.Script ?? "a Script"}; stop it first with '{Cli.Command(name, "script stop")}'.");
            }
        }
    }
}
