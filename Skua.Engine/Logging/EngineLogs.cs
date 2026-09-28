using System.Text.Json;
using System.Text.Json.Nodes;
using Skua.Control;

namespace Skua.Engine.Logging;

/// <summary>
/// Everything the Engine records: one seq counter across the <c>script</c>, <c>debug</c>, <c>flash</c> and <c>events</c> kinds,
/// a ring of the latest entries per kind, this start's JSONL file, and cursor-based reads.
/// </summary>
/// <remarks>
/// One lock orders every append, so a reader never sees a seq before every lower seq is in place and a cursor never skips an entry.
/// </remarks>
internal sealed class EngineLogs : IDisposable
{
    public const int RingCapacity = 10_000;
    public const int DefaultMax = 200;
    public const int MaxMax = 1000;

    /// <summary>The cap on a <c>logs</c> reply.</summary>
    public const int MaxReplyBytes = 1024 * 1024;

    /// <summary>Room in a reply for the JSON-RPC envelope, <c>next</c> and <c>gap</c>.</summary>
    private const int ReplyOverheadBytes = 1024;

    /// <summary>An entry whose data serializes larger loses its data, so one entry always fits a reply.</summary>
    private const int MaxEntryBytes = 512 * 1024;

    private static readonly LogKind[] AllKinds = [LogKind.Script, LogKind.Debug, LogKind.Flash, LogKind.Events];

    private readonly object _lock = new();
    private readonly LogScrubber _scrubber = new();
    private readonly Dictionary<LogKind, LogRing> _rings = AllKinds.ToDictionary(kind => kind, _ => new LogRing(RingCapacity));
    private LogFile? _file;
    private long _seq;
    private int _run;
    private TaskCompletionSource _appended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="file">This start's JSONL file, or null when it couldn't be opened.</param>
    public EngineLogs(DateTimeOffset started, LogFile? file)
    {
        Epoch = started.ToUnixTimeMilliseconds();
        _file = file;
    }

    /// <summary>Identifies this Engine start in cursors, so a cursor from an earlier start reads as a gap.</summary>
    public long Epoch { get; }

    /// <summary>The Script run that entries recorded from now on belong to, or null outside a run.</summary>
    public int? Run
    {
        get => Volatile.Read(ref _run) is > 0 and int run ? run : null;
        set => Volatile.Write(ref _run, value ?? 0);
    }

    /// <summary>
    /// The redaction hook: every later occurrence of <paramref name="secret"/> is redacted before it is stored, published or written.
    /// </summary>
    public void AddSecret(string secret) => _scrubber.AddSecret(secret);

    /// <summary>Redacts every secret and the login token in <paramref name="text"/>, for text the Engine returns rather than records.</summary>
    public string Scrub(string text) => _scrubber.Redact(text);

    /// <summary>A Script Dialog's message as the Engine returns it: redacted and cut to 64 KB, as its events record it.</summary>
    public string ScrubDialogText(string text) => _scrubber.DialogText(text);

    /// <summary>Records a text entry and returns its text as stored.</summary>
    public string Write(LogKind kind, string text)
    {
        bool truncated = false;
        string stored = _scrubber.Text(text, ref truncated);
        Append(kind, stored, null, null, truncated);
        return stored;
    }

    /// <summary>Records an event whose data is <paramref name="data"/> serialized as a JSON object.</summary>
    public void Event(string type, object data)
    {
        bool truncated = false;
        JsonNode? node = JsonSerializer.SerializeToNode(data, ControlJson.Options);
        _scrubber.Data(node, ref truncated);
        Append(LogKind.Events, null, type, JsonSerializer.SerializeToElement(node, ControlJson.Options), truncated);
    }

    /// <summary>The texts of the held entries of one kind, oldest first.</summary>
    public List<string> Texts(LogKind kind)
    {
        lock (_lock)
            return _rings[kind].After(0).Select(record => record.Entry.Text!).ToList();
    }

