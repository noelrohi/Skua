using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models.Items;
using Skua.Core.Models.Monsters;

namespace Skua.Engine.Game;

/// <summary>
/// The typed state queries and the <c>status</c> player summary: they read the game through Core's Script API, as a Script would, and map
/// Core's models by hand onto the Control Surface DTOs, so the game's wire names never cross the wire.
/// </summary>
internal sealed class GameQueries
{
    private static readonly TimeSpan BankLoadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BankPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long <c>status</c> waits for the player summary before falling back to the last one, since <c>status</c> never fails.</summary>
    private static readonly TimeSpan PlayerTimeout = TimeSpan.FromSeconds(3);

    private readonly IScriptInterface _api;
    private readonly IFlashUtil _flash;
    private readonly GameStateTracker _tracker;
    private readonly GameActionSlot _slot;
    private readonly DropTracker _drops;
    private readonly QuestProgress _questProgress;
    private readonly SemaphoreSlim _bankLoad = new(1, 1);

    /// <summary>Counts the worlds the game has entered; one per login or relogin.</summary>
    private int _world;

    /// <summary>The world whose bank the game server has sent, or 0 for none; worlds count from 1.</summary>
    private int _bankWorld;

    private readonly object _playerLock = new();
    private Task<PlayerReading>? _playerRead;
    private int _playerReadWorld;
    private PlayerReading? _lastPlayer;

    public GameQueries(IScriptInterface api, IFlashUtil flash, GameStateTracker tracker, GameActionSlot slot, QuestProgress questProgress)
    {
        _api = api;
        _questProgress = questProgress;
        _flash = flash;
        _tracker = tracker;
        _slot = slot;
        _drops = new DropTracker(flash, tracker);
        // Each login starts a new world, whose bank the game server hasn't sent yet.
        tracker.Playing += () => Interlocked.Increment(ref _world);
    }

    public async Task<InventoryResult> InventoryAsync(InventoryKind kind, CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("list the items");
        if (kind == InventoryKind.Bank)
            await LoadBankAsync(cancellationToken);
        return await Task.Run(() => kind switch
        {
            InventoryKind.Bank => new InventoryResult(kind, _api.Bank.UsedSlots, _api.Bank.Slots, _api.Bank.Items.Select(ToDto).ToList()),
            InventoryKind.Temp => new InventoryResult(kind, _api.TempInv.Items.Count, null, _api.TempInv.Items.Select(ToTempDto).ToList()),
            InventoryKind.House => new InventoryResult(kind, _api.House.UsedSlots, _api.House.Slots, _api.House.Items.Select(ToDto).ToList()),
            InventoryKind.Inventory => InventoryWithSpaces(),
            _ => throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{kind}' isn't a kind of item store: inventory, bank, temp or house."),
        }, cancellationToken);
    }

    /// <summary>The inventory with its Bag Space, and its Misc Space unless the game has none (before AQW client 5.0).</summary>
    private InventoryResult InventoryWithSpaces()
    {
        IScriptInventory inventory = _api.Inventory;
        int miscSlots = inventory.MiscSlots;
        return new InventoryResult(InventoryKind.Inventory, inventory.UsedSlots, inventory.Slots, inventory.Items.Select(ToDto).ToList(),
            miscSlots > 0 ? inventory.MiscUsedSlots : null, miscSlots > 0 ? miscSlots : null);
    }

    /// <summary>
    /// What the player holds, for <c>script.started</c>: the inventory's and the temporary inventory's items, and the bank's once the game has
    /// it, since loading it takes seconds; null while not playing, before the game has the inventory, or when the game can't be read.
    /// </summary>
    public HeldItemsDto? Held()
    {
        if (_tracker.State != GameState.Playing)
            return null;
        try
        {
            if (!_flash.GetGameObject<bool>("world.myAvatar.invLoaded"))
                return null;
            return new HeldItemsDto(Held(_api.Inventory.Items), Held(_api.TempInv.Items), BankArrived() ? Held(_api.Bank.Items) : null);
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't read what the player holds as the Script starts: {e.Message}");
            return null;
        }

        static List<HeldItemDto> Held(IEnumerable<ItemBase> items) => [.. items.Select(i => new HeldItemDto(i.ID, i.Name, i.Quantity))];
    }

    public Task<QuestsResult> QuestsAsync(QuestFilter filter, CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("list the quests");
        return Task.Run(() => new QuestsResult(filter, _questProgress.Read(filter)), cancellationToken);
    }

    public Task<MapDto> MapAsync(CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("read the map");
        return Task.Run(() => new MapDto(
            _api.Map.Name,
            _api.Map.RoomID,
            _api.Map.Cells,
            (_api.Map.Players ?? []).Select(p => new MapPlayerDto(p.Name ?? "", p.Level, p.Cell ?? "", p.Pad ?? "", p.HP, p.MaxHP, p.AFK)).ToList(),
            _api.Monsters.MapMonsters.Select(m => new MonsterDto(m.ID, m.MapID, m.Name ?? "", m.Cell ?? "", m.HP, m.MaxHP, m.Alive)).ToList()),
            cancellationToken);
    }

    public Task<DropsResult> DropsAsync(CancellationToken cancellationToken)
    {
        _slot.EnsurePlaying("list the drops");
        return Task.FromResult(new DropsResult(_drops.Drops));
    }

