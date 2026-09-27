using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Skua.Engine.Tests;

/// <summary>The measurements the live-game tests judge the Engine by: footprint, getter latency, Game Host stats and screenshots.</summary>
public static partial class LiveMetrics
{
    /// <summary>The Game Client's stage size, which a screenshot has at its native size.</summary>
    public const int StageWidth = 958;
    public const int StageHeight = 550;

    /// <summary>The value at quantile <paramref name="q"/> (0 to 1) of <paramref name="values"/>, by nearest rank.</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double q)
    {
        if (values.Count == 0)
            return double.NaN;
        double[] sorted = [.. values.Order()];
        return sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * q))];
    }

    /// <summary>The least-squares slope of <paramref name="points"/>, in y units per x unit; 0 with fewer than two points.</summary>
    public static double Slope(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
            return 0;
        double meanX = points.Average(p => p.X);
        double meanY = points.Average(p => p.Y);
        double spread = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        return spread == 0 ? 0 : points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / spread;
    }

    /// <summary>
    /// The process's footprint in MB (what Activity Monitor calls Memory), as macOS's <c>footprint</c> prints it, or null when it can't
    /// be read, e.g. once the process has exited.
    /// </summary>
    public static async Task<double?> FootprintMbAsync(int pid) =>
        ParseFootprintMb(await RunAsync("/usr/bin/footprint", pid.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The total of <c>footprint</c>'s output, e.g. <c>Footprint: 1.4 GB</c>, in MB (1 GB = 1024 MB).</summary>
    public static double? ParseFootprintMb(string output)
    {
        Match match = FootprintTotal().Match(output);
        if (!match.Success)
            return null;
        double value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "B" => value / (1024 * 1024),
            "KB" => value / 1024,
            "MB" => value,
            "GB" => value * 1024,
            _ => null,
        };
    }

    /// <summary>The process's resident set size in MB, or null once it has exited.</summary>
    public static async Task<double?> RssMbAsync(int pid) =>
        double.TryParse((await RunAsync("/bin/ps", "-o", "rss=", "-p", pid.ToString(CultureInfo.InvariantCulture))).Trim(), CultureInfo.InvariantCulture, out double kb)
            ? kb / 1024
            : null;

    /// <summary>The 1-minute load average.</summary>
    public static async Task<double> LoadAverageAsync() =>
        double.Parse(LoadAverage().Match(await RunAsync("/usr/sbin/sysctl", "-n", "vm.loadavg")).Groups[1].Value, CultureInfo.InvariantCulture);

    /// <summary>Whether the console session's screen is locked.</summary>
    public static async Task<bool> ScreenLockedAsync() => ParseScreenLocked(await RunAsync("/usr/sbin/ioreg", "-n", "Root", "-d1", "-a"));

    /// <summary>Whether <c>ioreg -n Root -d1 -a</c> reports a console user whose screen is locked.</summary>
    public static bool ParseScreenLocked(string ioregPlist) => ScreenLocked().IsMatch(ioregPlist);

    /// <summary>
    /// Parses a Game Host stats debug line: the Engine writes one per interval, with the Game Host's counters since the last one
    /// (<c>ticks</c> and <c>uptimeMs</c> are running totals; <c>maxTickGapMs</c> is the largest gap since the last line).
    /// </summary>
    public static GameHostStats? ParseStats(long ts, string debugLine)
    {
        if (!debugLine.StartsWith(StatsPrefix, StringComparison.Ordinal))
            return null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(debugLine[StatsPrefix.Length..]);
            JsonElement root = json.RootElement;
            double Number(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
            return new GameHostStats(ts, Number("uptimeMs"), Number("ticks"), Number("frameRate"), Number("maxTickGapMs"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The Engine's prefix for Game Host stats lines in the debug log (GameHostSupervisor.StatsPrefix).</summary>
    public const string StatsPrefix = "[gamehost] stats ";

    /// <summary>The ticks per second between two stats lines, from the Game Host's own clock.</summary>
    public static double TicksPerSecond(GameHostStats earlier, GameHostStats later) =>
        later.UptimeMs > earlier.UptimeMs ? (later.Ticks - earlier.Ticks) * 1000 / (later.UptimeMs - earlier.UptimeMs) : double.NaN;

    /// <summary>
    /// Why <paramref name="png"/> isn't a correct screenshot of the stage, or null when it is: a PNG at the stage's native size that shows
    /// something, not one flat colour (a blank or black frame).
    /// </summary>
    public static string? ScreenshotProblem(byte[] png, int width = StageWidth, int height = StageHeight)
    {
        Image? image;
        try
        {
            image = DecodePng(png);
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        {
            return $"it isn't a PNG this check can read: {e.Message}";
        }
        if (image is null)
            return "it isn't an 8-bit RGB or RGBA PNG";
        if ((image.Width, image.Height) != (width, height))
            return $"it is {image.Width}x{image.Height}, not the stage's {width}x{height}";
        int colours = image.DistinctColours(64);
        return colours < 16 ? $"it shows only {colours} distinct colours on a 64x64 grid, so the frame is blank" : null;
    }

    /// <summary>A decoded 8-bit, non-interlaced RGB or RGBA PNG, as the Game Host encodes screenshots; null for any other kind.</summary>
    public static Image? DecodePng(byte[] png)
    {
        ReadOnlySpan<byte> signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(signature))
            throw new InvalidDataException("no PNG signature");

        int width = 0, height = 0, channels = 0;
        using MemoryStream idat = new();
        for (int at = 8; at + 8 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            string type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            ReadOnlySpan<byte> data = png.AsSpan(at + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                (byte depth, byte colourType, byte interlace) = (data[8], data[9], data[12]);
                channels = colourType switch { 2 => 3, 6 => 4, _ => 0 };
                if (depth != 8 || channels == 0 || interlace != 0)
                    return null;
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }
            else if (type == "IEND")
            {
                break;
            }
            at += 12 + length;
        }

        int stride = width * channels;
        byte[] pixels = new byte[stride * height];
        idat.Position = 0;
        using ZLibStream inflate = new(idat, CompressionMode.Decompress);
        byte[] row = new byte[stride + 1];
        for (int y = 0; y < height; y++)
        {
            inflate.ReadExactly(row);
            Span<byte> current = pixels.AsSpan(y * stride, stride);
            ReadOnlySpan<byte> previous = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : new byte[stride];
            for (int x = 0; x < stride; x++)
            {
                int left = x >= channels ? current[x - channels] : 0;
                int up = previous[x];
                int upLeft = x >= channels ? previous[x - channels] : 0;
                current[x] = (byte)(row[x + 1] + row[0] switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"filter {row[0]}"),
                });
            }
        }
        return new Image(width, height, channels, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static async Task<string> RunAsync(string tool, params string[] arguments)
    {
        System.Diagnostics.ProcessStartInfo startInfo = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stderr;
        return stdout;
    }

    [GeneratedRegex(@"Footprint:\s+([\d.]+)\s+(B|KB|MB|GB)\b")]
    private static partial Regex FootprintTotal();

    [GeneratedRegex(@"\{\s*([\d.]+)")]
    private static partial Regex LoadAverage();

    [GeneratedRegex(@"<key>CGSSessionScreenIsLocked</key>\s*<true/>")]
    private static partial Regex ScreenLocked();
}

/// <summary>One Game Host stats line: <paramref name="Ts"/> is when the Engine logged it, the rest the Game Host's counters.</summary>
public sealed record GameHostStats(long Ts, double UptimeMs, double Ticks, double FrameRate, double MaxTickGapMs);

/// <summary>Decoded pixels, <see cref="Channels"/> bytes each, row by row.</summary>
public sealed record Image(int Width, int Height, int Channels, byte[] Pixels)
{
    /// <summary>How many distinct RGB colours a <paramref name="grid"/> by <paramref name="grid"/> sample of the image shows.</summary>
    public int DistinctColours(int grid)
    {
        HashSet<int> colours = [];
        for (int gy = 0; gy < grid; gy++)
        for (int gx = 0; gx < grid; gx++)
        {
            int at = ((gy * Height / grid) * Width + gx * Width / grid) * Channels;
            colours.Add(Pixels[at] << 16 | Pixels[at + 1] << 8 | Pixels[at + 2]);
        }
        return colours.Count;
    }
}
