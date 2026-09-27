using Skua.Control;

namespace Skua.App.Engine.Logging;

/// <summary>A stored entry and the size of its JSON.</summary>
internal sealed record LogRecord(LogEntryDto Entry, int Bytes);

/// <summary>The latest entries of one kind, oldest first. Not thread-safe; <see cref="EngineLogs"/> guards it.</summary>
internal sealed class LogRing(int capacity)
{
    private readonly Queue<LogRecord> _records = new(capacity);

    /// <summary>The seq of the newest entry evicted so far, or 0.</summary>
    public long EvictedThrough { get; private set; }

    public void Add(LogRecord record)
    {
        if (_records.Count == capacity)
            EvictedThrough = _records.Dequeue().Entry.Seq;
        _records.Enqueue(record);
    }

    /// <summary>The records with a seq above <paramref name="seq"/>, oldest first.</summary>
    public IEnumerable<LogRecord> After(long seq) => _records.SkipWhile(record => record.Entry.Seq <= seq);
}
