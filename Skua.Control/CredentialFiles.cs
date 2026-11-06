using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skua.Control;

/// <summary>
/// The generic passwords that <see cref="Keychain"/> keeps on Linux, which has no Keychain: one JSON file per service, mode 0600, in a
/// folder of mode 0700 (<c>&lt;SkuaDIR&gt;/credentials</c>). Unlike a secret service, it needs no desktop session, so cron can log in.
/// </summary>
/// <remarks>Each file is written whole and moved into place, so a reader never sees half of one.</remarks>
public sealed partial class CredentialFiles
{
    private const UnixFileMode FolderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ItemMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    /// <param name="directory">The folder the files live in; it is made, with mode 0700, on the first add.</param>
    public CredentialFiles(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    /// <summary>The account and comment under <paramref name="service"/>, or null when there is none.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when the file can't be read.</exception>
    public KeychainAttributes? Find(string service) =>
        Load(service) is { } item ? new KeychainAttributes(item.Account, item.Comment is { Length: > 0 } comment ? comment : null) : null;

    /// <summary>The account and password under <paramref name="service"/>, or null when there is none.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when the file can't be read.</exception>
    public KeychainItem? Read(string service) =>
        Load(service) is { } item ? new KeychainItem(item.Account, item.Password) : null;

    /// <summary>Adds the item under <paramref name="service"/>, or replaces the one there.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when the file can't be written.</exception>
    public void Add(string service, string account, string comment, string password)
    {
        string path = PathOf(service);
        string temporary = Path.Combine(Directory, $".{service}.{Environment.ProcessId}.{Guid.NewGuid():N}");
        try
        {
            System.IO.Directory.CreateDirectory(Directory, FolderMode);
            // An older folder may be wider; the passwords' folder is the owner's alone.
            File.SetUnixFileMode(Directory, FolderMode);
            using (FileStream stream = new(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = ItemMode }))
                JsonSerializer.Serialize(stream, new Item(account, comment, password), _json);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception) when (e is IOException or UnauthorizedAccessException)
            {
            }
            throw Failed(path, e);
        }
    }

    /// <summary>Deletes the item under <paramref name="service"/>; returns false when there was none.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.KeychainFailed"/> when the file can't be deleted.</exception>
    public bool Delete(string service)
    {
        string path = PathOf(service);
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Failed(path, e);
        }
    }

    private Item? Load(string service)
    {
        string path = PathOf(service);
        try
        {
            return JsonSerializer.Deserialize<Item>(File.ReadAllText(path)) is { Account: not null, Password: not null } item
                ? item
                : throw new ControlException(ErrorCode.KeychainFailed, $"{path} has no account or no password.");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw Failed(path, e);
        }
    }

    /// <summary>The file of <paramref name="service"/>, whose name is the service's; a service that isn't a plain file name is refused.</summary>
    private string PathOf(string service) =>
        ServiceName().IsMatch(service)
            ? Path.Combine(Directory, service)
            : throw new ControlException(ErrorCode.InvalidArgument, $"The credential service '{service}' must match [A-Za-z0-9_-][A-Za-z0-9._-]{{0,127}}.");

    private static ControlException Failed(string path, Exception e) => new(ErrorCode.KeychainFailed, $"{path}: {e.Message}");

    private sealed record Item(string Account, string Comment, string Password);

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ServiceName();
}
