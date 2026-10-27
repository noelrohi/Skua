using System.CommandLine;
using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua find &lt;text&gt;</c>: the items whose name contains the text in every item store, for "does this account have X". It starts no
/// Engine. <c>--json</c> prints the items as an array, and says on stderr when the bank wasn't searched, so stdout stays the array.
/// </summary>
internal static class FindCommand
{
    public const string BankNotLoaded = "The bank isn't loaded yet, so it wasn't searched; 'skua inventory bank' loads it.";

    public static void AddTo(RootCommand root, Option<bool> json)
    {
        Argument<string> text = new("text") { Description = "Part of an item's name, ignoring case, e.g. sage." };
        Command find = new("find",
            "List the items whose name contains the text in the inventory, the bank, the temporary inventory and the house: store, ID, name and quantity. "
            + "The bank is searched only once the game has loaded it; it starts no Engine.")
        {
            text,
        };
        find.SetAction((parse, ct) => RunAsync(parse.GetValue(json), parse.GetValue(text)!, ct));
        root.Subcommands.Add(find);
    }

    private static async Task<int> RunAsync(bool json, string text, CancellationToken cancellationToken)
    {
        FindResult result;
        try
        {
            using EngineConnection connection = await EngineCommands.ConnectToRunningAsync(Cli.Options(reads: true), cancellationToken);
            result = await connection.FindAsync(text, cancellationToken);
        }
        catch (ControlException e)
        {
            return Cli.Fail(json, e);
        }
        if (!json)
        {
            Console.WriteLine(Human(result));
            return ExitCodes.Success;
        }
        Console.WriteLine(JsonSerializer.Serialize(result.Items, Output.JsonOptions));
        if (!result.BankLoaded)
            Console.Error.WriteLine($"skua: {BankNotLoaded}");
        return ExitCodes.Success;
    }

    public static string Human(FindResult result)
    {
        StringBuilder text = new();
        if (result.Items.Count == 0)
            text.Append($"No item's name contains '{result.Text}'.");
        foreach (FoundItemDto item in result.Items)
            text.AppendLine().Append($"{Output.Name(item.Store),-9}  {item.Id,8}  {item.Name} x{item.Qty}");
        if (!result.BankLoaded)
            text.AppendLine().Append(BankNotLoaded);
        return text.ToString().TrimStart('\n', '\r');
    }
}
