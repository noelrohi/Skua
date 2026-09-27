using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Skua.Control;

/// <summary>A generic password's attributes, which Keychain gives without asking for access.</summary>
/// <param name="Comment">Its comment, or null when it has none.</param>
public sealed record KeychainAttributes(string Account, string? Comment);

/// <summary>A generic password in Keychain. Never log or return <see cref="Password"/>.</summary>
public sealed record KeychainItem(string Account, string Password)
{
    public override string ToString() => $"KeychainItem {{ Account = {Account} }}";
}

/// <summary>
/// Generic passwords in the user's Keychain, through macOS's <c>security</c> tool. A password reaches <c>security</c> only on its standard input,
/// never on a command line, where <c>ps</c> would show it.
/// </summary>
public static partial class Keychain
{
    /// <summary>Runs another <c>security</c> tool instead of macOS's, for tests.</summary>
    public const string ToolVariable = "SKUA_SECURITY_TOOL";

    /// <summary><c>security</c>'s exit code when no item matches.</summary>
    private const int ItemNotFound = 44;

    /// <summary>
    /// The account and comment of the generic password under <paramref name="service"/>, or null when there is none. It doesn't read the
    /// password, so macOS never asks for access.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when <c>security</c> fails.</exception>
    public static async Task<KeychainAttributes?> FindAsync(string service, CancellationToken cancellationToken)
    {
        (int exitCode, string attributes, string error) = await RunAsync(["find-generic-password", "-s", service], null, cancellationToken);
        return exitCode switch
        {
            0 => new KeychainAttributes(Value(AccountLine().Match(attributes)), Value(CommentLine().Match(attributes)) is { Length: > 0 } comment ? comment : null),
            ItemNotFound => null,
            _ => throw Failed(exitCode, error),
        };
    }

    /// <summary>
    /// Reads the generic password under <paramref name="service"/> with <c>security find-generic-password -g</c>, or returns null when there is none;
    /// its account is the username. macOS may ask once whether <c>security</c> may read it.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when <c>security</c> fails.</exception>
    public static async Task<KeychainItem?> ReadAsync(string service, CancellationToken cancellationToken)
    {
        // The attributes come on stdout and the password line on stderr; on a failure stderr carries only security's own message.
        (int exitCode, string attributes, string passwordLine) = await RunAsync(["find-generic-password", "-s", service, "-g"], null, cancellationToken);
        return exitCode switch
        {
            0 => new KeychainItem(Value(AccountLine().Match(attributes)), Value(PasswordLine().Match(passwordLine))),
            ItemNotFound => null,
            _ => throw Failed(exitCode, passwordLine),
        };
    }

    /// <summary>Adds the generic password under <paramref name="service"/>, or replaces the one there.</summary>
    /// <param name="label">The name Keychain Access shows for it.</param>
    /// <param name="comment">Its comment; empty for none.</param>
    /// <exception cref="ControlException">
    /// <see cref="ErrorCode.InvalidArgument"/> for an empty password or a line break or NUL in a value, and <see cref="ErrorCode.KeychainFailed"/> when <c>security</c> fails.
    /// </exception>
    public static async Task AddAsync(string service, string account, string label, string comment, string password, CancellationToken cancellationToken)
    {
        if (password.Length == 0)
            throw new ControlException(ErrorCode.InvalidArgument, "A Keychain password can't be empty.");
        // security's interactive mode reads the command from stdin, with quoting as a shell's.
        string quoted = Quote(password);
        // The comment is always given, so a replaced item doesn't keep the old one.
        string command = $"add-generic-password -U -s {Quote(service)} -a {Quote(account)} -l {Quote(label)} -j {Quote(comment)} -w {quoted}\n";
        (int exitCode, _, string error) = await RunAsync(["-i"], command, cancellationToken);
        // security may echo the command it read, quoted or not.
        if (exitCode != 0)
            throw Failed(exitCode, error.Replace(quoted[1..^1], "[redacted]", StringComparison.Ordinal).Replace(password, "[redacted]", StringComparison.Ordinal));
    }

    /// <summary>Deletes the generic password under <paramref name="service"/>; returns false when there was none.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when <c>security</c> fails.</exception>
    public static async Task<bool> DeleteAsync(string service, CancellationToken cancellationToken)
    {
        (int exitCode, _, string error) = await RunAsync(["delete-generic-password", "-s", service], null, cancellationToken);
        return exitCode switch
        {
            0 => true,
            ItemNotFound => false,
            _ => throw Failed(exitCode, error),
        };
    }

    /// <summary>A double-quoted argument for security's interactive mode, which unescapes a backslashed character as a shell does.</summary>
    private static string Quote(string value)
    {
        if (value.IndexOfAny(['\n', '\r', '\0']) >= 0)
            throw new ControlException(ErrorCode.InvalidArgument, "A Keychain value can't hold a line break or a NUL.");
        StringBuilder quoted = new("\"");
        foreach (char c in value)
            quoted.Append(c is '\\' or '"' or '$' or '`' ? $"\\{c}" : c);
        return quoted.Append('"').ToString();
    }

    private static ControlException Failed(int exitCode, string error) =>
        new(ErrorCode.KeychainFailed, $"security exited with {exitCode}: {error.Trim()}");

    private static string Value(Match match) =>
        !match.Success ? ""
        : match.Groups["text"].Success ? match.Groups["text"].Value
        : Encoding.UTF8.GetString(Convert.FromHexString(match.Groups["hex"].Value)).TrimEnd('\0');

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string[] arguments, string? input, CancellationToken cancellationToken)
    {
        string tool = Environment.GetEnvironmentVariable(ToolVariable) is { Length: > 0 } path ? path : "/usr/bin/security";
        ProcessStartInfo startInfo = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)!;
        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }
    }

    // An attribute or the password is quoted text, or hex followed by its escaped text when it isn't printable.
    [GeneratedRegex("^\\s*\"acct\"<blob>=(?:\"(?<text>.*)\"|0x(?<hex>[0-9A-Fa-f]+)\\b.*)$", RegexOptions.Multiline)]
    private static partial Regex AccountLine();

    [GeneratedRegex("^\\s*\"icmt\"<blob>=(?:\"(?<text>.*)\"|0x(?<hex>[0-9A-Fa-f]+)\\b.*)$", RegexOptions.Multiline)]
    private static partial Regex CommentLine();

    [GeneratedRegex("^password: (?:\"(?<text>.*)\"|0x(?<hex>[0-9A-Fa-f]+)\\b.*)$", RegexOptions.Multiline)]
    private static partial Regex PasswordLine();
}
