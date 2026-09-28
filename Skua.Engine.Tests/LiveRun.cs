using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>What a live run judges and where it writes, apart from the scenario's own steps.</summary>
public sealed record LiveRunOptions
{
    /// <summary>How long one sample takes; a phase lasts a whole number of them. A minute, except in the dry runs against the fake Game Host.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The folder the run's report, screenshots and, on failure, its logs go in.</summary>
    public required string OutDir { get; init; }

    /// <summary>
    /// Refuses to start unless the 1-minute load average is under <see cref="LiveRun.QuietLoad"/>, for the memory, footprint, fps and getter
    /// gates. Without it, a sample taken at a higher load isn't held to the getter gate.
    /// </summary>
    public bool RequireQuietMac { get; init; }

    /// <summary>The Scripts checkout copied into the data folder, for runs that start a Script.</summary>
    public string? ScriptsCheckout { get; init; }

    /// <summary>Whether the screen is locked and whether the display is asleep; the Mac's own, except in the dry runs.</summary>
    public Func<Task<(bool Locked, bool DisplayAsleep)>> Visibility { get; init; } =
        async () => (await LiveMetrics.ScreenLockedAsync(), LiveMetrics.DisplayAsleep());
}

/// <summary>The outcome of a live run.</summary>
/// <param name="Failures">Every gate that failed and every error, in order; empty when the run passed.</param>
public sealed record LiveRunResult(string OutDir, IReadOnlyList<string> Failures, IReadOnlyList<MinuteSample> Samples, IReadOnlyList<GameHostStats> Stats)
{
    public bool Passed => Failures.Count == 0;

    /// <summary>Writes the run's report to the test output, then fails the test unless the run passed.</summary>
    public void AssertPassed()
    {
        TestContext.Current.TestOutputHelper?.WriteLine(File.ReadAllText(Path.Combine(OutDir, "report.txt")));
        Assert.True(Passed, $"The live run failed; its report, a screenshot and the logs are in {OutDir}:\n- {string.Join("\n- ", Failures)}");
    }
}

/// <summary>A process's footprint (Activity Monitor's Memory, the gate metric) and RSS in MB; each null when it couldn't be read.</summary>
public sealed record ProcessMemory(double? FootprintMb, double? RssMb)
{
    public static async Task<ProcessMemory> ReadAsync(int pid) => new(await LiveMetrics.FootprintMbAsync(pid), await LiveMetrics.RssMbAsync(pid));
}

/// <summary>One sample: memory of both processes and the getter latencies measured during it.</summary>
/// <param name="Minute">The sample's number in its phase, from 0.</param>
/// <param name="GetterMs">Round trips of <c>Bot.Player.Cell</c>, measured in the Engine by <c>eval</c> as a Script's waits make them.</param>
/// <param name="Locked">Whether the screen was locked when the sample ended.</param>
/// <param name="DisplayAsleep">Whether the main display was asleep when the sample ended.</param>
/// <param name="Player">The player as <c>status</c> summarised it when the sample ended, or null when it didn't.</param>
public sealed record MinuteSample(
    string Phase, int Minute, DateTimeOffset At, ProcessMemory GameHost, ProcessMemory Engine, IReadOnlyList<double> GetterMs, double Load, bool Locked, bool DisplayAsleep, int ScriptLines, PlayerDto? Player)
{
    /// <summary>Whether nobody could see the game: the screen was locked or the display asleep.</summary>
    public bool Hidden => Locked || DisplayAsleep;
}

/// <summary>A span of a live run with the same gates, e.g. 5 minutes idle in battleon.</summary>
/// <param name="Minutes">How many samples it lasts.</param>
/// <param name="Script">Whether a Script must keep running through it.</param>
/// <param name="Hidden">
/// Whether the screen must stay locked or the display asleep, with the Game Host ticking at its frame rate and no throttled tick gaps, and the Script progressing:
/// the player's gold or level rises in every <see cref="LiveRun.ProgressWindow"/> samples.
/// </param>
/// <param name="ScreenshotEvery">Checks a screenshot every this many samples, and at the end.</param>
/// <param name="FlatForMinutes">Checks that the footprint's slope over this many last samples stays within <see cref="LiveRun.MaxSlopeMbPerMin"/>.</param>
public sealed record LivePhase(string Name, int Minutes, bool Script = false, bool Hidden = false, int? ScreenshotEvery = null, int? FlatForMinutes = null);

