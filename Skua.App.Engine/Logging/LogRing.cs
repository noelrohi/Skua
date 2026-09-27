using Skua.Control;

namespace Skua.App.Engine.Logging;

/// <summary>A stored entry and the size of its JSON.</summary>
internal sealed record LogRecord(LogEntryDto Entry, int Bytes);

/// <summary>The latest entries of one kind, oldest first. Not thread-safe; <see cref="EngineLogs"/> guards it.</summary>
internal sealed class LogRing(int capacity)
{
    private readonly LogRecord[] _records = new LogRecord[capacity];
    private int _oldest;
    private int _count;

    /// <summary>The seq of the newest entry evicted so far, or 0.</summary>
    public long EvictedThrough { get; private set; }

    public void Add(LogRecord record)
    {
        if (_count == capacity)
        {
            EvictedThrough = _records[_oldest].Entry.Seq;
            _records[_oldest] = record;
            _oldest = (_oldest + 1) % capacity;
        }
        else
        {
            _records[(_oldest + _count++) % capacity] = record;
        }
    }

    /// <summary>The records with a seq above <paramref name="seq"/>, oldest first.</summary>
    public IEnumerable<LogRecord> After(long seq)
    {
        // Seqs rise from oldest to newest, so a binary search finds the first one above the cursor.
        int low = 0, high = _count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (At(middle).Entry.Seq <= seq)
                low = middle + 1;
            else
                high = middle;
        }
        for (int i = low; i < _count; i++)
            yield return At(i);
    }

    private LogRecord At(int index) => _records[(_oldest + index) % capacity];
}
