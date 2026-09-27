using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua script start --follow</c>: prints the run's Script log lines and events until it ends and, when stdin is a terminal, asks the
/// developer each of its Questions and answers with their choice. On a terminal a live status line of the game's progress stays under the log.
/// <c>skua watch</c> shows the same for every run from the current one on, without asking, until interrupted.
/// </summary>
internal sealed class ScriptFollow
{
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    /// <summary>Returns the cursor to the start of the line and erases the line.</summary>
    private const string ClearLine = "\r\u001b[2K";

    private readonly EngineConnection _connection;
    private readonly bool _json;

    /// <summary>The followed run, or null when watching every run from <see cref="_firstRun"/> on.</summary>
    private readonly int? _run;

    private readonly int _firstRun;

    /// <summary>Whether a live status line stays under the log: only on a terminal, and never with <c>--json</c>.</summary>
    private readonly bool _live;

    private readonly ProgressView _progress = new();

    /// <summary>The status line to draw, once the first <c>status</c> has arrived.</summary>
    private string? _statusLine;

    /// <summary>Whether the status line is the last thing on the terminal.</summary>
    private bool _statusShown;

    /// <summary>The run's pending Questions by id, as their events described them.</summary>
    private readonly SortedDictionary<int, FollowedQuestion> _pending = [];

    private bool _interactive;

    /// <summary>The Question the developer is being asked, if any.</summary>
    private int? _asking;

    /// <summary>Whether the prompt for <see cref="_asking"/> is the last thing on the terminal.</summary>
    private bool _promptShown;

    private ScriptFollow(EngineConnection connection, bool json, bool interactive, int? run, int firstRun)
    {
        _connection = connection;
        _json = json;
        _interactive = interactive;
        _run = run;
        _firstRun = firstRun;
        _live = !json && !Console.IsOutputRedirected;
    }