/// <summary>
/// A live-game run: a real Engine with the real Game Host and the Test Account, driven only through the Control Surface, as agents drive it.
/// It observes through <c>logs</c> cursors, <c>eval</c> and <c>screenshot</c>, samples footprint every minute, and on failure saves a screenshot
/// plus <c>logs(all)</c> since the run's start.
/// </summary>
/// <remarks>
/// A disconnect, a relogin or the Game Host exiting ends the run at once and stops the Script, so a run never logs in twice.
/// It never sees the Test Account's password: the Engine reads it from Keychain and redacts it from everything it returns.
/// </remarks>
public sealed class LiveRun
{
    /// <summary>The footprint gate: the Game Host stays under 2 GB.</summary>
    public const double MaxFootprintMb = 2048;

    /// <summary>"Flat": the footprint's least-squares slope over the last hour, in MB per minute (#13's pass was 0.29, its no-go 8.4).</summary>
    public const double MaxSlopeMbPerMin = 1;

    /// <summary>The getter gate: <c>Bot.Player.Cell</c>'s p99 round trip in every sample but a phase's first, which holds its join or Script start.</summary>
    public const double MaxGetterP99Ms = 50;

    /// <summary>Hidden running: the frame rate the Game Host must report, the Game Client's 30 fps.</summary>
    public const double FrameRate = 30;

    public static readonly TimeSpan MaxJoin = TimeSpan.FromSeconds(10);

    /// <summary>Hidden running: the Game Host ticks at least this share of its frame rate.</summary>
    public const double MinTickShare = 0.9;

    /// <summary>Hidden running: no tick gap longer than this, where App Nap or timer throttling stretch them to a second or more.</summary>
    public const double MaxTickGapMs = 250;

    /// <summary>Hidden running: the samples in which the player's gold or level must rise, since a farming Script needn't log every minute.</summary>
    public const int ProgressWindow = 5;

    /// <summary>The 1-minute load average under which the Mac counts as quiet.</summary>
    public const double QuietLoad = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly EngineSandbox _sandbox;
    private readonly EngineConnection _connection;
    private readonly LiveRunOptions _options;
    private readonly StreamWriter _report;
    private readonly List<string> _failures = [];
    private readonly List<MinuteSample> _samples = [];
    private readonly List<GameHostStats> _stats = [];
    private readonly List<long> _scriptLineTimes = [];
    private readonly CancellationTokenSource _fatal = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private int _gameHostPid;
    private bool _stoppingScript;
    private bool _loggingOut;
    private bool _scriptExpected;
    private bool _loggedIn;

    private LiveRun(EngineSandbox sandbox, EngineConnection connection, LiveRunOptions options, StreamWriter report)
    {
        _sandbox = sandbox;
        _connection = connection;
        _options = options;
        _report = report;
    }

    public EngineConnection Connection => _connection;

