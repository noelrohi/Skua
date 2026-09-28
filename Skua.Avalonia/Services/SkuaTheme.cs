using System.Globalization;
using System.Text;
using Avalonia.Media;

namespace Skua.Avalonia.Services;

/// <summary>How far a theme's lighter and darker accent shades stand from its primary colour; the names are Material Design's, as the Windows client saves them.</summary>
public enum ThemeContrast
{
    Low,
    Medium,
    High,
}

/// <summary>Which of a theme's colours its colour adjustment may change; the names are Material Design's, as the Windows client saves them.</summary>
public enum ThemeColorSelection
{
    None,
    Primary,
    Secondary,
    All,
}

/// <summary>
/// A theme as Core's settings keep it, in the Windows client's format, so both read the same <c>CurrentTheme</c>, <c>DefaultThemes</c> and
/// <c>UserThemes</c>: <c>name,Dark|Light,primary,secondary,primary foreground,secondary foreground</c>, with the colours as <c>#aarrggbb</c>,
/// then, for a colour-adjusted theme, <c>true,contrast ratio,contrast,colour selection</c>.
/// </summary>
/// <remarks>Two themes with the same name are the same theme, as on Windows: saving one under a taken name replaces it.</remarks>
public sealed class SkuaTheme
{
    public string Name { get; init; } = "";
    public bool IsDark { get; init; } = true;
    public Color Primary { get; init; }
    public Color Secondary { get; init; }
    public Color PrimaryForeground { get; init; }
    public Color SecondaryForeground { get; init; }
    public bool UseColorAdjustment { get; init; }
    public float ContrastRatio { get; init; } = 4.5f;
    public ThemeContrast Contrast { get; init; } = ThemeContrast.Medium;
    public ThemeColorSelection Colors { get; init; } = ThemeColorSelection.All;

    /// <summary>For the theme buttons: the theme's primary colour and the text on it.</summary>
    public IBrush PrimaryBrush => new SolidColorBrush(Primary);

    public IBrush PrimaryForegroundBrush => new SolidColorBrush(PrimaryForeground);

    /// <summary>The theme a setting holds, or null when it holds none or can't be read.</summary>
    public static SkuaTheme? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string[] fields = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 6
            || !Color.TryParse(fields[2], out Color primary) || !Color.TryParse(fields[3], out Color secondary)
            || !Color.TryParse(fields[4], out Color primaryForeground) || !Color.TryParse(fields[5], out Color secondaryForeground))
            return null;
        bool adjusted = fields.Length > 6 && bool.TryParse(fields[6], out bool use) && use;
        return new SkuaTheme
        {
            Name = fields[0],
            IsDark = fields[1].Equals("dark", StringComparison.OrdinalIgnoreCase),
            Primary = primary,
            Secondary = secondary,
            PrimaryForeground = primaryForeground,
            SecondaryForeground = secondaryForeground,
            UseColorAdjustment = adjusted,
            ContrastRatio = fields.Length > 7 && float.TryParse(fields[7], NumberStyles.Any, CultureInfo.InvariantCulture, out float ratio) ? ratio : 4.5f,
            Contrast = fields.Length > 8 && Enum.TryParse(fields[8], true, out ThemeContrast contrast) ? contrast : ThemeContrast.Medium,
            Colors = fields.Length > 9 && Enum.TryParse(fields[9], true, out ThemeColorSelection colors) ? colors : ThemeColorSelection.All,
        };
    }

    public string Format()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{Name},{(IsDark ? "Dark" : "Light")},{Hex(Primary)},{Hex(Secondary)},{Hex(PrimaryForeground)},{Hex(SecondaryForeground)},");
        if (UseColorAdjustment)
            text.Append(CultureInfo.InvariantCulture, $"{UseColorAdjustment},{ContrastRatio},{Contrast},{Colors},");
        return text.ToString();
    }

    public static string Hex(Color color) => $"#{color.A:x2}{color.R:x2}{color.G:x2}{color.B:x2}";

    public override string ToString() => Name;

    public override bool Equals(object? obj) => obj is SkuaTheme theme && theme.Name == Name;

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}
