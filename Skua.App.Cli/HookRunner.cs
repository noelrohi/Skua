using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua hooks</c>, the Hook Runner: it follows the events of the Engines in its data folder over <c>subscribe</c>, and for each event runs the
/// Hook named after its type, <c>&lt;SkuaDIR&gt;/hooks/&lt;type&gt;</c>, with the event's JSON on stdin; then records the run on the Engine with
/// <c>hook_ran</c>. It holds no policy: each Hook decides what to do, and each run is a process of its own, so a slow Hook holds up nothing.
/// </summary>
internal sealed class HookRunner
{
    /// <summary>The Engine Name a Hook runs for; <c>SKUA_DIR</c> and <c>SKUA_ENGINE_SOCKET</c> point <c>skua</c> at the same Engine.</summary>
    public const string EngineNameVariable = "SKUA_ENGINE_NAME";

    /// <summary>The newest output of a run that its <c>hook.ran</c> keeps.</summary>
    private const int OutputTailChars = 4096;

    /// <summary>How often the runner looks for Engines that started; their events themselves are pushed.</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly string _skuaDir;
    private readonly string _hooksDir;
    private readonly string? _onlyEngine;
    private readonly bool _json;
    private readonly ConcurrentDictionary<string, Task> _following = new();

    /// <summary>Where each Engine's events were read up to, so one that reconnects reads on from there, or from its start after a restart.</summary>
    private readonly ConcurrentDictionary<string, string> _cursors = new();

    /// <summary>The Engines of another protocol, by name and pid, said once and then left alone.</summary>
    private readonly ConcurrentDictionary<(string, int), byte> _mismatched = new();

    private readonly object _output = new();

    private HookRunner(string skuaDir, string? onlyEngine, bool json)
    {
        _skuaDir = skuaDir;
        _hooksDir = HooksDir(skuaDir);
        _onlyEngine = onlyEngine;
        _json = json;
    }

    public static string HooksDir(string skuaDir) => Path.Combine(skuaDir, "hooks");

    /// <summary>The lock a Hook Runner holds while it runs; one data folder has one runner.</summary>
    public static string LockPath(string skuaDir) => Path.Combine(skuaDir, "hooks.lock");