    /// <summary>Set once a disconnect, a relogin or the Game Host exiting has ended the run.</summary>
    public CancellationToken Fatal => _fatal.Token;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Starts an Engine in a scratch data folder with <paramref name="environment"/>, waits for the login screen, runs
    /// <paramref name="scenario"/>, then logs out and stops the Engine. Every failure, including an exception, is collected in the result,
    /// and a failed run also leaves a screenshot and the logs in <see cref="LiveRunOptions.OutDir"/>.
    /// </summary>
    public static async Task<LiveRunResult> RunAsync(string name, IDictionary<string, string> environment, LiveRunOptions options, Func<LiveRun, Task> scenario)
    {
        string outDir = Directory.CreateDirectory(options.OutDir).FullName;
        await using StreamWriter report = new(Path.Combine(outDir, "report.txt")) { AutoFlush = true };
        report.WriteLine($"{DateTimeOffset.Now:u} live run '{name}'");

        if (options.RequireQuietMac && await LiveMetrics.LoadAverageAsync() is var load && load >= QuietLoad)
        {
            string failure = $"The Mac isn't quiet: the 1-minute load average is {load:0.00}, not under {QuietLoad}; stop other tests, builds and Game Hosts first.";
            report.WriteLine("FAIL " + failure);
            return new LiveRunResult(outDir, [failure], [], []);
        }

        await using EngineSandbox sandbox = new();
        SeedDataFolder(sandbox.SkuaDir, options.ScriptsCheckout);
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(environment);
        // Unread, the Engine's stdout and stderr pipes fill over a long run and block it.
        _ = engine.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = engine.StandardError.BaseStream.CopyToAsync(Stream.Null);

        LiveRun run = new(sandbox, connection, options, report);
        using (connection)
        {
            using CancellationTokenSource stopWatching = new();
            Task watching = run.WatchAsync(stopWatching.Token);
            try
            {
                LogEntryDto started = await run.WaitForEventAsync(EventTypes.GameHostStarted, TimeSpan.FromSeconds(30));
                run._gameHostPid = started.Data!.Value.GetProperty("pid").GetInt32();
                await run.WaitForEventAsync(EventTypes.GameState, TimeSpan.FromMinutes(2), e => GameEvents.To(e) == "loginScreen");
                run.Note($"Engine pid {connection.Hello.Pid}, Game Host pid {run._gameHostPid}, data folder {sandbox.SkuaDir}");
                await scenario(run);
            }
            catch (Exception e) when (e is not LiveRunEnded)
            {
                run.Fail($"The run threw {e.GetType().Name}: {e.Message}");
            }
            catch (LiveRunEnded)
            {
            }
            finally
            {
                await run.EndAsync();
                stopWatching.Cancel();
                await watching;
            }
        }
        return new LiveRunResult(outDir, run._failures, run._samples, run._stats);
    }

