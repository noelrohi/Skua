using System.Runtime.CompilerServices;
using System.Text.Json;
using Newtonsoft.Json;
using Skua.Core.Models.Quests;
using Skua.MacOS.GameHost;

namespace Skua.Engine.Tests;

/// <summary>The Bridge's JSON in Flash's enumeration order: what Ruffle gives in the order the server added it, Windows (Flash) gets in hash order (#152).</summary>
public class FlashObjectOrderTests
{
    [Theory]
    // 10238 Wandering Light, as the Mac read it live and as Windows' QuestData.json has it.
    [InlineData(93555, 93556, 93556, 93555)]
    // 10259 Gilded Peace.
    [InlineData(93675, 93676, 93676, 93675)]
    public void A_quests_requirements_come_in_Flashs_order(int firstAdded, int secondAdded, int flashFirst, int flashSecond)
    {
        string items = $"\"{firstAdded}\":{{\"ItemID\":{firstAdded}}},\"{secondAdded}\":{{\"ItemID\":{secondAdded}}}";
        string json = $"{{\"QuestID\":10238,\"oItems\":{{{items}}},\"oReqd\":{{{items}}},\"oRewards\":{{\"itemsS\":{{{items}}}}},"
            + $"\"turnin\":[{{\"ItemID\":{firstAdded},\"iQty\":6}},{{\"ItemID\":{secondAdded},\"iQty\":9}}]}}";

        Quest quest = JsonConvert.DeserializeObject<Quest>(FlashObjectOrder.Apply(json))!;

        Assert.Equal([flashFirst, flashSecond], quest.Requirements.Select(r => r.ID));
        Assert.Equal([flashFirst, flashSecond], quest.AcceptRequirements.Select(r => r.ID));
        Assert.Equal([flashFirst, flashSecond], quest.Rewards.Select(r => r.ID));
        Assert.Equal(firstAdded == flashFirst ? 6 : 9, quest.Requirements[0].Quantity);
    }

    [Fact]
    public void The_rule_gives_Windows_order_for_every_quest_in_QuestData()
    {
        int checkedQuests = 0;
        List<string> wrong = [];
        List<int> unverified = [];
        int notAscending = 0;
        using JsonDocument data = JsonDocument.Parse(File.ReadAllBytes(QuestDataPath()));
        foreach (JsonElement quest in data.RootElement.EnumerateArray())
        {
            foreach (string list in (string[])["Requirements", "AcceptRequirements"])
            {
                int[] windows = [.. quest.GetProperty(list).EnumerateArray().Select(i => i.GetProperty("ItemID").GetInt32())];
                if (windows.Length < 2)
                    continue;
                checkedQuests++;
                // The server mostly adds them in ascending order; where two share a slot, the order they were added in decides.
                if (FlashObjectOrder.Order(windows.Order())!.SequenceEqual(windows))
                    continue;
                notAscending++;
                if (windows.Length > 6)
                    unverified.Add(quest.GetProperty("ID").GetInt32());
                else if (!Permutations(windows).Any(added => FlashObjectOrder.Order(added)!.SequenceEqual(windows)))
                    wrong.Add($"{quest.GetProperty("ID").GetInt32()} {list}: [{string.Join(", ", windows)}]");
            }
        }

        Assert.Empty(wrong);
        Assert.True(checkedQuests > 3000, $"only {checkedQuests} lists checked");
        // Most need no search: added in ascending order, the rule gives Windows' order exactly.
        Assert.True(notAscending < checkedQuests / 20, $"{notAscending} of {checkedQuests} lists weren't added in ascending order");
        // Quests with 7+ requirements, some sharing a slot, the server didn't add in ascending order: too many orders to try.
        Assert.True(unverified.Count < 20, $"{unverified.Count} unverified: {string.Join(", ", unverified)}");
    }

    [Fact]
    public void Colliding_names_are_placed_by_the_order_they_were_added_in()
    {
        // 661 and 669 both hash to slot 1 of 4; the first added keeps it (quest 135 on Windows: 669, 661).
        Assert.Equal([669, 661], FlashObjectOrder.Order([669, 661]));
        Assert.Equal([661, 669], FlashObjectOrder.Order([661, 669]));
    }

    [Theory]
    [InlineData("""{"b":1,"a":2}""")]
    [InlineData("""{"2":1,"x":2,"1":3}""")]
    [InlineData("""{"02":1,"1":2}""")]
    [InlineData("""{"268435457":1,"268435456":2}""")]
    [InlineData("""{"1":1,"2":2,"3":3}""")]
    [InlineData("""[{"7":1}]""")]
    [InlineData("not json {\"1")]
    [InlineData("\"input\"")]
    [InlineData("""{"2":1,"1":2,"2":3}""")]
    public void What_Flash_doesnt_order_by_hash_or_already_is_in_order_comes_back_as_it_was(string json)
    {
        Assert.Same(json, FlashObjectOrder.Apply(json));
    }

    [Fact]
    public void Nested_objects_are_ordered_and_the_rest_is_kept()
    {
        string json = """{"name":"Été ☃","list":[{"93555":"x","93556":"y"}],"n":1.5}""";

        Assert.Equal("""{"name":"Été ☃","list":[{"93556":"y","93555":"x"}],"n":1.5}""", FlashObjectOrder.Apply(json));
    }

    private static IEnumerable<int[]> Permutations(int[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        for (int i = 0; i < items.Length; i++)
            foreach (int[] rest in Permutations([.. items[..i], .. items[(i + 1)..]]))
                yield return [items[i], .. rest];
    }

    private static string QuestDataPath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "Skua.App.WPF", "QuestData.json");
}
