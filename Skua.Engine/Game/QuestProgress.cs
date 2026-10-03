using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Models.Quests;
using Skua.Engine.Logging;
using Skua.Engine.Scripts;

namespace Skua.Engine.Game;

/// <summary>
/// Watches the loaded quests' requirements while playing, so a Control Surface can tell how long an account has gone without
/// progress: when each requirement's count last rose and how fast it rises, and how long a run's accepted quests not yet done have gone
/// without a rise in any of their requirements. A requirement's count is what the player owns of it: the inventory and bank's, or the
/// temporary inventory's for a temporary item. So banking an item is no progress, and a count that falls (a turn-in, or temporary items
/// lost to a relogin) is none either. It records <see cref="EventTypes.QuestStalled"/> once a run's quests have gone without a rise for
/// the stall time.
/// </summary>
/// <remarks>What it remembers is the account's: another account logging in starts it afresh.</remarks>
internal sealed class QuestProgress : IDisposable
{
    /// <summary>How often it reads the quests while playing: 5000 ms unless set.</summary>
    public const string SampleVariable = "SKUA_QUEST_SAMPLE_MS";

    /// <summary>How long a run's quests go without a rise before <see cref="EventTypes.QuestStalled"/>: 600 s unless set.</summary>
    public const string StallVariable = "SKUA_QUEST_STALL_SEC";

    /// <summary>The window <see cref="QuestRequirementDto.GainPerHour"/> counts over, and how long it watches before it says.</summary>
    private static readonly TimeSpan RateWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan RateMinimum = TimeSpan.FromMinutes(5);

    private readonly IScriptInterface _api;
    private readonly GameStateTracker _tracker;
    private readonly ScriptRuns _runs;
    private readonly EngineLogs _logs;
    private readonly CombatTally _tally;
    private readonly ScriptGoal _goal;
    private readonly TimeSpan _stallAfter;
    private readonly Timer _poll;
    private readonly object _lock = new();
    private readonly Dictionary<(int Id, bool Temp), Watch> _watches = [];
    private int _polling;
    private string? _account;

    /// <summary>Whether this login's bank has arrived: the game has none until a Script or <c>inventory</c> loads it.</summary>
    private bool _bankSeen;

    /// <summary>When an accepted quest's unmet requirement last rose, or null when no accepted quest has one.</summary>
    private DateTime? _questsIdleSince;

    /// <summary>The run and idle start <see cref="EventTypes.QuestStalled"/> was last recorded for, so it is recorded once per stall.</summary>
    private (int Run, DateTime Since)? _recordedStall;

