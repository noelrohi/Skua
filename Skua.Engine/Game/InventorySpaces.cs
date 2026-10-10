using Skua.Core.Interfaces;

namespace Skua.Engine.Game;

/// <summary>How many of a Space's slots the inventory's items fill.</summary>
internal readonly record struct SpaceUse(int Used, int Slots)
{
    public bool Full => Used >= Slots;
}

/// <summary>The inventory's Spaces as the game reports them.</summary>
internal static class InventorySpaces
{
    /// <summary>The inventory's Bag Space, or null before the game has the inventory.</summary>
    public static SpaceUse? BagSpace(this IScriptInventory inventory) => Of(inventory.Slots, () => inventory.UsedSlots);

    /// <summary>The inventory's Misc Space, or null before the game has the inventory and in a game without one (before AQW client 5.0).</summary>
    public static SpaceUse? MiscSpace(this IScriptInventory inventory) => Of(inventory.MiscSlots, () => inventory.MiscUsedSlots);

    /// <summary>A Space the game reports no slots for is none: it has no such Space, or no inventory yet.</summary>
    private static SpaceUse? Of(int slots, Func<int> used) => slots > 0 ? new SpaceUse(used(), slots) : null;
}
