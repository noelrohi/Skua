using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Skua.MacOS.GameHost;

/// <summary>
/// Puts the integer-keyed objects in the Game Client's JSON in the order Flash Player enumerates them, such as a quest's <c>oItems</c>,
/// which Scripts pair with their monsters by position (#152).
/// </summary>
/// <remarks>
/// Ruffle enumerates an object's properties in the order they were added; Flash Player (avmplus <c>InlineHashtable</c>) in the order of
/// its hash table's slots. An integer name goes to slot <c>name mod capacity</c>; a taken slot probes on by 8, then 9, 10, ... slots.
/// The capacity is a power of two that doubles before an add would fill three quarters of it, rehashing the entries in slot order. So
/// from the order Ruffle keeps, the order Flash gives follows. A string name hashes by its address, so an object with one is left as is.
/// </remarks>
public static class FlashObjectOrder
{
    /// <summary>Flash keeps an integer name as an int atom only below 2^28 (29-bit atoms); a larger one is a string.</summary>
    private const long MaxIntKey = (1 << 28) - 1;

    private static readonly JsonSerializerOptions Write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Returns <paramref name="json"/> with each object whose names are all integers in Flash's order; the same string when nothing moves
    /// or it isn't JSON.
    /// </summary>
    public static string Apply(string json)
    {
        if (!HasIntegerName(json))
            return json;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
        return root is not null && Reorder(root) ? root.ToJsonString(Write) : json;
    }

    /// <summary>
    /// The integer names in Flash's enumeration order, given the order they were added in; null if a probe never finds a free slot, which
    /// its sequence doesn't promise.
    /// </summary>
    public static List<int>? Order(IEnumerable<int> added)
    {
        int?[]? slots = new int?[4];
        int count = 0;
        foreach (int key in added)
        {
            if (4 * (count + 1) >= 3 * slots.Length && (slots = Rehash(slots, slots.Length * 2)) is null)
                return null;
            if (Find(slots, key) is not int i)
                return null;
            slots[i] = key;
            count++;
        }
        return [.. slots.OfType<int>()];
    }

    private static int?[]? Rehash(int?[] slots, int capacity)
    {
        int?[] grown = new int?[capacity];
        foreach (int key in slots.OfType<int>())
        {
            if (Find(grown, key) is not int i)
                return null;
            grown[i] = key;
        }
        return grown;
    }

    private static int? Find(int?[] slots, int key)
    {
        int mask = slots.Length - 1;
        int i = key & mask;
        // The offsets repeat with a period of twice the capacity.
        for (int step = 8; step < 8 + 2 * slots.Length; step++)
        {
            if (slots[i] is not int taken || taken == key)
                return i;
            i = (i + step) & mask;
        }
        return null;
    }

    /// <summary>A cheap screen: an object whose first name starts with a digit.</summary>
    private static bool HasIntegerName(string json)
    {
        for (int i = json.IndexOf("{\"", StringComparison.Ordinal); i >= 0; i = json.IndexOf("{\"", i + 2, StringComparison.Ordinal))
            if (i + 2 < json.Length && char.IsAsciiDigit(json[i + 2]))
                return true;
        return false;
    }

    private static bool Reorder(JsonNode node)
    {
        bool moved = false;
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                moved |= item is not null && Reorder(item);
            return moved;
        }
        if (node is not JsonObject obj)
            return false;
        foreach (KeyValuePair<string, JsonNode?> property in obj)
            moved |= property.Value is not null && Reorder(property.Value);

        if (obj.Count < 2 || !obj.All(p => IsIntKey(p.Key)))
            return moved;
        List<int> added = [.. obj.Select(p => int.Parse(p.Key))];
        if (Order(added) is not List<int> order || order.SequenceEqual(added))
            return moved;
        List<KeyValuePair<string, JsonNode?>> properties = [.. obj];
        obj.Clear();
        Dictionary<int, KeyValuePair<string, JsonNode?>> byKey = properties.ToDictionary(p => int.Parse(p.Key));
        foreach (int key in order)
            obj.Add(byKey[key]);
        return true;
    }

    /// <summary>Whether Flash stores the name as an int atom: a canonical decimal integer in its range.</summary>
    private static bool IsIntKey(string name) =>
        name.Length is > 0 and <= 9 && name.All(char.IsAsciiDigit) && (name == "0" || name[0] != '0') && long.Parse(name) <= MaxIntKey;
}
