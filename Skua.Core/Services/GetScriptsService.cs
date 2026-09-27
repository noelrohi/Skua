using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.GitHub;
using Skua.Core.Utils;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Skua.Core.Services;

public partial class GetScriptsService : ObservableObject, IGetScriptsService
{
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;

    private const string _skillsSetsPath = "Skills/AdvancedSkills.json";
    private const string _questDataPath = "QuestData.json";
    private const string _junkItemsPath = "JunkItems.json";
    private const int _compareFileLimit = 300;

    [ObservableProperty]
    private RangedObservableCollection<ScriptInfo> _scripts = new();

    private readonly ScriptSource _defaultSource;

    public GetScriptsService(IDialogService dialogService, ISettingsService settingsService)
        : this(dialogService, settingsService, new ScriptSource())
    {
    }

    /// <param name="defaultSource">The Script Source used while the setting is unset.</param>
    public GetScriptsService(IDialogService dialogService, ISettingsService settingsService, ScriptSource defaultSource)
    {
        _dialogService = dialogService;
        _settingsService = settingsService;
        _defaultSource = defaultSource;
    }

    public ScriptSource Source => _settingsService.GetShared().ScriptSource ?? _defaultSource;

    public async ValueTask<List<ScriptInfo>> GetScriptsAsync(IProgress<string>? progress, CancellationToken token)
    {
        if (_scripts.Count > 0)
            return _scripts.ToList();

        await GetScripts(progress, false, token);
        return _scripts.ToList();
    }

    public Task RefreshScriptsAsync(IProgress<string>? progress, CancellationToken token)
        => GetScripts(progress, true, token);

    private async Task GetScripts(IProgress<string>? progress, bool refresh, CancellationToken token)
    {
        try
        {
            Scripts.Clear();

            progress?.Report("Fetching scripts...");
            List<ScriptInfo> scripts = await GetScriptsInfo(refresh, token);

            progress?.Report($"Found {scripts.Count} scripts.");

            _scripts.AddRange(scripts);

            progress?.Report($"Fetched {scripts.Count} scripts.");
            OnPropertyChanged(nameof(Scripts));
        }
        catch (TaskCanceledException)
        {
            progress?.Report("Task cancelled.");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            _dialogService.ShowMessageBox(
                "Unable to connect to GitHub.\r\nCheck your connection and try again.",
                "Network Error");
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox(
                $"Failed to retrieve scripts.\r\n{ex.Message}",
                "Search Scripts Error");
        }
    }

    private async Task<List<ScriptInfo>> GetScriptsInfo(bool refresh, CancellationToken token)
    {
        if (_scripts.Count != 0 && !refresh)
            return _scripts.ToList();

        return await FetchScriptsAsync(Source, token);
    }

    public Task<List<ScriptInfo>> FetchScriptsAsync(CancellationToken token)
        => FetchScriptsAsync(Source, token);

    private static async Task<List<ScriptInfo>> FetchScriptsAsync(ScriptSource source, CancellationToken token)
    {
        using HttpResponseMessage response =
            await ValidatedHttpExtensions.GetAsync(HttpClients.GitHubRaw, source.RawFileUrl("scripts.json"), token);

        string content = await response.Content.ReadAsStringAsync(token);
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("scripts.json is empty.");

        List<ScriptInfo>? scripts =
            JsonConvert.DeserializeObject<List<ScriptInfo>>(content);

        if (scripts is null || scripts.Count == 0)
            throw new InvalidDataException("scripts.json contains no valid scripts.");

        return scripts;
    }

    public Task DownloadScriptAsync(ScriptInfo info)
        => DownloadScriptAsync(Source, info, CancellationToken.None);

    /// <remarks>
    /// The file comes from the Script Source, not from the entry's <c>downloadUrl</c>: a fork's <c>scripts.json</c> still points at upstream.
    /// </remarks>
    private static async Task DownloadScriptAsync(ScriptSource source, ScriptInfo info, CancellationToken token)
    {
        string? directory = Path.GetDirectoryName(info.LocalFile);

        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        using HttpResponseMessage response =
            await ValidatedHttpExtensions.GetAsync(HttpClients.GitHubRaw, source.RawFileUrl(info.FilePath), token);

        byte[] scriptBytes = await response.Content.ReadAsByteArrayAsync(token);
        await File.WriteAllBytesAsync(info.LocalFile, scriptBytes, token);
    }

    public async Task<int> DownloadAllWhereAsync(Func<ScriptInfo, bool> pred)
    {
        List<ScriptInfo> toUpdate = _scripts.Where(pred).ToList();

        await Parallel.ForEachAsync(toUpdate, async (script, _) =>
        {
            await DownloadScriptAsync(script);
        });

        if (toUpdate.Count > 0)
            ClearCachedScriptsDirectory();

        return toUpdate.Count;
    }

