using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua script start --follow</c>: prints the run's Script log lines and events until it ends and, when stdin is a terminal, asks the
/// developer each of its Questions and answers with their choice.
/// </summary>
internal sealed class ScriptFollow
{
    private readonly EngineConnection _connection;
    private readonly bool _json;
    private readonly int _run;

    /// <summary>The run's pending Questions by id, as their events described them.</summary>
    private readonly SortedDictionary<int, Asked> _pending = [];

    private bool _interactive;

    /// <summary>The Question the developer is being asked, if any.</summary>
    private int? _asking;

    /// <summary>Whether the prompt for <see cref="_asking"/> is the last thing on the terminal.</summary>
    private bool _promptShown;

    private ScriptFollow(EngineConnection connection, bool json, bool interactive, int run)
    {
        _connection = connection;
        _json = json;
        _interactive = interactive;
        _run = run;
    }

    /// <summary>
    /// Starts the run, then follows it until it ends: exit 0 when it completed or was stopped, 1 when it failed. Interrupting the follow
    /// leaves the Script running.
    /// </summary>
    public static async Task<int> RunAsync(bool json, Func<EngineConnection, Task<ScriptStartResult>> start, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(
                new EngineClientOptions { Endpoint = EngineEndpoint.FromEnvironment() }, cancellationToken);
            ScriptStartResult started = await start(connection);
            Console.WriteLine(json ? JsonSerializer.Serialize(started, ControlJson.Options) : Output.ScriptStart(started));
            return await new ScriptFollow(connection, json, !Console.IsInputRedirected, started.Run).FollowAsync(cancellationToken);
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

    private async Task<int> FollowAsync(CancellationToken cancellationToken)
    {
        Channel<object?> inbox = Channel.CreateUnbounded<object?>(new UnboundedChannelOptions { SingleReader = true });
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = PumpLogsAsync(inbox.Writer, stop.Token);
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
                        foreach (LogEntryDto entry in page.Entries.Where(e => e.Run == _run))
                        {
                            if (Show(entry) is { } exitCode)
                                return exitCode;
                        }
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
        }
    }

    /// <summary>Prints an entry of the run; returns the exit code once the run has ended.</summary>
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
                human = $"Notice '{Text(data, "caption")}': {Text(data, "text")}";
                break;
            case EventTypes.QuestionRaised:
                Asked asked = new(data.GetProperty("id").GetInt32(), Text(data, "caption"), Text(data, "text"),
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
                human = $"Question {id} answered by {Text(data, "answeredBy")}: {choice ?? "the fallback"}";
                break;
            case EventTypes.ScriptStarted:
                // The first start was reported with the run number.
                if (!data.GetProperty("restart").GetBoolean())
                    return null;
                human = $"Run {_run} restarted by the auto-relogin.";
                break;
            case EventTypes.ScriptError:
                human = $"Script error: {Text(data, "error")}";
                break;
            case EventTypes.ScriptStopped:
                string outcome = Text(data, "outcome");
                human = $"Run {_run} {outcome} after {data.GetProperty("durationSec").GetDouble():0.#} s"
                    + (data.TryGetProperty("error", out JsonElement error) ? $": {error.GetString()}." : ".");
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
        if (_asking is not { } id || !_pending.TryGetValue(id, out Asked? asked))
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

    /// <summary>Prints a line of the run, on a line of its own after an open prompt.</summary>
    private void WriteLine(string? text)
    {
        Console.WriteLine(_promptShown ? $"\n{text}" : text);
        _promptShown = false;
    }

    /// <summary>Writes to the terminal: stdout, or stderr with <c>--json</c> so stdout stays JSON.</summary>
    private void Prompt(string text)
    {
        TextWriter writer = _json ? Console.Error : Console.Out;
        writer.Write(text);
        writer.Flush();
    }

    private async Task PumpLogsAsync(ChannelWriter<object?> inbox, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (LogPage page in _connection.SubscribeAsync([LogKind.Script, LogKind.Events], null, cancellationToken))
                inbox.TryWrite(page);
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

    private static string Text(JsonElement data, string property) => data.GetProperty(property).ToString();

    private sealed record Asked(int Id, string Caption, string Text, IReadOnlyList<string> Choices);
}
