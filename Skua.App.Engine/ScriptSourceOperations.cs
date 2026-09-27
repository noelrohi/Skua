using Skua.App.Engine.Scripts;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;

namespace Skua.App.Engine;

/// <summary>
/// <c>scripts_search</c>, <c>scripts_list</c>, <c>scripts_update</c>, <c>scripts_new</c>, <c>scripts_source</c> and <c>scripts_source_set</c>:
/// finding Scripts in the Script Source, syncing them to disk, what the syncs brought, and which Script Source it is.
/// </summary>
internal sealed class ScriptSourceOperations
{
    private readonly IGetScriptsService _scriptsService;
    private readonly ScriptRuns _runs;
    private readonly ActionSlot _slot;
    private readonly CancellationToken _shutdown;
    private readonly SemaphoreSlim _updating = new(1, 1);
    private readonly ScriptHistory _history = new();

    /// <param name="shutdown">Ends an update in flight; a client disconnecting doesn't.</param>
    public ScriptSourceOperations(IGetScriptsService scriptsService, ScriptRuns runs, ActionSlot slot, CancellationToken shutdown)
    {
        _scriptsService = scriptsService;
        _runs = runs;
        _slot = slot;
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

    public async Task<ScriptsListResult> ListAsync(string? folder, CancellationToken cancellationToken)
    {
        ScriptSource source = _scriptsService.Source;
        List<ScriptInfo> scripts = await FromScriptSourceAsync(source, () => _scriptsService.FetchScriptsAsync(cancellationToken));

        string wanted = (folder ?? "").Trim().Trim('/');
        string prefix = wanted.Length == 0 ? "" : wanted + "/";
        List<ScriptInfo> inFolder = scripts.Where(s => s.FilePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inFolder.Count == 0)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"The Script Source {source} has no Scripts in a folder '{wanted}'; 'skua scripts list' shows its folders.");

        // The folder as the Script Source spells it.
        string spelled = wanted.Length == 0 ? "" : inFolder[0].FilePath[..wanted.Length];
        List<ScriptFolderDto> folders = inFolder
            .Select(s => s.FilePath[prefix.Length..])
            .Where(rest => rest.Contains('/'))
            .GroupBy(rest => rest[..rest.IndexOf('/')], StringComparer.OrdinalIgnoreCase)
            .Select(g => new ScriptFolderDto(prefix.Length == 0 ? g.Key : $"{spelled}/{g.Key}", g.Count()))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<ScriptDto> direct = inFolder
            .Where(s => !s.FilePath[prefix.Length..].Contains('/'))
            .OrderBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
        return new ScriptsListResult(ToDto(source), spelled, folders, direct);
    }

    public ScriptsNewResult New(string? since) => _history.New(ToDto(_scriptsService.Source), since);

    /// <remarks>Refused while a Script runs, and holds the Engine's slot, so a Script's files never change under it.</remarks>
    public async Task<ScriptsUpdateResult> UpdateAsync()
    {
        if (!_updating.Wait(0))
            throw RpcErrors.Of(ErrorCode.Busy, "A Scripts update is already running.");

        try
        {
            _runs.EnsureIdle("update the Scripts");
            using IDisposable lease = _slot.Take("update the Scripts");
            ScriptSource source = _scriptsService.Source;
            ScriptsSyncResult result = await FromScriptSourceAsync(source, () => _scriptsService.SyncScriptsAsync(_shutdown));
            if (result.Added.Count > 0 || result.Changed.Count > 0)
                _history.Record(ToDto(result.Source), result.Commit, result.Mode == ScriptsSyncMode.Full, Entries(result.Added), Entries(result.Changed));
            return new ScriptsUpdateResult(ToDto(result.Source), Mode(result.Mode), result.Commit, result.Downloaded, result.Failed,
                result.Added.Select(s => s.FilePath).ToList(), result.Changed.Select(s => s.FilePath).ToList());
        }
        finally
        {
            _updating.Release();
        }
    }

    public ScriptSourceResult Source() =>
        new(ToDto(_scriptsService.Source), ScriptSourceSetting.Read(ClientFileSources.SkuaDIR) is null, ScriptSourceSetting.Default);

    /// <param name="source"><c>owner/repo@branch</c>, or null for the default.</param>
    /// <remarks>
    /// Refused while a Script runs or an update is in flight, and holds the Engine's slot, so a Script never starts while the Script Source
    /// changes. The Engine reads the setting afresh at every call, so it takes effect without a restart.
    /// </remarks>
    public ScriptSourceResult SetSource(string? source)
    {
        if (!_updating.Wait(0))
            throw RpcErrors.Of(ErrorCode.Busy, "Can't change the Script Source while a Scripts update is running; try again when it's done.");

        try
        {
            _runs.EnsureIdle("change the Script Source");
            using IDisposable lease = _slot.Take("change the Script Source");
            try
            {
                ScriptSourceSetting.Write(ClientFileSources.SkuaDIR, source is null ? null : ScriptSourceSetting.Parse(source));
            }
            catch (ControlException e)
            {
                throw RpcErrors.Of(e.Code, e.Message);
            }
            return Source();
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

    private static List<ScriptHistory.ScriptEntry> Entries(IReadOnlyList<ScriptInfo> scripts) =>
        scripts.Select(s => new ScriptHistory.ScriptEntry(s.FilePath, NullIfMissing(s.Name))).ToList();

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