    /// <summary>
    /// The player while playing, and how old the reading is in seconds when it isn't fresh. When the game doesn't answer in time it is the
    /// last reading of this login with its age, or none; a read that answers late still becomes the last reading.
    /// </summary>
    public async Task<(PlayerDto? Player, double? AgeSec)> PlayerAsync()
    {
        if (_tracker.State != GameState.Playing)
            return (null, null);
        int world = Volatile.Read(ref _world);
        Task<PlayerReading> read;
        lock (_playerLock)
        {
            // Under load reads queue behind each other in the Game Client, so a status joins the read still running rather than adding one.
            if (_playerRead is not { IsCompleted: false } || _playerReadWorld != world)
            {
                _playerReadWorld = world;
                _playerRead = Task.Run(() => Remember(world, ReadPlayer()));
            }
            read = _playerRead;
        }
        try
        {
            return ((await read.WaitAsync(PlayerTimeout)).Player, null);
        }
        catch (Exception e)
        {
            EngineLog.Write($"Couldn't read the player for status: {e.Message}");
            lock (_playerLock)
                return _lastPlayer is { } last && last.World == world ? (last.Player, Math.Round((DateTimeOffset.UtcNow - last.At).TotalSeconds, 1)) : (null, null);
        }
    }

    private PlayerReading Remember(int world, PlayerDto player)
    {
        PlayerReading reading = new(world, player, DateTimeOffset.UtcNow);
        lock (_playerLock)
        {
            if (world == Volatile.Read(ref _world))
                _lastPlayer = reading;
        }
        return reading;
    }

    private sealed record PlayerReading(int World, PlayerDto Player, DateTimeOffset At);

    private PlayerDto ReadPlayer()
    {
        IScriptPlayer player = _api.Player;
        int state = player.State;
        // The character's name as the game shows it; Core's Username is what was typed on the login screen.
        string? name = _flash.GetGameObject<string>("world.myAvatar.objData.strUsername");
        // Not Core's CurrentClass, which is null while the player is dead.
        string? playerClass = _api.Inventory.Items.Find(i => i is { Equipped: true, Category: ItemCategory.Class })?.Name;
        int xp = player.XP;
        int requiredXp = player.RequiredXP;
        // The game answers an empty monster, map ID 0, without a target.
        Monster? target = player.Target is { MapID: > 0 } monster ? monster : null;
        return new PlayerDto(name ?? player.Username ?? "", player.Level, playerClass, player.Health, player.MaxHealth, player.Mana, player.MaxMana, player.Gold,
            _api.Map.Name, player.Cell, player.Pad, Alive: state > 0, InCombat: state == 2, xp, requiredXp, PlayerDto.Percent(xp, requiredXp), target?.MapID, target?.Name,
            ReadSocial());
    }

    /// <summary>The game's own social options, in one read of its <c>uoPref</c>; null when the game gives none or they don't parse.</summary>
    private SocialDto? ReadSocial()
    {
        try
        {
            if (_flash.GetGameObject<JObject>("uoPref") is not { } pref)
                return null;
            // The game keeps each as a boolean; a 0 or 1 reads as one too.
            bool On(string key) => pref[key]?.ToObject<bool>() ?? false;
            return new SocialDto(On("bGoto"), On("bWhisper"), On("bParty"), On("bFriend"), On("bDuel"), On("bGuild"));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads the bank once per login, since the game has none until it does, and waits for it. It loads as the game itself does,
    /// with <c>getBank</c> over HTTP: the game server no longer answers Core's <c>loadBank</c> packet. <c>getBank</c> needs the
    /// character's data, which the game has once the inventory has loaded. The bank has arrived once it holds at least the login's
    /// bank count of items. That count leaves out AC items, so a bank holding only AC items may be read before they arrive.
    /// </summary>
    private async Task LoadBankAsync(CancellationToken cancellationToken)
    {
        int world = Volatile.Read(ref _world);
        if (Volatile.Read(ref _bankWorld) == world)
            return;
        await _bankLoad.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _bankWorld) == world)
                return;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(BankLoadTimeout);
            try
            {
                await PollAsync(() => _flash.GetGameObject<bool>("world.myAvatar.invLoaded"), timeout.Token);
                // A Script may have loaded it already; asking again could race its transfers, as Core's Bank.Load says. An empty bank,
                // or one of only AC items not yet loaded, holds none.
                if (!await Task.Run(() => BankArrived() && _flash.GetGameObject<int?>("world.bankinfo.BankArray.length") > 0, timeout.Token))
                    await Task.Run(() => _flash.CallGameFunction("getBank"), timeout.Token);
                await PollAsync(BankArrived, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw RpcErrors.Of(ErrorCode.Timeout, $"The game didn't load the bank within {BankLoadTimeout.TotalSeconds:0} s; try again.");
            }
            // A relogin while it loaded makes a new world, which still needs its bank.
            Volatile.Write(ref _bankWorld, world);
        }
        finally
        {
            _bankLoad.Release();
        }
    }

    /// <summary>Whether the bank holds the login's bank count of items; <c>BankArray</c>, unlike <c>items</c>, ignores a search in the game's bank.</summary>
    private bool BankArrived() =>
        _flash.GetGameObject<int?>("world.myAvatar.iBankCount") is int count
        && _flash.GetGameObject<int?>("world.bankinfo.BankArray.length") >= count;

    private static async Task PollAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        while (!await Task.Run(done, cancellationToken))
            await Task.Delay(BankPollInterval, cancellationToken);
    }

    private static ItemDto ToDto(InventoryItem item) =>
        new(item.ID, item.Name, item.Quantity, item.MaxStack, item.CategoryString ?? "", item.Equipped, item.EnhancementLevel);

    /// <summary>A temporary item, which can't be equipped or enhanced.</summary>
    private static ItemDto ToTempDto(ItemBase item) =>
        new(item.ID, item.Name, item.Quantity, item.MaxStack, item.CategoryString ?? "", Equipped: false, EnhancementLevel: null);
}
