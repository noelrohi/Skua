namespace Skua.Control;

/// <summary>
/// The lock an Engine holds on its lock file for its whole lifetime. The kernel releases it when the process dies.
/// </summary>
/// <remarks>
/// On Unix, .NET opens a file with <see cref="FileShare.None"/> by taking <c>flock(LOCK_EX | LOCK_NB)</c> on it,
/// and fails with an <see cref="IOException"/> carrying <c>EWOULDBLOCK</c> when another process holds that lock.
/// </remarks>
public sealed class EngineLock : IDisposable
{
    private static readonly int EWOULDBLOCK = OperatingSystem.IsMacOS() ? 35 : 11;

    private readonly FileStream _file;

    private EngineLock(FileStream file)
    {
        _file = file;
    }

    /// <summary>Takes the exclusive lock, or returns null when another process holds it.</summary>
    public static EngineLock? TryAcquire(string path)
    {
        try
        {
            return new EngineLock(new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }));
        }
        catch (IOException e) when (e.HResult == EWOULDBLOCK)
        {
            return null;
        }
    }

    /// <summary>Whether some process holds the lock right now.</summary>
    public static bool IsHeld(string path)
    {
        if (!File.Exists(path))
            return false;

        using EngineLock? probe = TryAcquire(path);
        return probe is null;
    }

    public void Dispose() => _file.Dispose();
}
