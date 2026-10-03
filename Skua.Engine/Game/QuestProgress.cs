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
/// without a rise in any of their requirements. A count that falls (a turn-in, or temporary items lost to a relogin) is no progress. It records <see cref="EventTypes.QuestStalled"/>
/// once a run's quests have gone without a rise for the stall time.
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
    private readonly KillCounter _kills;
    private readonly TimeSpan _stallAfter;
    private readonly Timer _poll;
    private readonly object _lock = new();
    private readonly Dictionary<(int Id, bool Temp), Watch> _watches = [];
    private int _polling;
    private string? _account;

    /// <summary>When an accepted quest's unmet requirement last rose, or null when no accepted quest has one.</summary>
    private DateTime? _questsIdleSince;

    /// <summary>The run and idle start <see cref="EventTypes.QuestStalled"/> was last recorded for, so it is recorded once per stall.</summary>
    private (int Run, DateTime Since)? _recordedStall;

    public QuestProgress(IScriptInterface api, GameStateTracker tracker, ScriptRuns runs, EngineLogs logs, KillCounter kills)
    {
        _kills = kills;
        _api = api;
        _tracker = tracker;
        _runs = runs;
        _logs = logs;
        _stallAfter = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(StallVariable), out int sec) && sec > 0 ? sec : 600);
        TimeSpan interval = TimeSpan.FromMilliseconds(
            int.TryParse(Environment.GetEnvironmentVariable(SampleVariable), out int ms) && ms > 0 ? ms : 5_000);
        _poll = new Timer(_ => Poll(), null, interval, interval);
    }

    /// <summary>The quests as <c>quests</c> lists them, read now, with what it has watched of each requirement.</summary>
    public List<QuestDto> Read(QuestFilter filter)
    {
        (List<Quest> quests, Dictionary<int, int> inventory, Dictionary<int, int> temp) = ReadGame();
        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            Observe(quests, inventory, temp, now);
            if (filter == QuestFilter.Active)
                quests = quests.FindAll(q => q.Active);
            return quests.Select(q => ToDto(q, inventory, temp, now)).ToList();
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
            (List<Quest> quests, Dictionary<int, int> inventory, Dictionary<int, int> temp) = ReadGame();
            ScriptRunDto? run = _kills.WithKills(_runs.Status().Run);
            object? stalled;
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                Observe(quests, inventory, temp, now);
                stalled = run is null ? null : Stalled(run, quests, inventory, temp, now);
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

    private (List<Quest> Quests, Dictionary<int, int> Inventory, Dictionary<int, int> Temp) ReadGame()
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
            }
        }
        // What the player has counts toward a requirement from the inventory, or the temporary inventory for a temporary item.
        return (_api.Quests.Tree, Quantities(_api.Inventory.Items), Quantities(_api.TempInv.Items));
    }

    private void Observe(List<Quest> quests, Dictionary<int, int> inventory, Dictionary<int, int> temp, DateTime now)
    {
        foreach (ItemBase requirement in quests.SelectMany(q => q.Requirements))
        {
            int have = Have(requirement, inventory, temp);
            if (_watches.TryGetValue((requirement.ID, requirement.Temp), out Watch? watch))
                watch.Saw(have, now);
            else
                _watches[(requirement.ID, requirement.Temp)] = new Watch(have, now);
        }
        // The newest rise among the requirements of the accepted quests not yet done: while any of them rises, the account is progressing.
        // The rise that meets a requirement counts too, as most 1/1 drops do at once.
        _questsIdleSince = quests
            .Where(q => q.Active && q.Requirements.Any(r => Have(r, inventory, temp) < r.Quantity))
            .SelectMany(q => q.Requirements)
            .Select(r => (DateTime?)_watches[(r.ID, r.Temp)].IdleSince)
            .Max();
    }

    /// <summary>The <see cref="EventTypes.QuestStalled"/> event's data when a run's quests have just stalled, else null.</summary>
    private object? Stalled(ScriptRunDto run, List<Quest> quests, Dictionary<int, int> inventory, Dictionary<int, int> temp, DateTime now)
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
                        .Where(r => Have(r, inventory, temp) < r.Quantity)
                        .Select(r => new { itemId = r.ID, name = r.Name, have = Have(r, inventory, temp), qty = r.Quantity })
                        .ToList(),
                })
                .Where(q => q.requirements.Count > 0)
                .ToList(),
        };
    }

    private double? IdleSec(ScriptRunDto run, DateTime now) =>
        _questsIdleSince is { } since ? Math.Round(Math.Min((now - since).TotalSeconds, run.ElapsedSec), 1) : null;

    private QuestDto ToDto(Quest quest, Dictionary<int, int> inventory, Dictionary<int, int> temp, DateTime now)
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
                return new QuestRequirementDto(r.ID, r.Name, r.Quantity, Have(r, inventory, temp), r.Temp,
                    Math.Round((now - watch.IdleSince).TotalSeconds, 1), watch.GainPerHour(now));
            }).ToList(),
            quest.Rewards.Select(r => new QuestRewardDto(r.ID, r.Name, r.Quantity)).ToList());
    }

    private static int Have(ItemBase requirement, Dictionary<int, int> inventory, Dictionary<int, int> temp) =>
        (requirement.Temp ? temp : inventory).GetValueOrDefault(requirement.ID);

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