    private static void ClearCachedScriptsDirectory()
    {
        string path = Path.Combine(ClientFileSources.SkuaScriptsDIR, "Cached-Scripts");

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch { }
    }

    public Task DeleteScriptAsync(ScriptInfo info)
    {
        try
        {
            if (File.Exists(info.LocalFile))
                File.Delete(info.LocalFile);
        }
        catch { }

        return Task.CompletedTask;
    }

    public async Task<long> CheckAdvanceSkillSetsUpdates()
    {
        try
        {
            long localSize = File.Exists(ClientFileSources.SkuaAdvancedSkillsFile)
                ? new FileInfo(ClientFileSources.SkuaAdvancedSkillsFile).Length
                : 0;

            string content =
                await ValidatedHttpExtensions.GetStringAsync(HttpClients.GitHubRaw, Source.RawFileUrl(_skillsSetsPath));

            long remoteSize = content.Length;

            return remoteSize != localSize ? remoteSize : 0;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<bool> UpdateSkillSetsFile()
    {
        try
        {
            string content =
                await ValidatedHttpExtensions.GetStringAsync(HttpClients.GitHubRaw, Source.RawFileUrl(_skillsSetsPath));

            await File.WriteAllTextAsync(ClientFileSources.SkuaAdvancedSkillsFile, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> UpdateQuestDataFile()
    {
        try
        {
            string content =
                await ValidatedHttpExtensions.GetStringAsync(HttpClients.GitHubRaw, Source.RawFileUrl(_questDataPath));

            await File.WriteAllTextAsync(ClientFileSources.SkuaQuestsFile, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<long> CheckJunkItemsUpdates()
    {
        try
        {
            long localSize = File.Exists(ClientFileSources.SkuaJunkItemsFile)
                ? new FileInfo(ClientFileSources.SkuaJunkItemsFile).Length
                : 0;

            string content =
                await ValidatedHttpExtensions.GetStringAsync(HttpClients.GitHubRaw, Source.RawFileUrl(_junkItemsPath));

            long remoteSize = content.Length;

            return remoteSize != localSize ? remoteSize : 0;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<bool> UpdateJunkItemsFile()
    {
        try
        {
            string content =
                await ValidatedHttpExtensions.GetStringAsync(HttpClients.GitHubRaw, Source.RawFileUrl(_junkItemsPath));

            await File.WriteAllTextAsync(ClientFileSources.SkuaJunkItemsFile, content);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> GetLastCommitShaAsync(CancellationToken token)
    {
        try
        {
            return await FetchHeadCommitShaAsync(Source, token);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> FetchHeadCommitShaAsync(ScriptSource source, CancellationToken token)
    {
        using HttpResponseMessage response =
            await HttpClients.MakeGitHubApiRequestAsync(source.CommitUrl);

        string content = await response.Content.ReadAsStringAsync(token);

        GitHubCommit? commit =
            JsonConvert.DeserializeObject<GitHubCommit>(content);

        return string.IsNullOrEmpty(commit?.Sha)
            ? throw new InvalidDataException($"GitHub returned no commit for {source}.")
            : commit.Sha;
    }

    private async Task<HashSet<string>> GetChangedFilesAsync(string oldSha, string newSha, CancellationToken token)
    {
        try
        {
            return await FetchChangedFilesAsync(Source, oldSha, newSha, token);
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox($"Error getting changed files: {ex.Message}", "Debug Info");
            return new HashSet<string>();
        }
    }

    private static async Task<HashSet<string>> FetchChangedFilesAsync(ScriptSource source, string oldSha, string newSha, CancellationToken token)
    {
        using HttpResponseMessage response =
            await HttpClients.MakeGitHubApiRequestAsync(source.CompareUrl(oldSha, newSha));

        string content = await response.Content.ReadAsStringAsync(token);

        GitHubCompare? compare =
            JsonConvert.DeserializeObject<GitHubCompare>(content);

        return compare?.Files?
            .Where(f => f.Status != "removed")
            .Select(f => f.FileName)
            .ToHashSet() ?? new HashSet<string>();
    }

    /// <summary>The last commit synced from <paramref name="source"/>, or null when the Scripts were last synced from another Script Source.</summary>
    private static string? GetStoredCommitSha(ScriptSource source)
    {
        try
        {
            string storedSource = File.Exists(ClientFileSources.SkuaScriptsSourceFile)
                ? File.ReadAllText(ClientFileSources.SkuaScriptsSourceFile).Trim()
                : new ScriptSource().ToString();
            if (storedSource != source.ToString())
                return null;

            return File.Exists(ClientFileSources.SkuaScriptsCommitFile)
                ? File.ReadAllText(ClientFileSources.SkuaScriptsCommitFile).Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task StoreCommitShaAsync(ScriptSource source, string sha)
    {
        try
        {
            await File.WriteAllTextAsync(ClientFileSources.SkuaScriptsCommitFile, sha);
            await File.WriteAllTextAsync(ClientFileSources.SkuaScriptsSourceFile, source.ToString());
        }
        catch
        {
        }
    }

    public async Task<ScriptsSyncResult> SyncScriptsAsync(CancellationToken token)
    {
        ScriptSource source = Source;
        string headSha = await FetchHeadCommitShaAsync(source, token);
        string? storedSha = GetStoredCommitSha(source);

        if (storedSha == headSha)
            return new ScriptsSyncResult(source, ScriptsSyncMode.UpToDate, headSha, 0, [], [], []);

        List<ScriptInfo> scripts = await FetchScriptsAsync(source, token);
        List<ScriptInfo> toDownload;
        if (string.IsNullOrEmpty(storedSha))
        {
            toDownload = scripts.Where(s => !s.Downloaded || s.Outdated).ToList();
        }
        else
        {
            HashSet<string> changedFiles = await FetchChangedFilesAsync(source, storedSha, headSha, token);
            // GitHub's compare lists at most 300 files; past that, every Script that differs from scripts.json is fetched instead.
            toDownload = changedFiles.Count >= _compareFileLimit
                ? scripts.Where(s => !s.Downloaded || s.Outdated).ToList()
                : scripts.Where(s => changedFiles.Contains(s.FilePath)).ToList();
        }

        ConcurrentBag<string> failed = new();
        ConcurrentBag<ScriptInfo> added = new();
        ConcurrentBag<ScriptInfo> changed = new();
        int downloaded = 0;
        await Parallel.ForEachAsync(toDownload, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (script, ct) =>
        {
            try
            {
                bool existed = script.Downloaded;
                await DownloadScriptAsync(source, script, ct);
                Interlocked.Increment(ref downloaded);
                (existed ? changed : added).Add(script);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failed.Add(script.FilePath);
            }
        });

        if (downloaded > 0)
            ClearCachedScriptsDirectory();

        if (failed.IsEmpty)
            await StoreCommitShaAsync(source, headSha);

        return new ScriptsSyncResult(
            source,
            string.IsNullOrEmpty(storedSha) ? ScriptsSyncMode.Full : ScriptsSyncMode.Incremental,
            headSha,
            downloaded,
            failed.Order(StringComparer.Ordinal).ToList(),
            added.OrderBy(s => s.FilePath, StringComparer.Ordinal).ToList(),
            changed.OrderBy(s => s.FilePath, StringComparer.Ordinal).ToList());
    }

    public IEnumerable<ScriptInfo> GetOutdatedScripts()
        => _scripts.Where(s => s.Outdated).ToList();

    public async Task<int> IncrementalUpdateScriptsAsync(IProgress<string>? progress, CancellationToken token)
    {
        try
        {
            progress?.Report("Checking for updates...");

            string? currentSha = await GetLastCommitShaAsync(token);

            if (string.IsNullOrEmpty(currentSha))
            {
                progress?.Report("Full refresh required.");
                await RefreshScriptsAsync(progress, token);
                return 0;
            }

            string? storedSha = GetStoredCommitSha(Source);

            if (string.IsNullOrEmpty(storedSha))
            {
                progress?.Report("Initial sync...");
                await RefreshScriptsAsync(progress, token);
                await StoreCommitShaAsync(Source, currentSha);
                return _scripts.Count;
            }

            if (storedSha == currentSha)
            {
                progress?.Report("Already up to date.");
                return 0;
            }

            progress?.Report("Checking changes...");
            HashSet<string> changedFiles = await GetChangedFilesAsync(storedSha, currentSha, token);

            HashSet<string> scriptChanges = changedFiles
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && f != "scripts.json")
                .ToHashSet();

            if (scriptChanges.Count == 0)
            {
                progress?.Report("No script changes detected.");
                await StoreCommitShaAsync(Source, currentSha);
                return 0;
            }

            List<ScriptInfo> scripts = await GetScriptsInfo(true, token);

            List<ScriptInfo> toUpdate = scripts
                .Where(s => scriptChanges.Contains(s.FilePath))
                .ToList();

            int updated = 0;

            foreach (ScriptInfo script in toUpdate)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    await DownloadScriptAsync(script);
                    updated++;
                    progress?.Report($"Updated {updated}/{toUpdate.Count}: {script.Name}");
                }
                catch (Exception ex)
                {
                    progress?.Report($"Failed: {script.Name} - {ex.Message}");
                }
            }

            await StoreCommitShaAsync(Source, currentSha);

            if (updated > 0)
                ClearCachedScriptsDirectory();

            progress?.Report($"Done. {updated} scripts updated.");
            return updated;
        }
        catch (TaskCanceledException)
        {
            progress?.Report("Update cancelled.");
            return 0;
        }
        catch (Exception ex)
        {
            _dialogService.ShowMessageBox(
                $"Incremental update failed:\r\n{ex.Message}\r\nFalling back to full refresh.",
                "Update Error");

            await RefreshScriptsAsync(progress, token);
            return 0;
        }
    }
}