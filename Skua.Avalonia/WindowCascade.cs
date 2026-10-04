using Avalonia;
using Avalonia.Controls;
using Skua.Control;

namespace Skua.Avalonia;

/// <summary>
/// Where a Mac App's main window opens. Each app is its own process, which macOS doesn't cascade, so apps the Skua Manager launches would
/// all open exactly over the first one. Each app instead holds a numbered slot under <c>&lt;SkuaDIR&gt;/windows</c> for its lifetime, the lowest
/// one free, and opens <see cref="Step"/> points further down and right per slot, as macOS cascades one app's windows.
/// </summary>
public sealed class WindowCascade : IDisposable
{
    /// <summary>The offset per slot, in points.</summary>
    public const double Step = 28;

    /// <summary>The slots there are; with every one held, an app opens where the first does.</summary>
    public const int Slots = 32;

    private readonly EngineLock? _lock;

    private WindowCascade(int slot, EngineLock? held)
    {
        Slot = slot;
        _lock = held;
    }

    /// <summary>This app's slot: 0 for the first app of the data folder, 1 for the next one open alongside it, and so on.</summary>
    public int Slot { get; }

    /// <summary>Takes the lowest slot no other app holds; the kernel frees it when this process ends.</summary>
    public static WindowCascade Claim(string skuaDir)
    {
        string folder = Path.Combine(skuaDir, "windows");
        try
        {
            Directory.CreateDirectory(folder);
            for (int slot = 0; slot < Slots; slot++)
            {
                if (EngineLock.TryAcquire(Path.Combine(folder, $"{slot}.lock")) is { } held)
                    return new WindowCascade(slot, held);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Without slots the window opens where it would have.
        }
        return new WindowCascade(0, null);
    }

    /// <summary>Moves <paramref name="window"/> to its slot's place once it first opens.</summary>
    public void Place(Window window)
    {
        if (Slot == 0)
            return;
        void OnFirstOpened(object? sender, EventArgs e)
        {
            window.Opened -= OnFirstOpened;
            if (window.Screens.ScreenFromWindow(window) is { } screen)
                window.Position = Offset(window.Position, PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling), screen.WorkingArea, Slot, window.DesktopScaling);
        }
        window.Opened += OnFirstOpened;
    }

    /// <summary>
    /// Where a window of <paramref name="size"/> that would open at <paramref name="origin"/> opens for <paramref name="slot"/>: a step down
    /// and right per slot, starting over from <paramref name="origin"/> once the next step would leave <paramref name="area"/>.
    /// </summary>
    public static PixelPoint Offset(PixelPoint origin, PixelSize size, PixelRect area, int slot, double scaling)
    {
        int step = Math.Max(1, (int)Math.Round(Step * scaling));
        int fit = Math.Min(area.Right - origin.X - size.Width, area.Bottom - origin.Y - size.Height) / step;
        int places = Math.Max(0, fit) + 1;
        int k = slot % places;
        return new PixelPoint(origin.X + k * step, origin.Y + k * step);
    }

    public void Dispose() => _lock?.Dispose();
}
