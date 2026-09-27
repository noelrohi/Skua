using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.GitHub;

namespace Skua.App.Engine;

/// <summary><c>scripts_search</c> and <c>scripts_update</c>: finding Scripts in the Script Source and syncing them to disk.</summary>
internal sealed class ScriptSourceOperations
{
    private readonly IGetScriptsService _scriptsService;
    private readonly CancellationToken _shutdown;
    private readonly SemaphoreSlim _updating = new(1, 1);

    /// <param name="shutdown">Ends an update in flight; a client disconnecting doesn't.</param>
    public ScriptSourceOperations(IGetScriptsService scriptsService, CancellationToken shutdown)
    {
        _scriptsService = scriptsService;
        _shutdown = shutdown;
    }

    /// <remarks>It fetches <c>scripts.json</c> every time, so <c>outdated</c> tells whether the Script Source has a newer version.</remarks>
    public async Task<ScriptsSearchResult> SearchAsync(string query, string? tag, CancellationToken cancellationToken)
    {
        ScriptSource source = _scriptsService.Source;
        List<ScriptInfo> scripts = await FromScriptSourceAsync(source, () => _scriptsService.FetchScriptsAsync(cancellationToken));

        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? wantedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        List<ScriptInfo> matches = scripts
            .Where(s => wantedTag is null || Tags(s).Contains(wantedTag, StringComparer.OrdinalIgnoreCase))
            .Where(s => terms.All(term => Matches(s, term)))
            .OrderBy(s => terms.All(term => Contains(s.FileName, term) || Contains(NullIfMissing(s.Name), term)) ? 0 : 1)
            .ThenBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScriptsSearchResult(ToDto(source), matches.Count, matches.Take(ScriptsSearchResult.MaxScripts).Select(ToDto).ToList());
    }

    public async Task<ScriptsUpdateResult> UpdateAsync()
    {
        if (!_updating.Wait(0))
            throw RpcErrors.Of(ErrorCode.Busy, "A Scripts update is already running.");

        try
        {
            ScriptSource source = _scriptsService.Source;
            ScriptsSyncResult result = await FromScriptSourceAsync(source, () => _scriptsService.SyncScriptsAsync(_shutdown));
            return new ScriptsUpdateResult(ToDto(result.Source), Mode(result.Mode), result.Commit, result.Downloaded, result.Failed);
        }
        finally
        {
            _updating.Release();
        }
    }

    /// <summary>Runs a call that reaches the Script Source, reporting a network or format failure as <see cref="ErrorCode.ScriptSourceUnavailable"/>.</summary>
    private async Task<T> FromScriptSourceAsync<T>(ScriptSource source, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidDataException or Newtonsoft.Json.JsonException
                                      || (e is TaskCanceledException && !_shutdown.IsCancellationRequested))
        {
            throw RpcErrors.Of(ErrorCode.ScriptSourceUnavailable, $"The Script Source {source} couldn't be read: {e.Message}");
        }
    }

    private static bool Matches(ScriptInfo script, string term) =>
        Contains(script.FilePath, term) || Contains(NullIfMissing(script.Name), term) || Contains(NullIfMissing(script.Description), term)
        || Tags(script).Any(tag => Contains(tag, term));

    private static bool Contains(string? text, string term) => text?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary><c>scripts.json</c> writes a missing name, description or tag as the string "null".</summary>
    private static string? NullIfMissing(string? text) => string.IsNullOrWhiteSpace(text) || text == "null" ? null : text;

    private static IReadOnlyList<string> Tags(ScriptInfo script) =>
        (script.Tags ?? []).Select(NullIfMissing).OfType<string>().ToList();

    private static ScriptDto ToDto(ScriptInfo script) =>
        new(script.FilePath, NullIfMissing(script.Name), NullIfMissing(script.Description), Tags(script), script.Downloaded, script.Outdated);

    private static ScriptSourceDto ToDto(ScriptSource source) => new(source.Owner, source.Repo, source.Branch);

    private static ScriptsUpdateMode Mode(ScriptsSyncMode mode) => mode switch
    {
        ScriptsSyncMode.Full => ScriptsUpdateMode.Full,
        ScriptsSyncMode.Incremental => ScriptsUpdateMode.Incremental,
        _ => ScriptsUpdateMode.UpToDate,
    };
}
