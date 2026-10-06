namespace Skua.Core.Models.Items;

/// <summary>
/// Compares item names with <c>&amp;amp;</c> and <c>&amp;</c> as the same: the Windows client reads a name such as
/// <c>Crag &amp;amp; Bamboozle</c> where the Mac App reads <c>Crag &amp; Bamboozle</c>, and Scripts spell it either way.
/// </summary>
public sealed class ItemNameComparer : IEqualityComparer<string?>
{
    /// <summary>Compares the names case-sensitively, as the item stores do.</summary>
    public static ItemNameComparer Ordinal { get; } = new(StringComparer.Ordinal);

    /// <summary>Compares the names case-insensitively, as drops and shops do.</summary>
    public static ItemNameComparer OrdinalIgnoreCase { get; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly StringComparer _comparer;

    private ItemNameComparer(StringComparer comparer)
    {
        _comparer = comparer;
    }

    /// <summary>The name with each <c>&amp;amp;</c>, in any case, as <c>&amp;</c>.</summary>
    public static string Normalize(string name)
    {
        return name.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name spelled with each <c>&amp;</c> as <c>&amp;</c>, then as <c>&amp;amp;</c>; a name without one is its only spelling.</summary>
    public static IEnumerable<string> Spellings(string name)
    {
        string plain = Normalize(name);
        return new[] { plain, plain.Replace("&", "&amp;", StringComparison.Ordinal) }.Distinct();
    }

    public bool Equals(string? x, string? y)
    {
        return x is null || y is null ? x == y : _comparer.Equals(Normalize(x), Normalize(y));
    }

    public int GetHashCode(string name)
    {
        return _comparer.GetHashCode(Normalize(name));
    }
}
