using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models.Auras;

namespace Skua.Core.Services;

/// <summary>
/// Service that monitors aura changes and publishes events.
/// </summary>
public class AuraMonitorService : IAuraMonitorService, IDisposable, IAsyncDisposable
{
    private readonly IScriptSelfAuras _selfAuras;
    private readonly IScriptTargetAuras _targetAuras;
    private readonly Lazy<IScriptPlayer> _player;
    private readonly Timer _pollTimer;
    private readonly SeenAuras _self = new();
    private readonly SeenAuras _target = new();
    private readonly object _lockObject = new();
    /// <summary>Held through a poll, so polls the timer starts while one runs skip, and the seen auras are only touched under it.</summary>
    private readonly object _pollLock = new();
    private bool _disposed;


    public event Action<string, DateTimeOffset, float, float, SubjectType>? AuraActivated;


    public event Action<string, SubjectType>? AuraDeactivated;


    public event Action<string, float, float, SubjectType>? AuraStackChanged;

    public bool IsMonitoring { get; private set; }

    public int SubscriberCount =>
        (AuraActivated?.GetInvocationList().Length ?? 0) +
        (AuraDeactivated?.GetInvocationList().Length ?? 0) +
        (AuraStackChanged?.GetInvocationList().Length ?? 0);

    /// <summary>What the last poll saw of a subject's auras.</summary>
    private sealed class SeenAuras
    {
        /// <summary>The map ID of the monster the auras are of, 0 for none, or null before the first poll; the player's are never a monster's.</summary>
        public int? Monster { get; set; }

        /// <summary>The names of the auras that are active.</summary>
        public HashSet<string> Active { get; } = [];

        /// <summary>The HUD stack count of each aura on the HUD.</summary>
        public Dictionary<string, int> Stacks { get; } = [];

