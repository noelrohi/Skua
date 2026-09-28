namespace Skua.Control;

/// <summary>
/// The Mac App's command line: <c>Skua [--name &lt;engine-name&gt;] [--account &lt;account&gt; [--server &lt;server&gt;] [--script &lt;path&gt;]]</c>,
/// or <c>Skua --manager</c> for the Skua Manager. The Manager launches the app with it, and the app parses it, so both sides share one spelling.
/// </summary>
/// <remarks>It never carries a password: the app reads the account's from Keychain at its login, as <c>skua login</c> does.</remarks>
/// <param name="Name">The Engine Name the app serves.</param>
/// <param name="Account">
/// The name of the account (<see cref="Accounts"/>) this app's Engine logs in with instead of the Active Account, and which the app logs in once it
/// starts; null for none.
/// </param>
/// <param name="Server">The server that first login picks, or null for the emptiest.</param>
/// <param name="Script">A Script the app starts once that login is playing, or null.</param>
/// <param name="Manager">Whether the process is the Skua Manager, which hosts no Engine.</param>
public sealed record AppArguments(string Name = EngineName.Default, string? Account = null, string? Server = null, string? Script = null, bool Manager = false)
{
    public const string Usage = "usage: Skua [--name <engine-name>] [--account <account> [--server <server>] [--script <path>]] | Skua --manager";

    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> for an unknown option, a missing value or a bad name.</exception>
    public static AppArguments Parse(IReadOnlyList<string> args)
    {
        AppArguments parsed = new();
        for (int i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ControlException(ErrorCode.InvalidArgument, $"{args[i]} needs a value. {Usage}");
            parsed = args[i] switch
            {
                "--name" => parsed with { Name = EngineName.Validate(Value()) },
                "--account" => parsed with { Account = ValidAccount(Value()) },
                "--server" => parsed with { Server = Value() },
                "--script" => parsed with { Script = Value() },
                "--manager" => parsed with { Manager = true },
                _ => throw new ControlException(ErrorCode.InvalidArgument, $"Unknown option '{args[i]}'. {Usage}"),
            };
        }
        if (parsed.Account is null && (parsed.Server ?? parsed.Script) is not null)
            throw new ControlException(ErrorCode.InvalidArgument, $"--server and --script go with --account. {Usage}");
        if (parsed.Manager && parsed != new AppArguments(Manager: true))
            throw new ControlException(ErrorCode.InvalidArgument, $"--manager takes no other option. {Usage}");
        return parsed;
    }

    /// <summary>The arguments that <see cref="Parse"/> reads back as this.</summary>
    public IReadOnlyList<string> ToArgs()
    {
        if (Manager)
            return ["--manager"];
        List<string> args = ["--name", Name];
        if (Account is not null)
            args.AddRange(["--account", Account]);
        if (Server is not null)
            args.AddRange(["--server", Server]);
        if (Script is not null)
            args.AddRange(["--script", Script]);
        return args;
    }

    private static string ValidAccount(string name) => Accounts.IsValidName(name)
        ? name
        : throw new ControlException(ErrorCode.InvalidArgument, $"Account name '{name}' is invalid; it must match [a-z0-9-]{{1,16}}.");
}
