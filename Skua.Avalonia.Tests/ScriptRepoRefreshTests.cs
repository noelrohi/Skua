using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using Skua.Core.Interfaces;
using Skua.Core.Models.GitHub;
using Skua.Core.Utils;
using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>
/// The Script Repo's refresh after a failed <c>scripts.json</c> fetch: Core's retrying GET, and the view model's refresh command, each
/// against its own stand-in, so nothing reaches GitHub or the app's shared state.
/// </summary>
public sealed class ScriptRepoRefreshTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_404_fails_at_once_without_a_retry()
    {
        StubHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(30) };

        HttpRequestException failed = await Assert.ThrowsAsync<HttpRequestException>(
            () => ValidatedHttpExtensions.GetAsyncWithRetry(client, "http://scripts.test/scripts.json", Ct));

        Assert.Equal(HttpStatusCode.NotFound, failed.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_503_then_a_network_error_are_retried_until_the_file_answers()
    {
        int request = 0;
        StubHandler handler = new(_ => Interlocked.Increment(ref request) switch
        {
            1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            2 => throw new HttpRequestException("Connection refused"),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }),
        });
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(30) };

        using HttpResponseMessage response = await ValidatedHttpExtensions.GetAsyncWithRetry(client, "http://scripts.test/scripts.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task A_server_that_never_answers_fails_within_the_clients_timeout_retries_included()
    {
        StubHandler handler = new(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromMilliseconds(500) };
        Stopwatch elapsed = Stopwatch.StartNew();

        TaskCanceledException timedOut = await Assert.ThrowsAsync<TaskCanceledException>(
            () => ValidatedHttpExtensions.GetAsyncWithRetry(client, "http://scripts.test/scripts.json", Ct));

        // Before, each of three attempts had the whole timeout, with 1 s and 2 s between them.
        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
        Assert.IsType<TimeoutException>(timedOut.InnerException);
    }

    [Fact]
    public async Task Cancelling_the_request_is_a_cancellation_not_a_timeout()
    {
        StubHandler handler = new(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ValidatedHttpExtensions.GetAsyncWithRetry(client, "http://scripts.test/scripts.json", cancel.Token));

        Assert.IsNotType<TimeoutException>(cancelled.InnerException);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task A_refresh_asked_for_while_one_hangs_restarts_it_and_the_two_never_fetch_at_once()
    {
        FakeScripts scripts = new();
        ScriptRepoViewModel repo = new(scripts, new NoProcesses());

        repo.RefreshScriptsCommand.Execute(null);
        FakeScripts.Refresh hung = await scripts.NextRefreshAsync();
        // The Refresh button stays on, so the view (or a click) can ask again.
        Assert.True(repo.RefreshScriptsCommand.CanExecute(null));
        repo.RefreshScriptsCommand.Execute(null);

        Assert.True(hung.Token.IsCancellationRequested);
        hung.Unwind.SetResult();
        FakeScripts.Refresh restarted = await scripts.NextRefreshAsync();
        Assert.True(repo.IsBusy);
        restarted.Finish(Script("Tests/Restarted.cs"));
        await repo.RefreshScriptsCommand.ExecutionTask!.WaitAsync(Ct);

        Assert.Equal(1, scripts.MostAtOnce);
        Assert.Equal(["Tests/Restarted.cs"], repo.Scripts.Select(s => s.FilePath));
        Assert.False(repo.IsBusy);
    }

    [Fact]
    public async Task The_refresh_opening_the_Script_Repo_starts_is_the_commands_so_a_later_one_restarts_it()
    {
        FakeScripts scripts = new();
        ScriptRepoViewModel repo = new(scripts, new NoProcesses());
        try
        {
            repo.IsActive = true;
            FakeScripts.Refresh opening = await scripts.NextRefreshAsync();
            Assert.True(repo.RefreshScriptsCommand.IsRunning);

            repo.RefreshScriptsCommand.Execute(null);

            Assert.True(opening.Token.IsCancellationRequested);
            opening.Unwind.SetResult();
            (await scripts.NextRefreshAsync()).Finish(Script("Tests/Opened.cs"));
            await repo.RefreshScriptsCommand.ExecutionTask!.WaitAsync(Ct);
            Assert.Equal(["Tests/Opened.cs"], repo.Scripts.Select(s => s.FilePath));
            Assert.False(repo.IsBusy);
        }
        finally
        {
            repo.IsActive = false;
        }
    }

    private static ScriptInfo Script(string path) => new() { FilePath = path, Name = Path.GetFileNameWithoutExtension(path), FileName = Path.GetFileName(path) };

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return answer(cancellationToken);
        }
    }

    /// <summary>
    /// A Script Source whose every refresh waits for the test: it finishes with the Scripts the test gives, or, once cancelled, returns
    /// when the test lets it unwind, as the real service does after reporting the cancellation.
    /// </summary>
    private sealed class FakeScripts : IGetScriptsService
    {
        private readonly ConcurrentQueue<Refresh> _started = new();
        private int _atOnce;
        private int _mostAtOnce;

        public event PropertyChangedEventHandler? PropertyChanged;

        public RangedObservableCollection<ScriptInfo> Scripts { get; } = new();

        public ScriptSource Source => new();

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public sealed record Refresh(CancellationToken Token)
        {
            public TaskCompletionSource Unwind { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<ScriptInfo[]> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Finish(params ScriptInfo[] scripts) => Done.SetResult(scripts);
        }

        public async Task<Refresh> NextRefreshAsync()
        {
            Stopwatch waited = Stopwatch.StartNew();
            Refresh? refresh;
            while (!_started.TryDequeue(out refresh))
            {
                if (waited.Elapsed > TimeSpan.FromSeconds(10))
                    throw new TimeoutException("No refresh started.");
                await Task.Delay(10, Ct);
            }
            return refresh;
        }

        public async Task RefreshScriptsAsync(IProgress<string>? progress, CancellationToken token)
        {
            int atOnce = Interlocked.Increment(ref _atOnce);
            InterlockedMax(ref _mostAtOnce, atOnce);
            try
            {
                Refresh refresh = new(token);
                _started.Enqueue(refresh);
                using CancellationTokenRegistration cancelled = token.Register(() => refresh.Done.TrySetCanceled(token));
                try
                {
                    ScriptInfo[] scripts = await refresh.Done.Task;
                    Scripts.Clear();
                    Scripts.AddRange(scripts);
                    PropertyChanged?.Invoke(this, new(nameof(Scripts)));
                }
                catch (OperationCanceledException)
                {
                    await refresh.Unwind.Task.WaitAsync(Ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _atOnce);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }

        public ValueTask<List<ScriptInfo>> GetScriptsAsync(IProgress<string>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<int> IncrementalUpdateScriptsAsync(IProgress<string>? progress, CancellationToken token) => throw new NotSupportedException();
        public Task<List<ScriptInfo>> FetchScriptsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ScriptsSyncResult> SyncScriptsAsync(bool verify, CancellationToken token) => throw new NotSupportedException();
        public Task<long> CheckAdvanceSkillSetsUpdates() => throw new NotSupportedException();
        public Task DownloadScriptAsync(ScriptInfo info) => throw new NotSupportedException();
        public Task<int> DownloadAllWhereAsync(Func<ScriptInfo, bool> pred) => throw new NotSupportedException();
        public Task DeleteScriptAsync(ScriptInfo info) => throw new NotSupportedException();
        public Task<bool> UpdateSkillSetsFile() => throw new NotSupportedException();
        public Task<bool> UpdateQuestDataFile() => throw new NotSupportedException();
        public Task<long> CheckJunkItemsUpdates() => throw new NotSupportedException();
        public Task<bool> UpdateJunkItemsFile() => throw new NotSupportedException();
    }

    private sealed class NoProcesses : IProcessService
    {
        public void OpenLink(string link) => throw new NotSupportedException();
        public void OpenVSC() => throw new NotSupportedException();
        public void OpenVSC(string path) => throw new NotSupportedException();
    }
}
