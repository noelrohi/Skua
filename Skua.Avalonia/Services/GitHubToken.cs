using System.ComponentModel;
using System.Diagnostics;
using Skua.Control;
using Skua.Core.Utils;

namespace Skua.Avalonia.Services;

/// <summary>
/// The GitHub token of the device-flow sign-in (<c>GitHubAuthViewModel</c>), which raises the Script Source's rate limits. The Mac App keeps it
/// in Keychain, as a generic password under <see cref="Service"/>, where Windows keeps it in <c>Skua.settings.json</c>. It is never logged.
/// </summary>
public sealed class GitHubToken
{
    /// <summary>The Keychain service the token is stored under.</summary>
    public const string Service = "skua-github-token";

    /// <summary>The settings key Core saves the token under; <see cref="AppSettingsService"/> sends it here instead.</summary>
    public const string SettingKey = "UserGitHubToken";

    private const string Account = "github";

    private volatile string? _token;

    /// <summary>The token signed in with, or null before a sign-in or a <see cref="LoadAsync"/> that found one.</summary>
    public string? Token => _token;

    /// <summary>
    /// Reads the token from Keychain, if there is one, and makes Core's GitHub requests use it, as the Windows client does with the saved one at
    /// its start. It looks first without reading the password, so macOS asks for access only when there is a token to read.
    /// </summary>
    /// <returns>Whether a token was found.</returns>
    public async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await Keychain.FindAsync(Service, cancellationToken) is null || await Keychain.ReadAsync(Service, cancellationToken) is not { Password.Length: > 0 } item)
                return false;
            _token = item.Password;
            HttpClients.UserGitHubClient = new WebClient(item.Password);
            return true;
        }
        catch (Exception e) when (e is ControlException or Win32Exception)
        {
            Trace.WriteLine($"Couldn't read the GitHub token from Keychain: {e.Message}");
            return false;
        }
    }

    /// <summary>Stores <paramref name="token"/> in Keychain, replacing any there; an empty one removes it.</summary>
    /// <remarks>Runs the security tool off the calling thread, which may be the UI thread, whose context the awaits mustn't need.</remarks>
    public void Save(string? token)
    {
        _token = string.IsNullOrEmpty(token) ? null : token;
        try
        {
            if (string.IsNullOrEmpty(token))
                Task.Run(() => Keychain.DeleteAsync(Service, CancellationToken.None)).GetAwaiter().GetResult();
            else
                Task.Run(() => Keychain.AddAsync(Service, Account, "Skua GitHub token", "", token, CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is ControlException or Win32Exception)
        {
            // Keychain's message never holds the password it was given.
            Trace.WriteLine($"Couldn't save the GitHub token to Keychain: {e.Message}");
        }
    }
}