    /// <summary>Completes on the next append. Take it before a <see cref="Read"/> that finds nothing, then wait on it.</summary>
    public Task NextAppend
    {
        get
        {
            lock (_lock)
                return _appended.Task;
        }
    }

    /// <summary>
    /// The entries of the given kinds after the cursor (or from the oldest held), merged by seq.
    /// Any requested entry after the cursor that is no longer held sets <see cref="LogPage.Gap"/>.
    /// </summary>
    /// <exception cref="StreamJsonRpc.LocalRpcException"><see cref="ErrorCode.InvalidArgument"/> for a malformed cursor or a max below 1.</exception>
    public LogPage Read(IEnumerable<LogKind> requested, string? after, int? max)
    {
        int limit = Math.Min(max ?? DefaultMax, MaxMax);
        if (limit < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"max must be at least 1, not {max}.");
        LogKind[] kinds = requested.SelectMany(kind => kind == LogKind.All ? AllKinds : [kind]).Distinct().ToArray();
        (long afterSeq, bool restarted) = ParseCursor(after);

        lock (_lock)
        {
            bool gap = restarted || kinds.Any(k => _rings[k].EvictedThrough > afterSeq);
            List<LogEntryDto> entries = [];
            int budget = MaxReplyBytes - ReplyOverheadBytes;
            bool more = false;
            foreach (LogRecord record in Merge(kinds.Select(k => _rings[k].After(afterSeq))))
            {
                budget -= record.Bytes + 1;
                if (entries.Count == limit || budget < 0)
                {
                    more = true;
                    break;
                }
                entries.Add(record.Entry);
            }
            long next = more ? entries[^1].Seq : _seq;
            return new LogPage(entries, Cursor(next), gap);
        }
    }

    /// <summary>Merges seq-ordered sequences into one, lazily, so a page reads only the records it returns.</summary>
    private static IEnumerable<LogRecord> Merge(IEnumerable<IEnumerable<LogRecord>> sequences)
    {
        List<IEnumerator<LogRecord>> heads = [];
        foreach (IEnumerable<LogRecord> sequence in sequences)
        {
            IEnumerator<LogRecord> head = sequence.GetEnumerator();
            if (head.MoveNext())
                heads.Add(head);
        }

        while (heads.Count > 0)
        {
            IEnumerator<LogRecord> first = heads.MinBy(head => head.Current.Entry.Seq)!;
            yield return first.Current;
            if (!first.MoveNext())
                heads.Remove(first);
        }
    }

    private void Append(LogKind kind, string? text, string? type, JsonElement? data, bool truncated)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            LogEntryDto entry = new(++_seq, ts, kind, Run, text, type, data, truncated);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry, ControlJson.Options);
            if (json.Length > MaxEntryBytes)
            {
                entry = entry with { Data = null, Truncated = true };
                json = JsonSerializer.SerializeToUtf8Bytes(entry, ControlJson.Options);
            }

            _rings[kind].Add(new LogRecord(entry, json.Length));
            WriteToFile(json);
            _appended.SetResult();
            _appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Closes the file; entries recorded afterwards stay in memory only.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private void WriteToFile(byte[] json)
    {
        try
        {
            _file?.WriteLine(json);
        }
        catch (IOException e)
        {
            // A full disk mustn't stop the Engine; the rings keep working.
            _file!.Dispose();
            _file = null;
            EngineLog.Echo($"Stopped writing the log file: {e.Message}");
        }
    }

    private string Cursor(long seq) => $"{Epoch}.{seq}";

    /// <summary>Returns the seq to read after, and whether the cursor comes from another Engine start.</summary>
    private (long AfterSeq, bool Restarted) ParseCursor(string? cursor)
    {
        if (cursor is null)
            return (0, false);
        string[] parts = cursor.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out long epoch) || !long.TryParse(parts[1], out long seq) || seq < 0)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"'{cursor}' isn't a log cursor; pass the 'next' of an earlier reply.");
        return epoch == Epoch ? (seq, false) : (0, true);
    }
}
