using Skua.Core.Flash;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Models.Items;
using System.Collections.Concurrent;
using System.Dynamic;

namespace Skua.Core.Scripts;

public partial class ScriptInventory : IScriptInventory
{
    private readonly Lazy<IFlashUtil> _lazyFlash;
    private readonly Lazy<IScriptSend> _lazySend;
    private readonly Lazy<IScriptOption> _lazyOptions;
    private readonly Lazy<IScriptWait> _lazyWait;
    private readonly Lazy<IScriptMap> _lazyMap;
    private readonly Lazy<IScriptManager> _lazyManager;
    private readonly Lazy<IScriptPlayer> _lazyPlayer;
    private readonly Lazy<ILogService> _lazyLogger;
    private IFlashUtil Flash => _lazyFlash.Value;
    private IScriptOption Options => _lazyOptions.Value;
    private IScriptWait Wait => _lazyWait.Value;
    private IScriptMap Map => _lazyMap.Value;
    private IScriptManager Manager => _lazyManager.Value;
    private IScriptPlayer Player => _lazyPlayer.Value;
    private IScriptSend Send => _lazySend.Value;
    private ILogService Logger => _lazyLogger.Value;

    public ScriptInventory(
        Lazy<IFlashUtil> flash,
        Lazy<IScriptSend> send,
        Lazy<IScriptOption> options,
        Lazy<IScriptWait> wait,
        Lazy<IScriptMap> map,
        Lazy<IScriptManager> manager,
        Lazy<IScriptPlayer> player,
        Lazy<ILogService> logger)
    {
        _lazyFlash = flash;
        _lazySend = send;
        _lazyOptions = options;
        _lazyWait = wait;
        _lazyMap = map;
        _lazyManager = manager;
        _lazyPlayer = player;
        _lazyLogger = logger;
    }

    [ObjectBinding("world.myAvatar.items", Default = "new()")]
    private List<InventoryItem> _items;

    [ObjectBinding("world.myAvatar.objData.iBagSlots")]
    private int _slots;

    [ObjectBinding("world.myAvatar.items.length")]
    private int _usedSlots;

    /// <summary>How long after its request an equip that hasn't landed counts as failed.</summary>
    private const int EquipFailSeconds = 10;
    private const int EquipCheckSleep = 200;

    /// <summary>The equip of each category still being confirmed; a later one of the category replaces it.</summary>
    private readonly ConcurrentDictionary<ItemCategory, object> _confirming = new();

    public void EquipItem(int id)
    {
        Wait.ForActionCooldown(GameActions.EquipItem);
        dynamic item = new ExpandoObject();
        item.ItemID = id;
        Flash.CallGameFunction("world.sendEquipItemRequest", item);
        if (Wait.ForItemEquip(id) || !((IScriptInventory)this).TryGetItem(id, out InventoryItem? asked) || asked!.Equipped)
            return;
        // The game server can take longer than that wait, so the Script goes on and the equip only counts as failed if it still hasn't
        // landed some seconds later, unless the Script has asked for another item of the category since.
        object attempt = new();
        _confirming[asked.Category] = attempt;
        string map = Map.Name;
        Task.Run(() =>
        {
            // ForItemEquip has waited about a second already.
            bool settled = Wait.ForTrue(() => _confirming.GetValueOrDefault(asked.Category) != attempt || !Player.Playing
                || (((IScriptInventory)this).TryGetItem(id, out InventoryItem? now) && now!.Equipped), (EquipFailSeconds - 1) * 1000 / EquipCheckSleep, EquipCheckSleep);
            _confirming.TryRemove(new(asked.Category, attempt));
            if (!settled)
                Logger.ScriptLog($"Equipping {asked.Name} failed: it still isn't equipped {EquipFailSeconds} s after the request (map {map}).");
        });
    }

    public void EquipUsableItem(InventoryItem? item)
    {
        if (item is null)
            return;
        Wait.ForActionCooldown(GameActions.EquipItem);

        dynamic dynItem = new ExpandoObject();
        dynItem.ItemID = item.ID;
        dynItem.sDesc = item.Description;
        dynItem.sFile = item.FileLink;
        dynItem.sName = item.Name;

        Flash.CallGameFunction("world.equipUseableItem", dynItem);

        Wait.ForItemEquip(item.ID);
    }

    public bool ToBank(InventoryItem item)
    {
        Send.Packet($"%xt%zm%bankFromInv%{Map.RoomID}%{item.ID}%{item.CharItemID}%");
        Wait.ForInventoryToBank(item.Name);
        return !((IScriptInventory)this).Contains(item.Name);
    }

    public bool EnsureToBank(string name)
    {
        if (!((IScriptInventory)this).TryGetItem(name, out InventoryItem? item))
            return false;
        int i = 0;
        while (!ToBank(item!) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return !((IScriptInventory)this).Contains(name);
    }

    public bool EnsureToBank(int id)
    {
        if (!((IScriptInventory)this).TryGetItem(id, out InventoryItem? item))
            return false;
        int i = 0;
        while (!ToBank(item!) && !Manager.ShouldExit && Player.Playing && ++i < Options.MaximumTries)
            Thread.Sleep(Options.ActionDelay);

        return !((IScriptInventory)this).Contains(id);
    }
}