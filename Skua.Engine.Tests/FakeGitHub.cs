using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skua.Core.Models.GitHub;

namespace Skua.Engine.Tests;

/// <summary>
/// Stands in for raw.githubusercontent.com and api.github.com: it serves Script Source repositories whose history the test writes.
/// </summary>
/// <remarks>
/// Each repository's <c>scripts.json</c> is generated from its latest commit, like the Scripts repo's own workflow does, and its
/// <c>downloadUrl</c>s always point at <c>auqw/Scripts</c>, as a fork's do, so a test sees which repository files really come from.
/// </remarks>
public sealed class FakeGitHub : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Dictionary<string, FakeRepo> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dataFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Task _serving;

    public FakeGitHub()
    {
        (_listener, BaseUrl) = LoopbackHttp.Start();
        _serving = ServeAsync();
    }

    public string BaseUrl { get; }

    /// <summary>Delays every raw file response, so a test can catch an update in flight.</summary>
    public TimeSpan RawDelay { get; set; }

    /// <summary>Every raw file response waits for this, so a test can hold an update in flight for as long as it needs, then let it finish.</summary>
    public Task RawHeld { get; set; } = Task.CompletedTask;

    /// <summary>Answers every request with 503, as GitHub does when it can't be reached through a proxy or is down.</summary>
    public bool Down { get; set; }

    /// <summary>
    /// Answers the commits API's list and single commits with 403, as GitHub does past its rate limit; a branch's head still answers.
    /// </summary>
    public bool HistoryDown { get; set; }

    /// <summary>Every request path served so far, in order, e.g. <c>/raw/auqw/Scripts/refs/heads/Skua/scripts.json</c>.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

    /// <summary>Waits for the first raw file request, e.g. an update's <c>scripts.json</c>.</summary>
    public async Task WaitForRawRequestAsync()
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!Requests.Any(r => r.StartsWith("/raw/", StringComparison.Ordinal)))
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("No raw file was requested.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The raw paths of Script files fetched from a repository, relative to it.</summary>
    public IReadOnlyList<string> ScriptDownloads(string owner, string repo, string branch)
    {
        string prefix = $"/raw/{owner}/{repo}/refs/heads/{branch}/";
        return Requests.Where(r => r.StartsWith(prefix, StringComparison.Ordinal) && r.EndsWith(".cs", StringComparison.Ordinal))
            .Select(r => r[prefix.Length..])
            .ToList();
    }

    public void ClearRequests() => _requests.Clear();

    /// <summary>The environment that points an Engine (and anything it runs) at this fake.</summary>
    public IDictionary<string, string> Environment() => new Dictionary<string, string>
    {
        [ScriptSource.RawUrlEnvironmentVariable] = BaseUrl + "raw/",
        [ScriptSource.ApiUrlEnvironmentVariable] = BaseUrl + "api/",
    };

    /// <summary>Adds a commit, made now, to <c>owner/repo@branch</c> whose tree is the previous one with these files added or replaced.</summary>
    public FakeGitHub Commit(string owner, string repo, string branch, params FakeScript[] scripts) =>
        CommitAt(DateTimeOffset.UtcNow, owner, repo, branch, scripts);

    /// <summary>Adds a commit made at <paramref name="at"/>, which the commits API reports to the second, as GitHub does.</summary>
    /// <param name="removed">Paths the commit deletes.</param>
    public FakeGitHub CommitAt(DateTimeOffset at, string owner, string repo, string branch, FakeScript[] scripts, params string[] removed)
    {
        string key = RepoKey(owner, repo, branch);
        lock (_repos)
        {
            if (!_repos.TryGetValue(key, out FakeRepo? fake))
                _repos[key] = fake = new FakeRepo();
            fake.Commit(DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds()), scripts, removed);
        }
        return this;
    }

    /// <summary>
    /// Serves a file of <c>owner/repo@branch</c> that isn't a Script, such as the AdvanceSkill sets, the quest data or the junk items: it is
    /// in no commit and not in <c>scripts.json</c>. Null stops serving it.
    /// </summary>
    public FakeGitHub Put(string owner, string repo, string branch, string path, string? content)
    {
        string key = $"{RepoKey(owner, repo, branch)}/{path}";
        lock (_repos)
        {
            if (content is null)
                _dataFiles.Remove(key);
            else
                _dataFiles[key] = content;
        }
        return this;
    }

    /// <summary>The commit that <see cref="Commit"/> or <see cref="CommitAt"/> added to <c>owner/repo@branch</c> last.</summary>
    public string Head(string owner, string repo, string branch)
    {
        lock (_repos)
            return _repos[RepoKey(owner, repo, branch)].Head.Sha;
    }

    public async ValueTask DisposeAsync()
    {
        // Close alone: on macOS, Stop then Close removes the prefix twice, and the second removal binds the port again, which fails
        // with "Address already in use" once a parallel test has taken the freed port.
        _listener.Close();
        try
        {
            await _serving;
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        string path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
        _requests.Enqueue(path);
        (int status, byte[] body) = await RouteAsync(path, context.Request.QueryString);
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private async Task<(int Status, byte[] Body)> RouteAsync(string path, NameValueCollection query)
    {
        if (Down)
            return (503, Encoding.UTF8.GetBytes("""{"message":"Service Unavailable"}"""));
        string[] parts = path.Trim('/').Split('/');
        lock (_repos)
        {
            // /api/repos/{owner}/{repo}/commits/{branch}
            if (parts is ["api", "repos", var owner, var repo, "commits", var branch]
                && _repos.TryGetValue(RepoKey(owner, repo, branch), out FakeRepo? commitsRepo))
                return Json(commitsRepo.Head.ToJson());

            if (HistoryDown && parts is ["api", "repos", _, _, "commits", ..])
                return (403, Encoding.UTF8.GetBytes("""{"message":"API rate limit exceeded"}"""));

            // /api/repos/{owner}/{repo}/commits?sha={branch}&since={time}&per_page={max}: the newest first, without their files.
            if (parts is ["api", "repos", var lOwner, var lRepo, "commits"]
                && _repos.TryGetValue(RepoKey(lOwner, lRepo, query["sha"] ?? ""), out FakeRepo? listRepo))
            {
                DateTimeOffset since = query["since"] is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
                int max = query["per_page"] is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : 30;
                return Json(listRepo.History.Where(c => c.At >= since).Reverse().Take(max)
                    .Select(c => new { sha = c.Sha, commit = new { committer = new { date = c.At } } }));
            }

            // /api/repos/{owner}/{repo}/commits/{sha}
            if (parts is ["api", "repos", var dOwner, var dRepo, "commits", var sha]
                && _repos.Where(r => r.Key.StartsWith(RepoKey(dOwner, dRepo, ""), StringComparison.OrdinalIgnoreCase))
                    .SelectMany(r => r.Value.History).FirstOrDefault(c => c.Sha == sha) is { } commit)
                return Json(commit.ToJson());

            // /api/repos/{owner}/{repo}/compare/{base}...{head}
            if (parts is ["api", "repos", var cOwner, var cRepo, "compare", var range]
                && range.Split("...") is [var from, var to]
                && _repos.Where(r => r.Key.StartsWith(RepoKey(cOwner, cRepo, ""), StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Value.Compare(from, to)).FirstOrDefault(c => c is not null) is { } files)
                return Json(new { files = files.Select(f => new { filename = f, status = "modified" }) });
        }

        // /raw/{owner}/{repo}/refs/heads/{branch}/{path...}
        if (parts is ["raw", var rOwner, var rRepo, "refs", "heads", var rBranch, .. var rest] && rest.Length > 0)
        {
            await Task.Delay(RawDelay);
            await RawHeld;
            string file = string.Join('/', rest);
            lock (_repos)
            {
                if (_dataFiles.TryGetValue($"{RepoKey(rOwner, rRepo, rBranch)}/{file}", out string? data))
                    return (200, Encoding.UTF8.GetBytes(data));
                if (_repos.TryGetValue(RepoKey(rOwner, rRepo, rBranch), out FakeRepo? rawRepo))
                {
                    if (file == "scripts.json")
                        return (200, rawRepo.ScriptsJson());
                    if (rawRepo.Head.Files.TryGetValue(file, out FakeScript? script))
                        return (200, Encoding.UTF8.GetBytes(script.Content));
                }
            }
        }

        return (404, Encoding.UTF8.GetBytes("""{"message":"Not Found"}"""));
    }

    private static (int, byte[]) Json(object value) => (200, JsonSerializer.SerializeToUtf8Bytes(value));

    private static string RepoKey(string owner, string repo, string branch) => $"{owner}/{repo}@{branch}";

    private sealed class FakeRepo
    {
        private readonly List<FakeCommit> _history = [];

        public FakeCommit Head => _history[^1];

        /// <summary>The commits, the oldest first.</summary>
        public IReadOnlyList<FakeCommit> History => _history;

        public void Commit(DateTimeOffset at, FakeScript[] scripts, string[] removed)
        {
            Dictionary<string, FakeScript> files = _history.Count == 0 ? [] : new(Head.Files);
            List<(string, string)> changes = [];
            foreach (FakeScript script in scripts)
            {
                changes.Add((script.Path, files.ContainsKey(script.Path) ? "modified" : "added"));
                files[script.Path] = script;
            }
            foreach (string path in removed)
            {
                files.Remove(path);
                changes.Add((path, "removed"));
            }
            string sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(
                $"{_history.Count}:{string.Join(',', scripts.Select(s => s.Path + s.Content))}:{string.Join(',', removed)}")));
            _history.Add(new FakeCommit(sha, at, files, changes));
        }

        /// <summary>The files changed after <paramref name="from"/> up to <paramref name="to"/>, or null when this repo has neither commit.</summary>
        public IReadOnlyList<string>? Compare(string from, string to)
        {
            int start = _history.FindIndex(c => c.Sha == from);
            int end = _history.FindIndex(c => c.Sha == to);
            if (start < 0 || end < 0)
                return null;
            return _history.Skip(start + 1).Take(end - start).SelectMany(c => c.Changes.Select(f => f.Path)).Distinct().ToList();
        }

        public byte[] ScriptsJson() => JsonSerializer.SerializeToUtf8Bytes(Head.Files.Values.Select(s =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s.Content);
            string fileName = s.Path.Split('/')[^1];
            return new
            {
                name = s.Name ?? "null",
                description = s.Description ?? "null",
                tags = s.Tags.Length > 0 ? s.Tags : ["null"],
                path = s.Path,
                size = bytes.Length,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                fileName,
                downloadUrl = $"https://raw.githubusercontent.com/auqw/Scripts/Skua/{s.Path}",
            };
        }));
    }

    /// <param name="Changes">Each file the commit touched, with GitHub's status for it: added, modified or removed.</param>
    private sealed record FakeCommit(string Sha, DateTimeOffset At, IReadOnlyDictionary<string, FakeScript> Files, IReadOnlyList<(string Path, string Status)> Changes)
    {
        /// <summary>The commit as GitHub's commits API returns one.</summary>
        public object ToJson() => new
        {
            sha = Sha,
            commit = new { committer = new { date = At } },
            files = Changes.Select(c => new { filename = c.Path, status = c.Status }),
        };
    }
}

/// <summary>One Script in a fake Script Source; a null name or description is written as the string "null", as scripts.json does.</summary>
public sealed record FakeScript(string Path, string Content, string? Name = null, string? Description = null, params string[] Tags);
