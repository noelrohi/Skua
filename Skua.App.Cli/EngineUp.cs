using System.CommandLine;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>What <c>skua engine up</c> did for one Engine.</summary>
/// <param name="Started">Whether this command started its Engine, or replaced one from another build.</param>
/// <param name="Login">The login, as <c>skua login</c> gives it; null when it failed.</param>
/// <param name="Error">Why the Engine isn't up; null when it is.</param>
public sealed record EngineUpResult(string Name, bool Started, LoginResult? Login, EngineUpError? Error);

public sealed record EngineUpError(ErrorCode Code, string Message);

/// <summary>
/// <c>skua engine up [&lt;name&gt;...]</c>: starts a windowless Engine for each name that has none running and logs it in as its own account, the
/// one named after it, as skua-tui's <c>L</c> does; with no names, every Skua Manager account. The Engines come up in parallel, and one that
/// fails doesn't stop the others. An Engine already playing as its account is left alone.
/// </summary>
internal static class EngineUp
{
    public static Command Command(Option<bool> json, Option<string?> engineName)
    {
        Argument<string[]> names = new("name")
        {
            Description = "The Engine Names, each also the name of the account it logs in (a Skua Manager account's); by default every Skua Manager account.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<string?> server = new("--server")
        {
            Description = "The server to log every Engine in on, from 'skua servers'; without it, each Engine picks an online, non-member server with room.",
        };
        Option<int?> timeout = new("--timeout") { Description = "Seconds each login waits for the world: 120 by default." };
        Command up = new("up",
            "Start a windowless Engine for each account that has none running and log it in as that account, in parallel; one line per Engine, "
            + "and a non-zero exit when any failed. An Engine already playing as its account is left alone.")
        {
            names, server, timeout,
        };
        up.Validators.Add(result =>
        {
            foreach (string name in result.GetValue(names) ?? [])
            {
                if (!EngineName.IsValid(name))
                    result.AddError($"Engine Name '{name}' is invalid; it must match [a-z0-9-]{{1,16}}.");
            }
            if (result.GetValue(engineName) is not null)
                result.AddError("'engine up' takes the Engines as its arguments, not --engine.");
        });
        up.SetAction((parse, ct) => Cli.RunAsync(parse.GetValue(json),
            _ => RunAsync(parse.GetValue(names) ?? [], parse.GetValue(server), parse.GetValue(timeout), ct),
            Output.EngineUp, results => results.Any(r => r.Error is not null) ? ExitCodes.Failure : ExitCodes.Success));
        return up;
    }

    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when no names are given and the Skua Manager has no accounts.</exception>
    public static async Task<IReadOnlyList<EngineUpResult>> RunAsync(IReadOnlyList<string> names, string? server, int? timeoutSec, CancellationToken cancellationToken)
    {
        string skuaDir = EngineEndpoint.DefaultSkuaDir();
        if (names.Count == 0)
            names = [.. new ManagerAccounts(skuaDir).Accounts.Select(account => account.Name)];
        if (names.Count == 0)
            throw new ControlException(ErrorCode.InvalidArgument,
                "The Skua Manager has no accounts; name the Engines to bring up, each after its account: 'skua engine up alt1 alt2'.");
        return await Task.WhenAll(names.Distinct().Select(name => UpAsync(EngineEndpoint.Resolve(name, skuaDir), server, timeoutSec, cancellationToken)));
    }

    private static async Task<EngineUpResult> UpAsync(EngineEndpoint endpoint, string? server, int? timeoutSec, CancellationToken cancellationToken)
    {
        string name = endpoint.Name;
        bool started = false;
        try
        {
            // Read without the password; an Engine started for an account Keychain lacks would only sit at the login screen.
            if (await Keychain.FindAsync(Accounts.ServiceOf(name), cancellationToken) is null)
                throw new ControlException(ErrorCode.AccountNotFound,
                    $"No account is named '{name}' in Keychain, so no Engine was started: name one of the Skua Manager's accounts, or add it with 'skua account add --name {name}'.");
            int? before;
            using (EngineConnection? running = await EngineClient.TryConnectAsync(endpoint, cancellationToken))
                before = running?.Hello.Pid;
            using EngineConnection connection = await EngineClient.ConnectAsync(Cli.Options(endpoint: endpoint), cancellationToken);
            started = connection.Hello.Pid != before;
            // An Engine's own account is the one named after it, as skua-tui's L logs it in.
            LoginResult login = await connection.LoginAsync(server, timeoutSec, name, cancellationToken);
            return new EngineUpResult(name, started, login, null);
        }
        catch (ControlException e)
        {
            return new EngineUpResult(name, started, null, new EngineUpError(e.Code, e.Message));
        }
    }
}