    public QuestProgress(IScriptInterface api, GameStateTracker tracker, ScriptRuns runs, EngineLogs logs, CombatTally tally, ScriptGoal goal)
    {
        _goal = goal;
        _tally = tally;
        _api = api;
        _tracker = tracker;
        _runs = runs;
        _logs = logs;
        _stallAfter = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(StallVariable), out int sec) && sec > 0 ? sec : 600);
        TimeSpan interval = TimeSpan.FromMilliseconds(
            int.TryParse(Environment.GetEnvironmentVariable(SampleVariable), out int ms) && ms > 0 ? ms : 5_000);
        _poll = new Timer(_ => Poll(), null, interval, interval);
        // Each login starts with no bank again.
        tracker.Playing += () =>
        {
            lock (_lock)
                _bankSeen = false;
        };
    }

    /// <summary>The quests as <c>quests</c> lists them, read now, with what it has watched of each requirement.</summary>
    public List<QuestDto> Read(QuestFilter filter)
    {
        (List<Quest> quests, Stores stores) = ReadGame();
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            Observe(quests, stores, now);
            if (filter == QuestFilter.Active)
                quests = quests.FindAll(q => q.Active);
            return quests.Select(q => ToDto(q, stores, now)).ToList();
        }
    }

    /// <summary><paramref name="run"/> with how long its accepted quests have gone without a rise, at most as long as it has run.</summary>
    public ScriptRunDto? WithQuestIdle(ScriptRunDto? run)
    {
        if (run is null)
            return null;
        lock (_lock)
            return run with { QuestIdleSec = IdleSec(run, DateTime.UtcNow) };
    }

    public void Dispose() => _poll.Dispose();

    private void Poll()
    {
        // A slow Game Host can hold a read past the next poll.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
            return;
        try
        {
            if (_tracker.State != GameState.Playing)
                return;
            (List<Quest> quests, Stores stores) = ReadGame();
            ScriptRunDto? run = _tally.WithTally(_runs.Status().Run);
            object? stalled;
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                Observe(quests, stores, now);
                stalled = run is null ? null : Stalled(run, quests, stores, now);
            }
            if (stalled is not null)
                _logs.Event(EventTypes.QuestStalled, stalled);
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't read the quests' progress: {e.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private (List<Quest> Quests, Stores Stores) ReadGame()
    {
        string? account = _api.Player.Username;
        lock (_lock)
        {
            if (account is not null && !string.Equals(account, _account, StringComparison.OrdinalIgnoreCase))
            {
                _account = account;
                _watches.Clear();
                _questsIdleSince = null;
                _recordedStall = null;
                _bankSeen = false;
            }
        }
        List<InventoryItem> inventory = _api.Inventory.Items;
        List<ItemBase> temp = _api.TempInv.Items;
        List<InventoryItem> bank = _api.Bank.Items;
        // What the player owns by name, for the Script's goal, which names its items.
        _goal.Sample(inventory.Cast<ItemBase>().Concat(temp).Concat(bank)
            .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.OrdinalIgnoreCase));
        return (_api.Quests.Tree, new Stores(Quantities(inventory), Quantities(temp), Quantities(bank)));
    }

    private void Observe(List<Quest> quests, Stores stores, DateTime now)
    {
        // The bank arriving after the login is no rise: what it holds was owned already.
        bool bankArrived = !_bankSeen && stores.Bank.Count > 0;
        _bankSeen |= bankArrived;
        foreach (ItemBase requirement in quests.SelectMany(q => q.Requirements))
        {
            int owned = stores.Owned(requirement);
            if (!_watches.TryGetValue((requirement.ID, requirement.Temp), out Watch? watch))
                _watches[(requirement.ID, requirement.Temp)] = new Watch(owned, now);
            else if (bankArrived)
                watch.Rebase(owned);
            else
                watch.Saw(owned, now);
        }
        // The newest rise among the requirements of the accepted quests not yet done: while any of them rises, the account is progressing.
        // The rise that meets a requirement counts too, as most 1/1 drops do at once.
        _questsIdleSince = quests
            .Where(q => q.Active && q.Requirements.Any(r => stores.Owned(r) < r.Quantity))
            .SelectMany(q => q.Requirements)
            .Select(r => (DateTime?)_watches[(r.ID, r.Temp)].IdleSince)
            .Max();
    }

    /// <summary>The <see cref="EventTypes.QuestStalled"/> event's data when a run's quests have just stalled, else null.</summary>
    private object? Stalled(ScriptRunDto run, List<Quest> quests, Stores stores, DateTime now)
    {
        if (_questsIdleSince is not { } since || IdleSec(run, now) is not { } idleSec || idleSec < _stallAfter.TotalSeconds
            || _recordedStall == (run.Number, since))
            return null;
        _recordedStall = (run.Number, since);
        return new
        {
            run = run.Number,
            script = run.Script,
            idleSec,
            killsPerMin = run.KillsPerMin,
            quests = quests
                .Where(q => q.Active)
                .Select(q => new
                {
                    id = q.ID,
                    name = q.Name,
                    requirements = q.Requirements
                        .Where(r => stores.Owned(r) < r.Quantity)
                        .Select(r => new { itemId = r.ID, name = r.Name, have = stores.Have(r), inBank = stores.InBank(r), qty = r.Quantity })
                        .ToList(),
                })
                .Where(q => q.requirements.Count > 0)
                .ToList(),
        };
    }

    private double? IdleSec(ScriptRunDto run, DateTime now) =>
        _questsIdleSince is { } since ? Math.Round(Math.Min((now - since).TotalSeconds, run.ElapsedSec), 1) : null;

    private QuestDto ToDto(Quest quest, Stores stores, DateTime now)
    {
        QuestStatus status = quest.Status switch
        {
            null => QuestStatus.NotAccepted,
            "c" => QuestStatus.Completable,
            _ => QuestStatus.InProgress,
        };
        return new QuestDto(quest.ID, quest.Name, status, quest.Upgrade, quest.Gold, quest.XP,
            quest.Requirements.Select(r =>
            {
                Watch watch = _watches[(r.ID, r.Temp)];
                return new QuestRequirementDto(r.ID, r.Name, r.Quantity, stores.Have(r), r.Temp,
                    Math.Round((now - watch.IdleSince).TotalSeconds, 1), watch.GainPerHour(now), stores.InBank(r));
            }).ToList(),
            quest.Rewards.Select(r => new QuestRewardDto(r.ID, r.Name, r.Quantity)).ToList());
    }

    /// <summary>What the player holds, by item ID: the bank's only once the game has loaded it.</summary>
    private sealed record Stores(Dictionary<int, int> Inventory, Dictionary<int, int> Temp, Dictionary<int, int> Bank)
    {
        /// <summary>What counts toward the turn-in: the inventory's, or the temporary inventory's for a temporary item.</summary>
        public int Have(ItemBase requirement) => (requirement.Temp ? Temp : Inventory).GetValueOrDefault(requirement.ID);

        /// <summary>The bank's, which a Script takes out for the turn-in; temporary items are never banked.</summary>
        public int InBank(ItemBase requirement) => requirement.Temp ? 0 : Bank.GetValueOrDefault(requirement.ID);

        public int Owned(ItemBase requirement) => Have(requirement) + InBank(requirement);
    }

    private static Dictionary<int, int> Quantities(IEnumerable<ItemBase> items) =>
        items.GroupBy(i => i.ID).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    /// <summary>One requirement's count as the Engine has seen it.</summary>
    private sealed class Watch(int have, DateTime now)
    {
        private readonly DateTime _since = now;
        private readonly Queue<(DateTime At, int Gain)> _gains = new();
        private int _have = have;

        /// <summary>When the count last rose, or when the watch began if it hasn't.</summary>
        public DateTime IdleSince { get; private set; } = now;

        /// <summary>Takes <paramref name="have"/> as the count without calling it a rise or a fall.</summary>
        public void Rebase(int have) => _have = have;

        public void Saw(int have, DateTime now)
        {
            if (have > _have)
            {
                IdleSince = now;
                _gains.Enqueue((now, have - _have));
            }
            _have = have;
            while (_gains.TryPeek(out var gain) && now - gain.At > RateWindow)
                _gains.Dequeue();
        }

        /// <summary>How much the count rose per hour over the last hour watched; null until it has watched for <see cref="RateMinimum"/>.</summary>
        public double? GainPerHour(DateTime now)
        {
            TimeSpan watched = now - _since;
            if (watched < RateMinimum)
                return null;
            TimeSpan window = watched < RateWindow ? watched : RateWindow;
            int gained = _gains.Where(g => now - g.At <= window).Sum(g => g.Gain);
            return Math.Round(gained / window.TotalHours, 1);
        }
    }
}
