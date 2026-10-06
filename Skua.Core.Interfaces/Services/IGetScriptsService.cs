using Skua.Core.Models.GitHub;
using Skua.Core.Utils;
using System.ComponentModel;

namespace Skua.Core.Interfaces;

public interface IGetScriptsService : INotifyPropertyChanged
{
    int Downloaded => Scripts.Count(s => s.Downloaded);
    int Outdated => Scripts.Count(s => s.Outdated);
    int Total => Scripts.Count;
    int Missing => Total - Downloaded;
    RangedObservableCollection<ScriptInfo> Scripts { get; }

    /// <summary>The Script Source that Scripts and their data files come from: the setting, or the app's default while it is unset.</summary>
    ScriptSource Source { get; }

    ValueTask<List<ScriptInfo>> GetScriptsAsync(IProgress<string>? progress, CancellationToken token);

    Task RefreshScriptsAsync(IProgress<string>? progress, CancellationToken token);

    Task<int> IncrementalUpdateScriptsAsync(IProgress<string>? progress, CancellationToken token);

    /// <summary>
    /// Fetches <c>scripts.json</c> from the Script Source. Unlike <see cref="GetScriptsAsync"/>, it throws on failure and leaves <see cref="Scripts"/> alone.
    /// </summary>
    Task<List<ScriptInfo>> FetchScriptsAsync(CancellationToken token);

    /// <summary>
    /// Syncs the Scripts on disk with the Script Source: the first sync from a Script Source downloads every missing or outdated Script,
    /// later ones only the Scripts changed since the last synced commit. Throws on failure and leaves <see cref="Scripts"/> alone.
    /// </summary>
    /// <param name="verify">
    /// Instead of the changes since the last synced commit, download every Script whose file is missing or differs in size or SHA-256 from
    /// <c>scripts.json</c>, whatever commit was synced last; a Script edited on disk is replaced.
    /// </param>
    Task<ScriptsSyncResult> SyncScriptsAsync(bool verify, CancellationToken token);

    Task<long> CheckAdvanceSkillSetsUpdates();

    Task DownloadScriptAsync(ScriptInfo info);

    Task<int> DownloadAllWhereAsync(Func<ScriptInfo, bool> pred);

    Task DeleteScriptAsync(ScriptInfo info);

    Task<bool> UpdateSkillSetsFile();

    Task<bool> UpdateQuestDataFile();

    Task<long> CheckJunkItemsUpdates();

    Task<bool> UpdateJunkItemsFile();
}
