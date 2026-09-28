using System.Diagnostics;
using Avalonia.Input.Platform;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

/// <summary>Core's clipboard on the macOS pasteboard, through the active window's Avalonia clipboard.</summary>
/// <remarks>Data in other formats stays in this process: nothing on macOS reads Skua's own formats.</remarks>
public sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly Dictionary<string, object> _data = [];

    public void SetText(string text) => UiThread.Post(async () =>
    {
        try
        {
            if (Clipboard() is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Couldn't copy to the clipboard: {e.Message}");
        }
    });

    public string GetText() => UiThread.Wait(async () => Clipboard() is { } clipboard ? await clipboard.TryGetTextAsync() ?? "" : "");

    public void SetData(string format, object data)
    {
        lock (_data)
            _data[format] = data;
    }

    public object GetData(string format)
    {
        lock (_data)
            return _data.GetValueOrDefault(format)!;
    }

    private static IClipboard? Clipboard() => Windows.Active()?.Clipboard;
}
