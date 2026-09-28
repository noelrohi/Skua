namespace Skua.Avalonia;

/// <summary>
/// What the Game View has shown since it was last read: the frames, and how old each was when shown (from the Game Host's write stamp).
/// </summary>
public sealed class GameViewStats
{
    private readonly object _lock = new();
    private readonly List<long> _agesNs = [];
    private long _total;

    /// <summary>Every frame shown since the view was made.</summary>
    public long Total => Interlocked.Read(ref _total);

    public void Shown(long ageNs)
    {
        Interlocked.Increment(ref _total);
        lock (_lock)
            _agesNs.Add(Math.Max(0, ageNs));
    }

    /// <summary>
    /// The frames shown since the last call and their age percentiles, as the JSON of a <c>[gameview] stats</c> debug line; the counts restart.
    /// </summary>
    public string TakeJson(TimeSpan interval)
    {
        long[] ages;
        lock (_lock)
        {
            ages = [.. _agesNs];
            _agesNs.Clear();
        }
        Array.Sort(ages);
        double fps = interval > TimeSpan.Zero ? ages.Length / interval.TotalSeconds : 0;
        return $$"""{"intervalSec":{{interval.TotalSeconds:0}},"framesShown":{{ages.Length}},"fps":{{fps:0.0}},"frameAgeMsP50":{{Percentile(ages, 0.50):0.0}},"frameAgeMsP95":{{Percentile(ages, 0.95):0.0}},"frameAgeMsMax":{{(ages.Length > 0 ? ages[^1] / 1e6 : 0):0.0}}}""";
    }

    /// <summary>The nearest-rank percentile of sorted nanoseconds, in milliseconds; 0 for none.</summary>
    public static double Percentile(long[] sortedNs, double p)
    {
        if (sortedNs.Length == 0)
            return 0;
        int rank = (int)Math.Ceiling(p * sortedNs.Length);
        return sortedNs[Math.Clamp(rank - 1, 0, sortedNs.Length - 1)] / 1e6;
    }
}