    /// <summary>
    /// Starts the run, then follows it until it ends: exit 0 when it completed or was stopped, 1 when it failed. Interrupting the follow
    /// leaves the Script running.
    /// </summary>
    public static async Task<int> RunAsync(bool json, Func<EngineConnection, Task<ScriptStartResult>> start, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(Cli.Options(), cancellationToken);
            ScriptStartResult started = await start(connection);
            Console.WriteLine(json ? JsonSerializer.Serialize(started, ControlJson.Options) : Output.ScriptStart(started));
            return await new ScriptFollow(connection, json, !Console.IsInputRedirected, started.Run, started.Run).FollowAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("skua: stopped following; the Script still runs, and 'skua script stop' stops it.");
            return ExitCodes.Success;
        }
        catch (ControlException e)
        {
            return Cli.Fail(json, e);
        }
    }

    /// <summary>
    /// Shows the log of the run in progress (or of the next one) and of every later run under the live status line, until interrupted;
    /// Questions are only reported. For <c>skua watch</c> on a terminal.
    /// </summary>
    /// <exception cref="OperationCanceledException">When interrupted, which leaves the Script running.</exception>
    public static async Task<int> WatchAsync(EngineConnection connection, CancellationToken cancellationToken)
    {
        ScriptStatusDto script = (await connection.StatusAsync(cancellationToken)).Script;
        int firstRun = script.Run?.Number ?? (script.LastRun?.Number ?? 0) + 1;
        return await new ScriptFollow(connection, json: false, interactive: false, run: null, firstRun).FollowAsync(cancellationToken);
    }

    private async Task<int> FollowAsync(CancellationToken cancellationToken)
    {
        Channel<object?> inbox = Channel.CreateUnbounded<object?>(new UnboundedChannelOptions { SingleReader = true });
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PumpLogsAsync(inbox.Writer, stop.Token);
        if (_live)
            _ = PumpStatusAsync(inbox.Writer, stop.Token);
        if (_interactive)
            _ = Task.Run(() => PumpInput(inbox.Writer), CancellationToken.None);

        try
        {
            await foreach (object? item in inbox.Reader.ReadAllAsync(cancellationToken))
            {
                switch (item)
                {
                    case LogPage page:
                        // The log replays from the oldest entry held, so earlier runs' entries are skipped.
                        foreach (LogEntryDto entry in page.Entries.Where(e => _run is { } run ? e.Run == run : e.Run >= _firstRun))
                        {
                            if (Show(entry) is { } exitCode)
                                return exitCode;
                        }
                        break;
                    case StatusDto status:
                        _statusLine = ProgressView.Line(_progress.Read(status));
                        DrawStatus();
                        break;
                    case string line:
                        await AnswerAsync(line, cancellationToken);
                        break;
                    case null:
                        _interactive = false;
                        break;
                    case Exception e:
                        ExceptionDispatchInfo.Throw(e);
                        break;
                }
                PromptNext();
            }
            return ExitCodes.Success;
        }
        finally
        {
            stop.Cancel();
            // The last status stays on its own line.
            if (_statusShown)
                Console.WriteLine();
        }
    }

    /// <summary>Prints an entry of the run; returns the exit code once the followed run has ended.</summary>
    private int? Show(LogEntryDto entry)
    {
        if (_json)
            Console.WriteLine(JsonSerializer.Serialize(entry, ControlJson.Options));
        if (entry.Kind != LogKind.Events)
        {
            if (!_json)
                WriteLine(entry.Text);
            return null;
        }

        if (entry.Data is not { } data)
        {
            // Too large to keep its data.
            if (!_json)
                WriteLine(Output.Entry(entry));
            return null;
        }
        string? human = null;
        int? exitCode = null;
        switch (entry.Type)
        {
            case EventTypes.NoticeShown:
                human = $"Notice '{Field(data, "caption")}': {Field(data, "text")}";
                break;
            case EventTypes.QuestionRaised:
                FollowedQuestion asked = new(data.GetProperty("id").GetInt32(), Field(data, "caption"), Field(data, "text"),
                    data.GetProperty("choices").EnumerateArray().Select(c => c.GetString() ?? "").ToList());
                // A Question of cancel mode is answered as it is raised; its answer follows at once.
                _pending[asked.Id] = asked;
                human = $"Question {asked.Id} '{asked.Caption}': {asked.Text} ({string.Join(" / ", asked.Choices)})"
                    + (_interactive ? "" : $"; answer it with 'skua dialogs answer {asked.Id} <choice>'");
                break;
            case EventTypes.QuestionAnswered:
                int id = data.GetProperty("id").GetInt32();
                _pending.Remove(id);
                if (_asking == id)
                    _asking = null;
                string? choice = data.GetProperty("choice").GetString();
                human = $"Question {id} answered by {Field(data, "answeredBy")}: {choice ?? "the fallback"}";
                break;
            case EventTypes.ScriptStarted:
                if (data.GetProperty("restart").GetBoolean())
                    human = $"Run {entry.Run} restarted by the auto-relogin.";
                // A followed run's start was reported with its number.
                else if (_run is null)
                    human = $"Run {entry.Run} started: {Field(data, "script")}.";
                else
                    return null;
                break;
            case EventTypes.ScriptError:
                human = $"Script error: {Field(data, "error")}";
                break;
            case EventTypes.ScriptStopped:
                string outcome = Field(data, "outcome");
                human = $"Run {entry.Run} {outcome} after {data.GetProperty("durationSec").GetDouble():0.#} s"
                    + (data.TryGetProperty("error", out JsonElement error) ? $": {error.GetString()}." : ".");
                if (_run is not null)
                    exitCode = outcome is "completed" or "stopped" ? ExitCodes.Success : ExitCodes.Failure;
                break;
            default:
                human = Output.Entry(entry);
                break;
        }
        if (!_json)
            WriteLine(human);
        return exitCode;
    }

    /// <summary>Asks the developer the oldest pending Question, again after other output, unless stdin isn't a terminal.</summary>
    private void PromptNext()
    {
        if (!_interactive || _promptShown)
            return;
        if (_asking is not { } id || !_pending.ContainsKey(id))
        {
            if (_pending.Count == 0)
                return;
            _asking = id = _pending.Keys.First();
        }
        Prompt($"Answer Question {id}, {string.Join(", ", _pending[id].Choices.Select((c, i) => $"{i + 1}) {c}"))}: ");
        _promptShown = true;
    }

    private async Task AnswerAsync(string line, CancellationToken cancellationToken)
    {
        _promptShown = false;
        if (_asking is not { } id || !_pending.TryGetValue(id, out FollowedQuestion? asked))
            return;
        string typed = line.Trim();
        string? choice = asked.Choices.FirstOrDefault(c => string.Equals(c, typed, StringComparison.OrdinalIgnoreCase))
            ?? (int.TryParse(typed, out int number) && number >= 1 && number <= asked.Choices.Count ? asked.Choices[number - 1] : null);
        if (choice is null)
        {
            Prompt($"'{typed}' isn't a choice of Question {id}.\n");
            return;
        }
        try
        {
            await _connection.DialogAnswerAsync(id, choice, cancellationToken);
        }
        catch (ControlException e) when (e.Code == ErrorCode.DialogNotPending)
        {
            Prompt($"Question {id} is no longer pending.\n");
        }
        // Its question.answered event reports the answer.
        _pending.Remove(id);
        _asking = null;
    }

    /// <summary>Prints a line of the run, on a line of its own after an open prompt, and above the status line.</summary>
    private void WriteLine(string? text)
    {
        ClearStatus();
        Console.WriteLine(_promptShown ? $"\n{text}" : text);
        _promptShown = false;
        DrawStatus();
    }

    /// <summary>Writes to the terminal: stdout, or stderr with <c>--json</c> so stdout stays JSON.</summary>
    private void Prompt(string text)
    {
        ClearStatus();
        TextWriter writer = _json ? Console.Error : Console.Out;
        writer.Write(text);
        writer.Flush();
    }

    /// <summary>Draws the status line in place of the last one, unless a prompt waits for the developer's answer.</summary>
    private void DrawStatus()
    {
        if (!_live || _statusLine is null || _promptShown)
            return;
        // A line that wraps couldn't be erased in place.
        int width = Console.WindowWidth;
        string line = width > 1 && _statusLine.Length >= width ? _statusLine[..(width - 2)] + "…" : _statusLine;
        Console.Out.Write(ClearLine + line);
        Console.Out.Flush();
        _statusShown = true;
    }

    private void ClearStatus()
    {
        if (!_statusShown)
            return;
        Console.Out.Write(ClearLine);
        _statusShown = false;
    }

    /// <summary>Sends a <c>status</c> reply every second, for the status line; the log's pump reports an Engine that went away.</summary>
    private async Task PumpStatusAsync(ChannelWriter<object?> inbox, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                inbox.TryWrite(await _connection.StatusAsync(cancellationToken));
                await Task.Delay(StatusInterval, cancellationToken);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ControlException)
        {
        }
    }

    private async Task PumpLogsAsync(ChannelWriter<object?> inbox, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (LogPage page in _connection.SubscribeAsync([LogKind.Script, LogKind.Events], null, cancellationToken))
            {
                if (page.Gap)
                    Console.Error.WriteLine($"skua: {Output.GapNotice}");
                inbox.TryWrite(page);
            }
            inbox.TryWrite(new ControlException(ErrorCode.EngineUnavailable, "The Engine stopped sending the run's log."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            inbox.TryWrite(e);
        }
    }

    /// <summary>Reads the developer's lines until stdin closes, which is sent as null.</summary>
    private static void PumpInput(ChannelWriter<object?> inbox)
    {
        while (Console.In.ReadLine() is { } line)
            inbox.TryWrite(line);
        inbox.TryWrite(null);
    }

    private static string Field(JsonElement data, string property) => data.GetProperty(property).ToString();

    private sealed record FollowedQuestion(int Id, string Caption, string Text, IReadOnlyList<string> Choices);
}
