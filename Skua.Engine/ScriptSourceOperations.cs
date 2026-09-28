using Skua.Engine.Scripts;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;

namespace Skua.Engine;

/// <summary>
/// <c>scripts_search</c>, <c>scripts_list</c>, <c>scripts_update</c>, <c>scripts_new</c>, <c>scripts_source</c> and <c>scripts_source_set</c>:
/// finding Scripts in the Script Source, syncing them to disk, what the syncs brought, and which Script Source it is; and the Mac App's reset.
/// </summary>
internal sealed class ScriptSourceOperations
{
    private readonly IGetScriptsService _scriptsService;
    private readonly ScriptRuns _runs;
    private readonly ActionSlot _scriptsSlot;
    private readonly CancellationToken _shutdown;
    private readonly SemaphoreSlim _updating = new(1, 1);
    private readonly ScriptHistory _history = new();

    /// <param name="scriptsSlot">The Engine's Scripts slot, which a Script's compile takes too.</param>
    /// <param name="shutdown">Ends an update in flight; a client disconnecting doesn't.</param>
    public ScriptSourceOperations(IGetScriptsService scriptsService, ScriptRuns runs, ActionSlot scriptsSlot, CancellationToken shutdown)
    {
        _scriptsService = scriptsService;
        _runs = runs;
        _scriptsSlot = scriptsSlot;
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

        return new ScriptsSearchResult(source.ToDto(), matches.Count, matches.Take(ScriptsSearchResult.MaxScripts).Select(ToDto).ToList());
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
        return new ScriptsListResult(source.ToDto(), spelled, folders, direct);
    }

    public ScriptsNewResult New(string? since) => _history.New(_scriptsService.Source.ToDto(), since);

    /// <remarks>
    /// Refused while a Script runs, and holds the Engine's Scripts slot, so a Script's files never change under it and no Script compiles
    /// until the update ends; a login or another game action goes ahead meanwhile.
    /// </remarks>
    public Task<ScriptsUpdateResult> UpdateAsync() => SyncAsync("update the Scripts", reset: false);

    /// <summary>
    /// Deletes the Scripts folder's contents, the junk items list aside, then downloads every Script from the Script Source again, as the
    /// Windows Manager's Reset Scripts does; a Script edited or added on disk is gone. Refused as <see cref="UpdateAsync"/> is.
    /// </summary>
    public Task<ScriptsUpdateResult> ResetAsync() => SyncAsync("reset the Scripts", reset: true);

    private async Task<ScriptsUpdateResult> SyncAsync(string action, bool reset)
    {
        if (!_updating.Wait(0))
            throw RpcErrors.Of(ErrorCode.Busy, "A Scripts update is already running.");

        try
        {
            // The slot first: a Script's start holds it until its run has begun, so no start slips in after the check.
            using IDisposable lease = _scriptsSlot.Take(action);
            _runs.EnsureIdle(action);
            if (reset)
                DeleteLocalScripts();
            ScriptSource source = _scriptsService.Source;
            ScriptsSyncResult result = await FromScriptSourceAsync(source, () => _scriptsService.SyncScriptsAsync(_shutdown));
            if (result.Mode == ScriptsSyncMode.Full)
                await RecordFullDownloadAsync(result);
            else if (result.Added.Count > 0 || result.Changed.Count > 0)
                _history.Record(result.Source.ToDto(), result.Commit, full: false, Entries(result.Added), Entries(result.Changed));
            return new ScriptsUpdateResult(result.Source.ToDto(), Mode(result.Mode), result.Commit, result.Downloaded, result.Failed,
                result.Added.Select(s => s.FilePath).ToList(), result.Changed.Select(s => s.FilePath).ToList());
        }
        finally
        {
            _updating.Release();
        }
    }

    /// <summary>
    /// Forgets the last synced commit, so the next sync is a full download, and deletes everything in the Scripts folder but the junk items
    /// list, which is the Junk panel's own.
    /// </summary>
    /// <exception cref="IOException">A file couldn't be deleted; the next update is a full download all the same.</exception>
    private static void DeleteLocalScripts()
    {
        try
        {
            File.Delete(ClientFileSources.SkuaScriptsCommitFile);
            DirectoryInfo scripts = new(ClientFileSources.SkuaScriptsDIR);
            if (!scripts.Exists)
                return;
            foreach (FileSystemInfo entry in scripts.EnumerateFileSystemInfos())
            {
                if (entry.FullName == Path.GetFullPath(ClientFileSources.SkuaJunkItemsFile))
                    continue;
                if (entry is DirectoryInfo folder)
                    folder.Delete(recursive: true);
                else
                    entry.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Couldn't delete the Scripts in {ClientFileSources.SkuaScriptsDIR}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Records a full download with the Script Source's commits of the last <see cref="ScriptHistory.DefaultWindow"/>, since the download itself
    /// isn't news; after an earlier full download read them, only the commits since.
    /// </summary>
    private async Task RecordFullDownloadAsync(ScriptsSyncResult result)
    {
        ScriptSourceDto source = result.Source.ToDto();
        DateTimeOffset since = DateTimeOffset.UtcNow - ScriptHistory.DefaultWindow;
        if (_history.SourceHistoryReadAt(source) is { } readAt && readAt > since)
            since = readAt;
        SourceHistory sourceHistory = await ScriptSourceHistory.ReadAsync(result.Source, since, _shutdown);
        Dictionary<string, string?> names = result.Added.Concat(result.Changed).ToDictionary(s => s.FilePath, s => NullIfMissing(s.Name), StringComparer.Ordinal);
        _history.Record(source, result.Commit, full: true, [], [], sourceHistory, names);
    }

    /// <remarks>Reads the setting once, so the Script Source and whether it is the default always agree.</remarks>
    public ScriptSourceResult Source()
    {
        ScriptSourceDto? set = ScriptSourceSetting.Read(ClientFileSources.SkuaDIR);
        return new(set ?? ScriptSourceSetting.Default, IsDefault: set is null, ScriptSourceSetting.Default);
    }

    /// <param name="source"><c>owner/repo@branch</c>, or null for the default.</param>
    /// <remarks>
    /// Refused while a Script runs or an update is in flight, and holds the Engine's Scripts slot, so a Script never starts while the Script Source
    /// changes. The Engine reads the setting afresh at every call, so it takes effect without a restart.
    /// </remarks>
    public ScriptSourceResult SetSource(string? source)
    {
        if (!_updating.Wait(0))
            throw RpcErrors.Of(ErrorCode.Busy, "Can't change the Script Source while a Scripts update is running; try again when it's done.");

        try
        {
            using IDisposable lease = _scriptsSlot.Take("change the Script Source");
            _runs.EnsureIdle("change the Script Source");
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

    private static ScriptsUpdateMode Mode(ScriptsSyncMode mode) => mode switch
    {
        ScriptsSyncMode.Full => ScriptsUpdateMode.Full,
        ScriptsSyncMode.Incremental => ScriptsUpdateMode.Incremental,
        _ => ScriptsUpdateMode.UpToDate,
    };
}
