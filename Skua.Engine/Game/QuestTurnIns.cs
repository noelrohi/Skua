using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Engine.Logging;

namespace Skua.Engine.Game;

/// <summary>
/// Quest turn-ins and the game server's answers to them, whoever sends them: a Script, <c>quest_complete</c> or the game's own quest window.
/// The game server answers each with <c>ccqr</c>, which the game shows only as a popup; this records it as
/// <see cref="EventTypes.QuestCompleted"/> or <see cref="EventTypes.QuestRejected"/>, with a Script log line for a refusal, and remembers each
/// quest's last refusal for <c>quests</c> until the quest is turned in.
/// </summary>
/// <remarks>What it remembers is the account's: another account logging in starts it afresh.</remarks>
internal sealed class QuestTurnIns
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly EngineLogs _logs;
    private readonly IScriptQuest _quests;
    private readonly IScriptPlayer _player;
    private readonly object _lock = new();
    private readonly Dictionary<int, QuestRejectionDto> _rejections = [];
    private readonly List<(int Id, TaskCompletionSource<QuestCompleteResult> Answer)> _waiting = [];
    private string? _account;

    /// <summary>The quest last sent for turn-in, which a refusal is for, since the game server's refusal names none.</summary>
    private int? _sent;

    public QuestTurnIns(EngineLogs logs, IScriptQuest quests, IScriptPlayer player)
    {
        _logs = logs;
        _quests = quests;
        _player = player;
    }

    /// <summary>The game sent a turn-in of the quest: its <c>tryQuestComplete</c> packet.</summary>
    public void Sent(int id)
    {
        lock (_lock)
            _sent = id;
    }

    /// <summary>The game server's <c>ccqr</c>: <paramref name="id"/> is its <c>QuestID</c>, which only a turn-in carries.</summary>
    public void Answered(int? id, bool completed, string? name, string? reason)
    {
        // Each turn-in is answered once.
        lock (_lock)
            (id, _sent) = (id ?? _sent, null);
        name ??= id is { } known ? _quests.Tree.Find(q => q.ID == known)?.Name : null;
        if (completed)
        {
            _logs.Event(EventTypes.QuestCompleted, new { id, name });
        }
        else
        {
            _logs.Event(EventTypes.QuestRejected, new { id, name, reason });
            string named = name is null ? $"Quest {id}" : $"Quest {id} '{name}'";
            _logs.Write(LogKind.Script, reason is null ? $"{named} wasn't turned in; the game server gave no reason." : $"{named} wasn't turned in: {reason}");
        }
        if (id is not { } quest)
            return;

        string? account = _player.Username;
        List<TaskCompletionSource<QuestCompleteResult>> answered;
        lock (_lock)
        {
            ForAccount(account);
            if (completed)
                _rejections.Remove(quest);
            else
                _rejections[quest] = new QuestRejectionDto(reason, DateTimeOffset.UtcNow);
            answered = _waiting.Where(w => w.Id == quest).Select(w => w.Answer).ToList();
            _waiting.RemoveAll(w => w.Id == quest);
        }
        foreach (TaskCompletionSource<QuestCompleteResult> answer in answered)
            answer.TrySetResult(new QuestCompleteResult(quest, name, completed, completed ? null : reason));
    }

    /// <summary>The game server's refusals of the quests' last turn-ins, by quest, since which they haven't been turned in.</summary>
    public Dictionary<int, QuestRejectionDto> Rejections()
    {
        string? account = _player.Username;
        lock (_lock)
        {
            ForAccount(account);
            return new(_rejections);
        }
    }

    /// <summary>Waits for the game server's answer to a turn-in of the quest; call it before sending the turn-in, so no answer is missed.</summary>
    public async Task<QuestCompleteResult> AnswerAsync(int id, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<QuestCompleteResult> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _waiting.Add((id, answer));
        try
        {
            return await answer.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw RpcErrors.Of(ErrorCode.Timeout,
                $"The game server didn't answer the turn-in of quest {id} within {timeout.TotalSeconds:0} s, as for a quest that isn't accepted; 'skua quests active' lists the accepted ones.");
        }
        finally
        {
            lock (_lock)
                _waiting.RemoveAll(w => w.Answer == answer);
        }
    }

    /// <summary>Forgets the last account's refusals once another account plays; call it under the lock.</summary>
    private void ForAccount(string? account)
    {
        if (account is null || string.Equals(account, _account, StringComparison.OrdinalIgnoreCase))
            return;
        _account = account;
        _rejections.Clear();
    }
}
