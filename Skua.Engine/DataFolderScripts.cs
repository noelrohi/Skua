using Skua.Control;
using Skua.Core.Services;
using Skua.MacOS.Services;
using StreamJsonRpc;

namespace Skua.Engine;

/// <summary>
/// The Script Source operations on the data folder, run in the caller's process with no Engine, so <c>skua scripts</c> starts none. They are
/// the Engine's own operations; with no Engine's Scripts slot to guard them, the caller checks that no Engine of the data folder runs a Script
/// before it updates the Scripts or changes the Script Source.
/// </summary>
public sealed class DataFolderScripts
{
    private readonly ScriptSourceOperations _operations;

    /// <param name="cancellationToken">Ends an update in flight.</param>
    public DataFolderScripts(CancellationToken cancellationToken)
    {
        GetScriptsService scripts = new(new HeadlessDialogService(new ScriptDialogBroker()), new EngineSettingsService(), ScriptSourceSetting.Default.ToCore());
        _operations = new ScriptSourceOperations(scripts, _ => { }, new ActionSlot(), cancellationToken);
    }

    /// <exception cref="ControlException">The Script Source couldn't be read.</exception>
    public Task<ScriptsSearchResult> SearchAsync(string query, string? tag, CancellationToken cancellationToken) =>
        CallAsync(() => _operations.SearchAsync(query, tag, cancellationToken));

    /// <exception cref="ControlException">The Script Source couldn't be read, or has no such folder.</exception>
    public Task<ScriptsListResult> ListAsync(string? folder, CancellationToken cancellationToken) =>
        CallAsync(() => _operations.ListAsync(folder, cancellationToken));

    /// <inheritdoc cref="ScriptSourceOperations.UpdateAsync"/>
    /// <exception cref="ControlException">Another update in this process runs, or the Script Source couldn't be read.</exception>
    public Task<ScriptsUpdateResult> UpdateAsync(bool verify) => CallAsync(() => _operations.UpdateAsync(verify));

    /// <exception cref="ControlException"><paramref name="since"/> is neither a date nor a recorded commit.</exception>
    public Task<ScriptsNewResult> NewAsync(string? since) => CallAsync(() => Task.FromResult(_operations.New(since)));

    public ScriptSourceResult Source() => _operations.Source();

    /// <param name="source"><c>owner/repo@branch</c>, or null for the default.</param>
    /// <exception cref="ControlException">The Script Source isn't <c>owner/repo@branch</c>.</exception>
    public Task<ScriptSourceResult> SetSourceAsync(string? source) => CallAsync(() => Task.FromResult(_operations.SetSource(source)));

    /// <summary>Runs an operation, turning its failure into the one a Control Surface sees over the socket.</summary>
    private static async Task<T> CallAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (LocalRpcException e)
        {
            throw RpcErrors.ToControlException(e);
        }
    }
}