        /// <summary>Takes <paramref name="auras"/> and <paramref name="snapshots"/> as what was seen, without raising anything.</summary>
        public void Seed(int monster, List<Aura> auras, List<AuraSnapshot> snapshots)
        {
            Clear();
            Monster = monster;
            Active.UnionWith(auras.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)));
            foreach (AuraSnapshot snapshot in snapshots.Where(s => !string.IsNullOrEmpty(s.Name)))
                Stacks[snapshot.Name] = snapshot.Stacks;
        }

        public void Clear()
        {
            Monster = null;
            Active.Clear();
            Stacks.Clear();
        }
    }

    public AuraMonitorService(
        IScriptSelfAuras selfAuras,
        IScriptTargetAuras targetAuras,
        Lazy<IScriptPlayer> player)
    {
        _selfAuras = selfAuras;
        _targetAuras = targetAuras;
        _player = player;
        _pollTimer = new Timer(PollAuras, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void EnsureMonitoring(int pollIntervalMs = 100)
    {
        lock (_lockObject)
        {
            switch (IsMonitoring)
            {
                case false when SubscriberCount > 0:
                    IsMonitoring = true;
                    _pollTimer.Change(0, pollIntervalMs);
                    break;
                case true when SubscriberCount == 0:
                    IsMonitoring = false;
                    _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    ClearSeen();
                    break;
            }
        }
    }

    public void StartMonitoring(int pollIntervalMs = 100)
    {
        if (IsMonitoring) return;

        lock (_lockObject)
        {
            IsMonitoring = true;
            _pollTimer.Change(0, pollIntervalMs);
        }
    }

    public void StopMonitoring()
    {
        if (!IsMonitoring) return;

        lock (_lockObject)
        {
            IsMonitoring = false;
            _pollTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void PollAuras(object? state)
    {
        if (_disposed) return;

        if (SubscriberCount == 0)
        {
            StopMonitoring();
            return;
        }

        if (!IsMonitoring || !Monitor.TryEnter(_pollLock)) return;

        try
        {
            Check(_self, _selfAuras.Snapshots, _selfAuras.Auras, SubjectType.Self);
            PollTarget();
        }
        catch
        {
        }
        finally
        {
            Monitor.Exit(_pollLock);
        }
    }

    /// <summary>
    /// Checks the Target's auras, which are the targeted monster's: a poll that finds another monster targeted, or none, takes its auras as seen
    /// without raising anything, so its changes count from its own stacks. The first poll reports the target's auras as coming, as the player's.
    /// </summary>
    private void PollTarget()
    {
        int monster = TargetedMonster();
        List<AuraSnapshot> snapshots = _targetAuras.Snapshots;
        List<Aura> auras = _targetAuras.Auras;
        // The target changed while the poll read its auras, which may be either monster's.
        if (TargetedMonster() != monster)
            return;

        if (_target.Monster is { } seen && seen != monster)
        {
            _target.Seed(monster, auras, snapshots);
            return;
        }
        _target.Monster = monster;
        Check(_target, snapshots, auras, SubjectType.Target);
    }

    private int TargetedMonster() => _player.Value.Target?.MapID ?? 0;

    private void Check(SeenAuras seen, List<AuraSnapshot>? snapshots, List<Aura>? auras, SubjectType subject)
    {
        CheckStacks(seen, snapshots, subject);
        CheckAuras(seen, auras, subject);
    }

    /// <summary>
    /// Raises <see cref="AuraActivated"/> and <see cref="AuraDeactivated"/> as auras come and go. An aura that still has stacks on the HUD
    /// isn't gone, though the game takes it out of its auras when it loses some.
    /// </summary>
    private void CheckAuras(SeenAuras seen, List<Aura>? currentAuras, SubjectType subject)
    {
        if (currentAuras == null) return;

        foreach (Aura aura in currentAuras)
        {
            if (string.IsNullOrEmpty(aura.Name) || !seen.Active.Add(aura.Name)) continue;

            // The aura's effect value, which isn't its stack count.
            AuraActivated?.Invoke(aura.Name, aura.TimeStamp, aura.Duration, aura.Value, subject);
        }

        HashSet<string> currentNames = new(currentAuras.Select(a => a.Name ?? string.Empty));
        foreach (string name in seen.Active.Where(n => !currentNames.Contains(n) && !seen.Stacks.ContainsKey(n)).ToList())
        {
            seen.Active.Remove(name);
            AuraDeactivated?.Invoke(name, subject);
        }
    }

    /// <summary>
    /// Raises <see cref="AuraStackChanged"/> for each aura whose HUD stack count changed since the last poll.
    /// An aura that comes counts from 0 and one that goes counts to 0, as <see cref="IScriptAuras.GetAuraStacks"/> reads them.
    /// </summary>
    private void CheckStacks(SeenAuras seen, List<AuraSnapshot>? snapshots, SubjectType subject)
    {
        if (snapshots == null) return;

        foreach (AuraSnapshot snapshot in snapshots)
        {
            if (string.IsNullOrEmpty(snapshot.Name)) continue;

            int oldStacks = seen.Stacks.GetValueOrDefault(snapshot.Name);
            if (oldStacks == snapshot.Stacks) continue;

            seen.Stacks[snapshot.Name] = snapshot.Stacks;
            AuraStackChanged?.Invoke(snapshot.Name, oldStacks, snapshot.Stacks, subject);
        }

        HashSet<string> currentNames = new(snapshots.Select(s => s.Name));
        foreach (string name in seen.Stacks.Keys.Where(n => !currentNames.Contains(n)).ToList())
        {
            seen.Stacks.Remove(name, out int oldStacks);
            AuraStackChanged?.Invoke(name, oldStacks, 0, subject);
        }
    }

    private void ClearSeen()
    {
        lock (_pollLock)
        {
            _self.Clear();
            _target.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();
        _pollTimer?.Dispose();
        ClearSeen();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    ~AuraMonitorService()
    {
        Dispose();
    }
}