    /// <summary>
    /// Pre-seeds the one-time Script Dialog files CoreBots reads, so a first start in a fresh data folder raises no Data Collection or
    /// One Time-Only Questions, and copies the Scripts checkout in.
    /// </summary>
    public static void SeedDataFolder(string skuaDir, string? scriptsCheckout)
    {
        File.WriteAllLines(Path.Combine(skuaDir, "DataCollectionSettings.txt"),
            ["UserID: ", "genericDataConsent: False", "scriptNameConsent: False", "stopTimeConsent: False"]);
        File.WriteAllLines(Path.Combine(skuaDir, "OneTimeMessages.txt"), ["discordV11", "Xmax2025", "WARNING!"]);

        if (scriptsCheckout is null)
            return;
        string scripts = Path.Combine(skuaDir, "Scripts");
        foreach (string file in Directory.EnumerateFiles(scriptsCheckout, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(scriptsCheckout, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0] == ".git")
                continue;
            string target = Path.Combine(scripts, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Logs the Test Account in on <paramref name="server"/> as an agent, once; any failure (a rejection, a captcha, a rate limit) ends the run.</summary>
    public async Task LoginAsync(string server)
    {
        Stopwatch took = Stopwatch.StartNew();
        LoginResult result;
        try
        {
            // As an agent: the Test Account, whatever account the data folder names.
            result = await _connection.AgentLoginAsync(server, 120, Ct);
        }
        catch (ControlException e)
        {
            throw End($"login on {server} failed with {e.Code}: {e.Message}");
        }
        _loggedIn = true;
        Note($"logged in on {result.Server} in {took.Elapsed.TotalSeconds:0.0} s");
    }

    /// <summary>Joins a map and checks it took at most <see cref="MaxJoin"/>; returns false when the player was already there.</summary>
    public async Task<bool> JoinAsync(string map)
    {
        Stopwatch took = Stopwatch.StartNew();
        LocationResult result = await _connection.JoinAsync(map, timeoutSec: 60, cancellationToken: Ct);
        TimeSpan elapsed = took.Elapsed;
        Note($"join {map}: {(result.AlreadyThere ? "already there" : $"{elapsed.TotalSeconds:0.00} s")} → {result.Map} {result.Cell} {result.Pad}");
        if (!result.AlreadyThere && elapsed > MaxJoin)
            Fail($"join {map} took {elapsed.TotalSeconds:0.0} s, over {MaxJoin.TotalSeconds:0} s");
        return !result.AlreadyThere;
    }

    /// <summary>Starts a Script whose Questions get the fallback at once, so an unattended run never waits on one.</summary>
    public async Task StartScriptAsync(string script)
    {
        ScriptStartResult started = await _connection.ScriptStartAsync(script, dialogs: DialogMode.Cancel, cancellationToken: Ct);
        _scriptExpected = true;
        Note($"started {script} as run {started.Run}");
    }

    public async Task StopScriptAsync()
    {
        _stoppingScript = true;
        ScriptStopResult stopped = await _connection.ScriptStopAsync(Ct);
        _scriptExpected = false;
        Note($"stopped the Script: ended={stopped.Ended}, outcome {stopped.Status.LastRun?.Outcome}");
        if (stopped.WasRunning && !stopped.Ended)
            Fail("The Script's thread didn't end when stopped.");
    }

    /// <summary>Takes a screenshot, saves it as <c>screenshot-&lt;label&gt;.png</c> and checks it shows the stage.</summary>
    public async Task CheckScreenshotAsync(string label)
    {
        ScreenshotResult shot = await _connection.ScreenshotAsync(cancellationToken: Ct);
        string file = Path.Combine(_options.OutDir, $"screenshot-{label}.png");
        await File.WriteAllBytesAsync(file, shot.Png, Ct);
        if (LiveMetrics.ScreenshotProblem(shot.Png) is { } problem)
            Fail($"The {label} screenshot isn't correct: {problem}; see {file}.");
        else
            Note($"screenshot {label}: {shot.Width}x{shot.Height}, frame {shot.Frame}, {file}");
    }

    /// <summary>Waits until the screen is locked or the display asleep (<c>pmset displaysleepnow</c>), for the hidden-running check.</summary>
    public async Task WaitForHiddenAsync(TimeSpan timeout)
    {
        Note($"waiting up to {timeout.TotalMinutes:0} min for the screen to be locked or the display to sleep");
        Stopwatch waited = Stopwatch.StartNew();
        while (await _options.Visibility() is (false, false))
        {
            if (waited.Elapsed > timeout)
                throw End($"The screen wasn't locked, nor the display asleep, within {timeout.TotalMinutes:0} min.");
            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }
        (bool locked, bool asleep) = await _options.Visibility();
        Note($"hidden after {waited.Elapsed.TotalSeconds:0} s: locked={locked}, display asleep={asleep}");
    }

    /// <summary>Runs one phase: a sample every minute, then its gates.</summary>
    public async Task PhaseAsync(LivePhase phase)
    {
        Note($"phase {phase.Name}: {phase.Minutes} × {_options.SampleInterval.TotalSeconds:0} s");
        long startTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<MinuteSample> samples = [];
        for (int minute = 0; minute < phase.Minutes && !Fatal.IsCancellationRequested; minute++)
        {
            Stopwatch took = Stopwatch.StartNew();
            MinuteSample sample = await SampleAsync(phase.Name, minute);
            samples.Add(sample);
            _samples.Add(sample);
            Note(Describe(sample));
            if (phase.Hidden && !sample.Hidden)
                Fail($"{phase.Name} minute {minute}: the screen wasn't locked and the display was awake.");
            if (phase.ScreenshotEvery is { } every && minute > 0 && minute % every == 0)
                await CheckScreenshotAsync($"{phase.Name}-{minute}");
            if (_options.SampleInterval - took.Elapsed is { Ticks: > 0 } rest)
                await Task.Delay(rest, Ct);
        }
        if (Fatal.IsCancellationRequested)
            throw new LiveRunEnded();

        Judge(phase, samples, startTs);
        if (phase.Script && await _connection.ScriptWaitAsync(0, Ct) is { Reason: not ScriptWaitReason.Timeout } wait)
            Fail($"{phase.Name}: the Script isn't running at the end: script_wait returned {wait.Reason}, last run {wait.Status.LastRun?.Outcome} {wait.Status.LastRun?.Error}");
        if (phase.ScreenshotEvery is not null)
            await CheckScreenshotAsync($"{phase.Name}-end");
    }

    private void Judge(LivePhase phase, List<MinuteSample> samples, long startTs)
    {
        foreach (MinuteSample sample in samples)
        {
            // The getter gate leaves out the first sample, which holds the phase's join or the Script's start.
            double p99 = LiveMetrics.Percentile(sample.GetterMs, 0.99);
            if (sample.Minute > 0 && sample.GetterMs.Count == 0)
                Fail($"{phase.Name} minute {sample.Minute}: no getter round trips were measured.");
            else if (sample.Minute > 0 && p99 > MaxGetterP99Ms && !_options.RequireQuietMac && sample.Load >= QuietLoad)
                // A run that doesn't need a quiet Mac, such as a dry run beside other tests, measures the Mac's load as much as the Engine.
                Note($"{phase.Name} minute {sample.Minute}: getter p99 {p99:0.0} ms, not judged: the load average was {sample.Load:0.00}.");
            else if (sample.Minute > 0 && p99 > MaxGetterP99Ms)
                Fail($"{phase.Name} minute {sample.Minute}: getter p99 {p99:0.0} ms, over {MaxGetterP99Ms} ms.");
            if (sample.GameHost.FootprintMb is not { } footprint)
                Fail($"{phase.Name} minute {sample.Minute}: the Game Host's footprint couldn't be read.");
            else if (footprint >= MaxFootprintMb)
                Fail($"{phase.Name} minute {sample.Minute}: the Game Host's footprint is {footprint:0} MB, not under {MaxFootprintMb} MB.");
        }

        if (phase.FlatForMinutes is { } window)
        {
            List<(double X, double Y)> points = samples.TakeLast(window)
                .Where(s => s.GameHost.FootprintMb is not null)
                .Select(s => ((s.At - samples[0].At).TotalMinutes, s.GameHost.FootprintMb!.Value)).ToList();
            double slope = LiveMetrics.Slope(points);
            Note($"{phase.Name}: footprint slope over the last {points.Count} samples {slope:+0.00;-0.00} MB/min");
            if (slope > MaxSlopeMbPerMin)
                Fail($"{phase.Name}: the Game Host's footprint isn't flat: {slope:0.00} MB/min over the last {points.Count} samples, over {MaxSlopeMbPerMin}.");
        }

        if (phase.Hidden)
        {
            // Windows of ProgressWindow samples, the last one shorter when the phase doesn't divide evenly.
            for (int start = 0; start < samples.Count - 1; start += ProgressWindow)
            {
                int end = Math.Min(start + ProgressWindow, samples.Count - 1);
                (PlayerDto? from, PlayerDto? to) = (samples[start].Player, samples[end].Player);
                if (from is null || to is null || (to.Gold <= from.Gold && to.Level <= from.Level))
                    Fail($"{phase.Name} minutes {start}–{end}: the player's gold and level didn't rise " +
                         $"({from?.Gold} → {to?.Gold} gold, level {from?.Level} → {to?.Level}), so the Script isn't progressing.");
            }

            // The first line covers time before the phase.
            List<GameHostStats> stats;
            lock (_stats)
                stats = _stats.Where(s => s.Ts > startTs).ToList();
            if (stats.Count < 2)
                Fail($"{phase.Name}: only {stats.Count} Game Host stats lines arrived, so its frame rate is unknown.");
            else
                Note($"{phase.Name}: {stats.Count} Game Host stats lines; frame rate {string.Join("/", stats.Select(s => s.FrameRate).Distinct())} fps, " +
                     $"ticks {stats.Zip(stats.Skip(1), LiveMetrics.TicksPerSecond).Min():0}–{stats.Zip(stats.Skip(1), LiveMetrics.TicksPerSecond).Max():0}/s, " +
                     $"largest tick gap {stats.Skip(1).Max(s => s.MaxTickGapMs)} ms");
            for (int i = 1; i < stats.Count; i++)
            {
                double ticks = LiveMetrics.TicksPerSecond(stats[i - 1], stats[i]);
                if (Math.Abs(stats[i].FrameRate - FrameRate) > 1)
                    Fail($"{phase.Name}: the Game Host reported {stats[i].FrameRate} fps, not {FrameRate}.");
                if (!(ticks >= MinTickShare * stats[i].FrameRate))
                    Fail($"{phase.Name}: the Game Host ticked {ticks:0.0}/s, under {MinTickShare:P0} of its {stats[i].FrameRate} fps.");
                if (!(stats[i].MaxTickGapMs <= MaxTickGapMs))
                    Fail($"{phase.Name}: a tick gap of {stats[i].MaxTickGapMs} ms, over {MaxTickGapMs} ms, so ticks were throttled.");
            }
        }
    }

    private async Task<MinuteSample> SampleAsync(string phase, int minute)
    {
        long from = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IReadOnlyList<double> getter = await MeasureGetterAsync(_options.SampleInterval * 0.9);
        int enginePid = _connection.Hello.Pid;
        long to = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int scriptLines;
        lock (_scriptLineTimes)
            scriptLines = _scriptLineTimes.Count(t => t >= from && t < to);
        (bool locked, bool asleep) = await _options.Visibility();
        return new MinuteSample(
            phase, minute, DateTimeOffset.Now,
            await ProcessMemory.ReadAsync(_gameHostPid), await ProcessMemory.ReadAsync(enginePid), getter, await LiveMetrics.LoadAverageAsync(), locked, asleep, scriptLines,
            (await _connection.StatusAsync(Ct)).Game.Player);
    }

    /// <summary>
    /// Reads <c>Bot.Player.Cell</c> back to back with a short sleep for <paramref name="duration"/>, as a Script's waits do, in an
    /// <c>eval</c> snippet, and returns each round trip in ms.
    /// </summary>
    private async Task<IReadOnlyList<double>> MeasureGetterAsync(TimeSpan duration)
    {
        string code = $$"""
            var took = new List<double>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < {{(long)duration.TotalMilliseconds}})
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = Bot.Player.Cell;
                took.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                Thread.Sleep(20);
            }
            return took;
            """;
        try
        {
            EvalResult result = await _connection.EvalAsync(code, (int)duration.TotalSeconds + 30, Ct);
            if (result.Error is not null || result.Value is not { ValueKind: JsonValueKind.Array } values)
            {
                Fail($"The getter snippet failed: {result.Error ?? "it returned no list"}");
                return [];
            }
            return values.EnumerateArray().Select(v => v.GetDouble()).ToList();
        }
        catch (ControlException e)
        {
            Fail($"The getter snippet failed with {e.Code}: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Follows every log and event from the run's start through a <c>logs</c> cursor: it fails the run on a <c>bridge.error</c>, a death,
    /// a Game Host panic or a Script error, and ends it on a disconnect, a relogin or the Game Host exiting.
    /// </summary>
    private async Task WatchAsync(CancellationToken stop)
    {
        string? cursor = null;
        while (!stop.IsCancellationRequested)
        {
            LogPage page;
            try
            {
                page = await _connection.LogsAsync(LogKind.All, cursor, 1000, stop);
            }
            catch (Exception e) when (e is OperationCanceledException or ControlException or ObjectDisposedException)
            {
                return;
            }
            if (page.Gap && cursor is not null)
                Note("the logs have a gap: entries were evicted before this run read them");
            foreach (LogEntryDto entry in page.Entries)
                Observe(entry);
            cursor = page.Next;
            if (page.Entries.Count == 0)
            {
                try
                {
                    await Task.Delay(PollInterval, stop);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Observe(LogEntryDto entry)
    {
        switch (entry.Kind, entry.Type)
        {
            case (LogKind.Script, _):
                lock (_scriptLineTimes)
                    _scriptLineTimes.Add(entry.Ts);
                break;
            case (LogKind.Debug, _) when LiveMetrics.ParseStats(entry.Ts, entry.Text!) is { } stats:
                lock (_stats)
                    _stats.Add(stats);
                break;
            case (LogKind.Debug, _) when entry.Text!.Contains("panicked", StringComparison.Ordinal):
                Fail($"The Game Host panicked: {entry.Text}");
                break;
            case (LogKind.Events, EventTypes.BridgeError):
                Fail($"bridge.error: {entry.Data}");
                break;
            case (LogKind.Events, EventTypes.PlayerDeath):
                Fail($"player.death: {entry.Data}");
                break;
            case (LogKind.Events, EventTypes.ScriptError):
                Fail($"script.error: {entry.Data}");
                break;
            case (LogKind.Events, EventTypes.ScriptStopped) when _scriptExpected && !_stoppingScript:
                Fail($"The Script stopped on its own: {entry.Data}");
                break;
            case (LogKind.Events, EventTypes.QuestionRaised or EventTypes.NoticeShown):
                Note($"{entry.Type}: {Cut(entry.Data.ToString()!, 300)}");
                break;
            case (LogKind.Events, EventTypes.GameDisconnected) when _loggingOut:
                // The run's own logout, at its end, which the watcher can still read.
                break;
            case (LogKind.Events, EventTypes.GameDisconnected or EventTypes.GameRelogin or EventTypes.GameHostExited):
                EndFromWatcher($"{entry.Type}: {entry.Data}");
                break;
        }
    }

    /// <summary>Ends the run at once and stops the Script, so Core's auto-relogin never logs in a second time.</summary>
    private void EndFromWatcher(string reason)
    {
        Fail(reason);
        if (_fatal.IsCancellationRequested)
            return;
        _fatal.Cancel();
        _stoppingScript = true;
        _ = _connection.ScriptStopAsync(CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Stops the Script, logs out, and on failure saves a screenshot, <c>logs(all)</c> and the Engine's log files.</summary>
    private async Task EndAsync()
    {
        try
        {
            if (_scriptExpected)
                await StopScriptAsync();
            if (_loggedIn && !Fatal.IsCancellationRequested)
            {
                _loggingOut = true;
                await _connection.LogoutAsync(Ct);
            }
        }
        catch (Exception e) when (e is ControlException or OperationCanceledException)
        {
            Fail($"Ending the run threw {e.GetType().Name}: {e.Message}");
        }

        if (_failures.Count > 0)
            await SaveFailureAsync();
        Note(_failures.Count == 0 ? "PASS" : $"FAIL: {_failures.Count} failures");
        WriteSummary();
    }

    private async Task SaveFailureAsync()
    {
        try
        {
            ScreenshotResult shot = await _connection.ScreenshotAsync(cancellationToken: Ct);
            await File.WriteAllBytesAsync(Path.Combine(_options.OutDir, "failure-screenshot.png"), shot.Png, Ct);
        }
        catch (ControlException e)
        {
            Note($"no failure screenshot: {e.Code} {e.Message}");
        }
        catch (TimeoutException)
        {
            Note("no failure screenshot: it timed out");
        }

        // logs(all) from the run's start: the Engine started with the run, so its first entry is the start.
        await using (StreamWriter logs = new(Path.Combine(_options.OutDir, "logs-all.jsonl")))
        {
            string? cursor = null;
            try
            {
                while (true)
                {
                    LogPage page = await _connection.LogsAsync(LogKind.All, cursor, 1000, Ct);
                    if (page.Gap)
                        Note("logs-all.jsonl misses entries the rings no longer hold; engine-logs has every entry");
                    foreach (LogEntryDto entry in page.Entries)
                        await logs.WriteLineAsync(JsonSerializer.Serialize(entry, ControlJson.Options));
                    cursor = page.Next;
                    if (page.Entries.Count == 0)
                        break;
                }
            }
            catch (ControlException e)
            {
                Note($"logs(all) stopped early: {e.Code} {e.Message}");
            }
        }

        // The rings hold the last 10k entries of each kind; the Engine's JSONL files hold everything.
        string engineLogs = Directory.CreateDirectory(Path.Combine(_options.OutDir, "engine-logs")).FullName;
        if (Directory.Exists(_sandbox.Endpoint.LogFilesDir))
            foreach (string file in Directory.EnumerateFiles(_sandbox.Endpoint.LogFilesDir))
                File.Copy(file, Path.Combine(engineLogs, Path.GetFileName(file)), overwrite: true);
    }

    private void WriteSummary()
    {
        foreach (IGrouping<string, MinuteSample> phase in _samples.GroupBy(s => s.Phase))
        {
            List<MinuteSample> judged = phase.Skip(1).ToList();
            double[] footprints = phase.Select(s => s.GameHost.FootprintMb ?? double.NaN).ToArray();
            double worstP99 = judged.Select(s => LiveMetrics.Percentile(s.GetterMs, 0.99)).DefaultIfEmpty(double.NaN).Max();
            Note($"summary {phase.Key}: {phase.Count()} samples; Game Host footprint {footprints.Min():0}–{footprints.Max():0} MB, " +
                 $"RSS {phase.Min(s => s.GameHost.RssMb ?? double.NaN):0}–{phase.Max(s => s.GameHost.RssMb ?? double.NaN):0} MB; " +
                 $"Engine footprint up to {phase.Max(s => s.Engine.FootprintMb ?? double.NaN):0} MB; worst getter p99 {worstP99:0.00} ms");
        }
        foreach (string failure in _failures)
            _report.WriteLine("FAIL " + failure);
    }

    private static string Describe(MinuteSample s) => string.Create(CultureInfo.InvariantCulture,
        $"{s.Phase} m{s.Minute}: gamehost fp={s.GameHost.FootprintMb:0} rss={s.GameHost.RssMb:0} MB; engine fp={s.Engine.FootprintMb:0} rss={s.Engine.RssMb:0} MB; " +
        $"getter n={s.GetterMs.Count} p50={LiveMetrics.Percentile(s.GetterMs, 0.5):0.000} p99={LiveMetrics.Percentile(s.GetterMs, 0.99):0.000} " +
        $"max={s.GetterMs.DefaultIfEmpty(double.NaN).Max():0.0} ms; script lines={s.ScriptLines}; " +
        $"player level={s.Player?.Level} gold={s.Player?.Gold} map={s.Player?.Map} combat={s.Player?.InCombat}; load={s.Load:0.00}; locked={s.Locked}; display asleep={s.DisplayAsleep}");

    /// <summary>Waits for an event from the run's start that matches.</summary>
    public async Task<LogEntryDto> WaitForEventAsync(string type, TimeSpan timeout, Func<LogEntryDto, bool>? match = null)
    {
        string? cursor = null;
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < timeout)
        {
            LogPage page = await _connection.LogsAsync(LogKind.Events, cursor, 1000, Ct);
            if (page.Entries.FirstOrDefault(e => e.Type == type && (match?.Invoke(e) ?? true)) is { } found)
                return found;
            cursor = page.Next;
            if (page.Entries.Count == 0)
                await Task.Delay(100, Ct);
        }
        throw End($"No {type} event within {timeout.TotalSeconds:0} s.");
    }

    public void Note(string line)
    {
        lock (_report)
            _report.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} +{_elapsed.Elapsed:hh\\:mm\\:ss} {line}");
    }

    public void Fail(string failure)
    {
        lock (_failures)
            _failures.Add(failure);
        Note("FAIL " + failure);
    }

    private LiveRunEnded End(string failure)
    {
        Fail(failure);
        return new LiveRunEnded();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>Ends a scenario early; the reason is already among the failures.</summary>
    private sealed class LiveRunEnded : Exception;
}