    public static async Task<int> RunAsync(bool json, string? onlyEngine, CancellationToken cancellationToken)
    {
        string skuaDir = EngineEndpoint.DefaultSkuaDir();
        Directory.CreateDirectory(skuaDir);
        using EngineLock? held = EngineLock.TryAcquire(LockPath(skuaDir));
        if (held is null)
        {
            Console.Error.WriteLine($"skua: a Hook Runner already runs for {skuaDir}; it holds {LockPath(skuaDir)}.");
            return ExitCodes.Failure;
        }

        HookRunner runner = new(skuaDir, onlyEngine, json);
        runner.Say($"Running the hooks in {runner._hooksDir} for {(onlyEngine is null ? "every Engine" : $"Engine '{onlyEngine}'")}; ctrl-c stops.");
        try
        {
            bool first = true;
            while (true)
            {
                foreach (string name in runner.EngineNames())
                {
                    if (!(runner._following.TryGetValue(name, out Task? following) && !following.IsCompleted))
                        await runner.TryFollowAsync(name, fromNow: first, cancellationToken);
                }
                first = false;
                await Task.Delay(ScanInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }
    }

    private IEnumerable<string> EngineNames() =>
        _onlyEngine is not null ? [_onlyEngine] : EngineEndpoint.InDataFolder(_skuaDir).Select(endpoint => endpoint.Name);

    /// <summary>
    /// Connects to the Engine and follows it in the background until it goes away. An Engine already running as the runner starts is
    /// followed from now, so no Hook runs for its past; one that starts later is followed from its start.
    /// </summary>
    private async Task TryFollowAsync(string name, bool fromNow, CancellationToken cancellationToken)
    {
        EngineEndpoint endpoint = EngineEndpoint.Resolve(name, _skuaDir);
        EngineConnection? connection = await EngineClient.TryConnectAsync(endpoint, cancellationToken);
        if (connection is null)
            return;
        if (!connection.IsCompatible)
        {
            if (_mismatched.TryAdd((name, connection.Hello.Pid), 0))
                Say($"Engine '{name}' speaks protocol {connection.Hello.Protocol}, not {ControlProtocol.Version}; no hooks run for it.");
            connection.Dispose();
            return;
        }

        string? after;
        try
        {
            after = _cursors.TryGetValue(name, out string? cursor) ? cursor
                : fromNow ? (await connection.LogsAsync(LogKind.Events, null, null, 1, cancellationToken)).Next
                : null;
        }
        catch (ControlException)
        {
            connection.Dispose();
            return;
        }
        _following[name] = FollowAsync(name, endpoint, connection, after, cancellationToken);
    }

    private async Task FollowAsync(string name, EngineEndpoint endpoint, EngineConnection connection, string? after, CancellationToken cancellationToken)
    {
        Say($"Following Engine '{name}' (pid {connection.Hello.Pid}).");
        try
        {
            await foreach (LogPage page in connection.SubscribeAsync([LogKind.Events], after, cancellationToken))
            {
                foreach (LogEntryDto entry in page.Entries)
                {
                    if (entry.Type is { } type && type != EventTypes.HookRan && HookFor(type) is { } hook)
                        _ = RunHookAsync(name, endpoint, connection, hook, entry);
                }
                _cursors[name] = page.Next;
            }
        }
        catch (ControlException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            // A run still going then can't record itself, and says so.
            connection.Dispose();
        }
        Say($"Engine '{name}' went away.");
    }

    /// <summary>The Hook for an event type: an executable file named after it. Anything else, or nothing, runs nothing.</summary>
    private string? HookFor(string type)
    {
        if (type.IndexOfAny(['/', '\0']) >= 0 || type.StartsWith('.'))
            return null;
        string path = Path.Combine(_hooksDir, type);
        if (!File.Exists(path))
            return null;
        const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & Executable) != 0 ? path : null;
    }

    private async Task RunHookAsync(string name, EngineEndpoint endpoint, EngineConnection connection, string hook, LogEntryDto entry)
    {
        OutputTail output = new();
        long startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Stopwatch watch = Stopwatch.StartNew();
        int? exitCode;
        try
        {
            ProcessStartInfo start = new(hook)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _skuaDir,
            };
            start.Environment[EngineEndpoint.SkuaDirVariable] = _skuaDir;
            start.Environment[EngineEndpoint.SocketVariable] = endpoint.SocketPath;
            start.Environment[EngineNameVariable] = name;
            using Process process = new() { StartInfo = start };
            process.OutputDataReceived += (_, line) => output.Add(line.Data);
            process.ErrorDataReceived += (_, line) => output.Add(line.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(entry, ControlJson.Options));
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The Hook exited, or closed stdin, without reading the event.
            }
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            exitCode = null;
            output.Add($"couldn't run {hook}: {e.Message}");
        }

        HookRunDto run = new(entry.Type!, entry.Seq, startedAt, watch.ElapsedMilliseconds, exitCode, output.ToString());
        if (_json)
            Report(JsonSerializer.Serialize(new { engine = name, run }, ControlJson.Options));
        else
            Report($"{DateTimeOffset.FromUnixTimeMilliseconds(startedAt).LocalDateTime:HH:mm:ss} {name} {run.Hook} (seq {run.EventSeq}): "
                + $"{(exitCode is { } code ? $"exit {code}" : "didn't start")} in {run.DurationMs / 1000.0:0.0}s");
        try
        {
            await connection.HookRanAsync(run);
        }
        catch (Exception e) when (e is ControlException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"skua: couldn't record the run of {run.Hook} on Engine '{name}': {e.Message}");
        }
    }

    /// <summary>Says what the runner does: on stdout, or on stderr with <c>--json</c> so stdout holds only the runs.</summary>
    private void Say(string line)
    {
        if (_json)
            Console.Error.WriteLine($"skua: {line}");
        else
            Report(line);
    }

    /// <summary>Prints a run on stdout, whole, whichever run finishes.</summary>
    private void Report(string line)
    {
        lock (_output)
            Console.WriteLine(line);
    }

    /// <summary>The newest lines of a run's stdout and stderr, as they arrive.</summary>
    private sealed class OutputTail
    {
        private readonly StringBuilder _text = new();

        public void Add(string? line)
        {
            if (line is null)
                return;
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (_text.Length > OutputTailChars)
                    _text.Remove(0, _text.Length - OutputTailChars);
            }
        }

        public override string ToString()
        {
            lock (_text)
                return _text.ToString();
        }
    }
}
