using System.Text.Json;
using Skua.Core.Models.GitHub;
using Skua.Core.Utils;

namespace Skua.App.Engine.Scripts;

/// <summary>
/// The Script Source's recent commits, read from GitHub's commits API when a full download starts the Scripts afresh, so that
/// <c>scripts_new</c> has news from the first day instead of nothing.
/// </summary>
/// <remarks>
/// GitHub allows 60 unauthenticated API requests an hour, so a read is bounded by <see cref="MaxRequests"/> and never retries: the first
/// failure ends it, keeping the commits read so far.
/// </remarks>
internal static class ScriptSourceHistory
{
    /// <summary>The most commits read, the newest first; each costs one request for its files.</summary>
    public const int MaxCommits = 20;

    /// <summary>The most GitHub API requests one read makes: the list of commits, then each commit's files.</summary>
    public const int MaxRequests = MaxCommits + 1;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Reads the commits on the Script Source's branch made after <paramref name="since"/>. Never throws but for cancellation.</summary>
    public static async Task<SourceHistory> ReadAsync(ScriptSource source, DateTimeOffset since, CancellationToken cancellationToken)
    {
        List<SourceCommit> commits = [];
        bool listedAll = false;
        try
        {
            using JsonDocument list = await GetAsync(source.CommitsUrl(since, MaxCommits), cancellationToken);
            List<string> shas = list.RootElement.EnumerateArray().Select(c => c.GetProperty("sha").GetString()!).ToList();
            listedAll = shas.Count < MaxCommits;
            // Newest first, so the commits read always reach back unbroken from the head, however far a failure lets the read get.
            foreach (string sha in shas)
            {
                using JsonDocument commit = await GetAsync(source.CommitDetailsUrl(sha), cancellationToken);
                commits.Add(ToCommit(commit.RootElement));
            }
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            EngineLog.Write($"Read {commits.Count} commits of the history of the Script Source {source}, then couldn't read more: {e.Message}");
            listedAll = false;
        }

        DateTimeOffset? from = listedAll ? since : commits.Count > 0 ? commits[^1].At : null;
        return new SourceHistory(WithoutRemoved(commits), from);
    }

    private static async Task<JsonDocument> GetAsync(string url, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using HttpResponseMessage response = await HttpClients.GetGHClient().GetAsync(url, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
    }

    private static SourceCommit ToCommit(JsonElement commit)
    {
        List<SourceFile> files = [];
        if (commit.TryGetProperty("files", out JsonElement changed))
        {
            foreach (JsonElement file in changed.EnumerateArray())
            {
                string path = file.GetProperty("filename").GetString()!;
                string? previous = file.TryGetProperty("previous_filename", out JsonElement p) ? p.GetString() : null;
                files.Add(new SourceFile(path, file.GetProperty("status").GetString() ?? "", previous));
            }
        }
        return new SourceCommit(commit.GetProperty("sha").GetString()!,
            commit.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset(), files);
    }

    /// <summary>
    /// Each commit's Scripts, less those a later commit removed or renamed away, which aren't in the Script Source any more.
    /// </summary>
    private static List<ScriptSourceCommit> WithoutRemoved(List<SourceCommit> newestFirst)
    {
        HashSet<string> gone = new(StringComparer.Ordinal);
        List<ScriptSourceCommit> commits = [];
        foreach (SourceCommit commit in newestFirst)
        {
            List<string> added = [];
            List<string> changed = [];
            foreach (SourceFile file in commit.Files)
            {
                if (file.Previous is { } previous)
                    gone.Add(previous);
                if (file.Status == "removed")
                {
                    gone.Add(file.Path);
                    continue;
                }
                if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || gone.Contains(file.Path))
                    continue;
                (file.Status == "added" ? added : changed).Add(file.Path);
            }
            commits.Add(new ScriptSourceCommit(commit.Sha, commit.At, added, changed));
        }
        commits.Reverse();
        return commits;
    }

    private sealed record SourceCommit(string Sha, DateTimeOffset At, IReadOnlyList<SourceFile> Files);

    private sealed record SourceFile(string Path, string Status, string? Previous);
}

/// <summary>What <see cref="ScriptSourceHistory"/> read.</summary>
/// <param name="Commits">The commits read, the oldest first.</param>
/// <param name="From">Since when the commits are complete, or null when none could be read.</param>
internal sealed record SourceHistory(IReadOnlyList<ScriptSourceCommit> Commits, DateTimeOffset? From);

/// <summary>One commit of the Script Source: the <c>.cs</c> files it added and changed, by path.</summary>
internal sealed record ScriptSourceCommit(string Sha, DateTimeOffset At, IReadOnlyList<string> Added, IReadOnlyList<string> Changed);
