using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, FakeRepo> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _requests = new();
    private readonly Task _serving;

    public FakeGitHub()
    {
        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _serving = ServeAsync();
    }

    public string BaseUrl { get; }

    /// <summary>Delays every raw file response, so a test can catch an update in flight.</summary>
    public TimeSpan RawDelay { get; set; }

    /// <summary>Answers every request with 503, as GitHub does when it can't be reached through a proxy or is down.</summary>
    public bool Down { get; set; }

    /// <summary>Every request path served so far, in order, e.g. <c>/raw/auqw/Scripts/refs/heads/Skua/scripts.json</c>.</summary>
    public IReadOnlyList<string> Requests => [.. _requests];

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

    /// <summary>Adds a commit to <c>owner/repo@branch</c> whose tree is the previous one with these files added or replaced.</summary>
    public FakeGitHub Commit(string owner, string repo, string branch, params FakeScript[] scripts)
    {
        string key = RepoKey(owner, repo, branch);
        lock (_repos)
        {
            if (!_repos.TryGetValue(key, out FakeRepo? fake))
                _repos[key] = fake = new FakeRepo();
            fake.Commit(scripts);
        }
        return this;
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
        (int status, byte[] body) = await RouteAsync(path);
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    private async Task<(int Status, byte[] Body)> RouteAsync(string path)
    {
        if (Down)
            return (503, Encoding.UTF8.GetBytes("""{"message":"Service Unavailable"}"""));
        string[] parts = path.Trim('/').Split('/');
        lock (_repos)
        {
            // /api/repos/{owner}/{repo}/commits/{branch}
            if (parts is ["api", "repos", var owner, var repo, "commits", var branch]
                && _repos.TryGetValue(RepoKey(owner, repo, branch), out FakeRepo? commitsRepo))
                return Json(new { sha = commitsRepo.Head.Sha });

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
            string file = string.Join('/', rest);
            lock (_repos)
            {
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

    private static int FreePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class FakeRepo
    {
        private readonly List<FakeCommit> _history = [];

        public FakeCommit Head => _history[^1];

        public void Commit(FakeScript[] scripts)
        {
            Dictionary<string, FakeScript> files = _history.Count == 0 ? [] : new(Head.Files);
            foreach (FakeScript script in scripts)
                files[script.Path] = script;
            string sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{_history.Count}:{string.Join(',', scripts.Select(s => s.Path + s.Content))}")));
            _history.Add(new FakeCommit(sha, files, scripts.Select(s => s.Path).ToList()));
        }

        /// <summary>The files changed after <paramref name="from"/> up to <paramref name="to"/>, or null when this repo has neither commit.</summary>
        public IReadOnlyList<string>? Compare(string from, string to)
        {
            int start = _history.FindIndex(c => c.Sha == from);
            int end = _history.FindIndex(c => c.Sha == to);
            if (start < 0 || end < 0)
                return null;
            return _history.Skip(start + 1).Take(end - start).SelectMany(c => c.Changed).Distinct().ToList();
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

    private sealed record FakeCommit(string Sha, IReadOnlyDictionary<string, FakeScript> Files, IReadOnlyList<string> Changed);
}

/// <summary>One Script in a fake Script Source; a null name or description is written as the string "null", as scripts.json does.</summary>
public sealed record FakeScript(string Path, string Content, string? Name = null, string? Description = null, params string[] Tags);
