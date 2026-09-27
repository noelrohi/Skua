using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Skua.Control;

namespace Skua.App.Engine.Game;

/// <summary>The Test Account's credentials. Never log or return <see cref="Password"/>.</summary>
internal sealed record TestAccount(string Username, string Password)
{
    /// <summary>The Engine setting (in <c>client</c> of Skua.settings.json) that names the Keychain service holding the Test Account.</summary>
    public const string ServiceSetting = "TestAccountService";

    /// <summary>Runs another <c>security</c> tool instead of macOS's, for tests.</summary>
    public const string ToolVariable = "SKUA_SECURITY_TOOL";

    /// <summary><c>security</c>'s exit code when no item matches.</summary>
    private const int ItemNotFound = 44;

    // An attribute or the password is quoted text, or hex followed by its escaped text when it isn't printable.
    private static readonly Regex AccountLine = new("^\\s*\"acct\"<blob>=(?:\"(?<text>.*)\"|0x(?<hex>[0-9A-Fa-f]+)\\b.*)$", RegexOptions.Multiline);
    private static readonly Regex PasswordLine = new("^password: (?:\"(?<text>.*)\"|0x(?<hex>[0-9A-Fa-f]+)\\b.*)$", RegexOptions.Multiline);

    public override string ToString() => $"TestAccount {{ Username = {Username} }}";

    /// <summary>
    /// Reads the generic password under <paramref name="service"/> from Keychain with <c>security find-generic-password -g</c>;
    /// its account is the username. macOS may ask once whether <c>security</c> may read it.
    /// </summary>
    /// <exception cref="StreamJsonRpc.LocalRpcException"><see cref="ErrorCode.LoginFailed"/> when there is none or it can't be read.</exception>
    public static async Task<TestAccount> ReadAsync(string service, CancellationToken cancellationToken)
    {
        (string attributes, string passwordLine) = await RunAsync(service, cancellationToken);
        string username = Value(AccountLine.Match(attributes));
        string password = Value(PasswordLine.Match(passwordLine));
        if (username.Length == 0 || password.Length == 0)
            throw RpcErrors.Of(ErrorCode.LoginFailed, $"The Keychain item '{service}' has no account or no password; set the Test Account's username as its account.");
        return new TestAccount(username, password);
    }

    private static string Value(Match match) =>
        !match.Success ? ""
        : match.Groups["text"].Success ? match.Groups["text"].Value
        : Encoding.UTF8.GetString(Convert.FromHexString(match.Groups["hex"].Value)).TrimEnd('\0');

    /// <summary>Runs <c>security</c>; returns the item's attributes (stdout) and its password line (stderr).</summary>
    private static async Task<(string Attributes, string Password)> RunAsync(string service, CancellationToken cancellationToken)
    {
        string tool = Environment.GetEnvironmentVariable(ToolVariable) is { Length: > 0 } path ? path : "/usr/bin/security";
        ProcessStartInfo startInfo = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "find-generic-password", "-s", service, "-g" })
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode == ItemNotFound)
                throw RpcErrors.Of(ErrorCode.LoginFailed,
                    $"There is no Test Account in Keychain: add it as a generic password with service '{service}' and the username as its account, " +
                    $"e.g. 'security add-generic-password -s {service} -a <username> -w' (it asks for the password).");
            // On a failure stderr carries only security's own message; the password line comes only on success.
            if (process.ExitCode != 0)
                throw RpcErrors.Of(ErrorCode.LoginFailed,
                    $"Couldn't read the Test Account from Keychain (security exited with {process.ExitCode}: {(await stderr).Trim()}); allow security access when macOS asks.");
            return (await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
    }
}
