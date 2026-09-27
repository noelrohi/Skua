namespace Skua.App.Engine.Logging;

/// <summary>
/// This Engine start's JSONL file: one serialized entry per line, flushed as written, so a post-mortem survives a crash.
/// Opening it deletes all but the newest <see cref="Kept"/> files, counting itself.
/// </summary>
/// <remarks>Not thread-safe; <see cref="EngineLogs"/> writes under its lock.</remarks>
internal sealed class LogFile : IDisposable
{
    public const int Kept = 10;

    private const UnixFileMode UserOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly FileStream _stream;

    private LogFile(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>Starts a file named after the start time, so names sort in start order.</summary>
    public static LogFile Open(string directory, DateTimeOffset started)
    {
        Directory.CreateDirectory(directory, UserOnly | UnixFileMode.UserExecute);
        string path = Path.Combine(directory, $"{started.UtcDateTime:yyyy-MM-dd'T'HH-mm-ss.fff'Z'}.jsonl");

        foreach (string old in Directory.GetFiles(directory, "*.jsonl").Order(StringComparer.Ordinal).SkipLast(Kept - 1))
            File.Delete(old);

        return new LogFile(new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            UnixCreateMode = UserOnly,
        }));
    }

    public void WriteLine(ReadOnlySpan<byte> json)
    {
        _stream.Write(json);
        _stream.WriteByte((byte)'\n');
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();
}
