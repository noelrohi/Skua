namespace Skua.Engine.Game;

/// <summary>
/// The player's quest packets, whoever sends them: a Script, the Engine or the game's own quest window. It remembers when the last went
/// out, and which accepts the game server refused: it answers each accept with <c>acceptQuest</c>, but refuses one sent too soon after the
/// player's last action with only a <c>Please slow down</c> warning, which names no quest.
/// </summary>
internal sealed class QuestTraffic
{
    /// <summary>How long after an accept went out a <c>Please slow down</c> can still be its refusal.</summary>
    private static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly Dictionary<int, DateTime> _unanswered = [];
    private readonly Dictionary<int, DateTime> _refused = [];

    private DateTime _lastSent;

    /// <summary>When the player last sent a quest packet: an accept, a turn-in or a quest load.</summary>
    public DateTime LastSent
    {
        get
        {
            lock (_lock)
                return _lastSent;
        }
    }

    /// <summary>The game sent a quest packet that isn't an accept: a turn-in or a quest load.</summary>
    public void SentQuestPacket()
    {
        lock (_lock)
            _lastSent = DateTime.UtcNow;
    }

    /// <summary>The game sent an accept of the quest: its <c>acceptQuest</c> packet.</summary>
    public void SentAccept(int id)
    {
        lock (_lock)
        {
            _lastSent = DateTime.UtcNow;
            // An accept no answer or warning followed in time is no longer one a warning can be for.
            foreach (int old in _unanswered.Where(a => _lastSent - a.Value > RefusalWindow).Select(a => a.Key).ToList())
                _unanswered.Remove(old);
            _unanswered[id] = _lastSent;
        }
    }

    /// <summary>The game server's <c>acceptQuest</c> answer to an accept of the quest.</summary>
    public void Answered(int id)
    {
        lock (_lock)
        {
            _unanswered.Remove(id);
            _refused.Remove(id);
        }
    }

    /// <summary>The game server's <c>Please slow down</c>: it refused an action, which may be an accept still unanswered.</summary>
    public void SlowedDown()
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach ((int id, DateTime sent) in _unanswered.Where(a => now - a.Value <= RefusalWindow).ToList())
            {
                _unanswered.Remove(id);
                _refused[id] = now;
            }
        }
    }

    /// <summary>
    /// Takes the quests whose accept was refused at least <paramref name="settled"/> ago and still has no answer: a warning for another
    /// action can come before an accept's answer.
    /// </summary>
    public List<int> TakeRefused(TimeSpan settled)
    {
        DateTime now = DateTime.UtcNow;
        lock (_lock)
        {
            List<int> refused = _refused.Where(r => now - r.Value >= settled).Select(r => r.Key).ToList();
            foreach (int id in refused)
                _refused.Remove(id);
            return refused;
        }
    }

    /// <summary>Forgets the last login's accepts.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _unanswered.Clear();
            _refused.Clear();
        }
    }
}
