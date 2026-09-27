using Skua.Control;
using Skua.MacOS.GameHost;

namespace Skua.App.Engine;

/// <summary><c>screenshot</c>: one capture in the Game Host at a time, shared by the callers that want it at the same size.</summary>
internal sealed class ScreenshotOperations
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly BridgeFlashUtil _flash;
    private readonly object _lock = new();
    private Task<ScreenshotResult>? _capture;
    private uint _captureWidth;

    public ScreenshotOperations(BridgeFlashUtil flash)
    {
        _flash = flash;
    }

    /// <remarks>A caller that gives up doesn't end the capture: others may share it.</remarks>
    public async Task<ScreenshotResult> TakeAsync(int? maxWidth, CancellationToken cancellationToken)
    {
        if (maxWidth < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"maxWidth must be at least 1, not {maxWidth}.");
        uint width = (uint)(maxWidth ?? 0);

        while (true)
        {
            Task<ScreenshotResult> capture;
            bool shared;
            lock (_lock)
            {
                if (_capture is null || _capture.IsCompleted)
                {
                    _capture = Task.Run(() => Capture(width));
                    _captureWidth = width;
                }
                capture = _capture;
                shared = _captureWidth == width;
            }

            if (shared)
                return await capture.WaitAsync(cancellationToken);
            // A capture at another size is in flight: take ours once it's done.
            await Task.WhenAny(capture).WaitAsync(cancellationToken);
        }
    }

    private ScreenshotResult Capture(uint maxWidth)
    {
        if (!_flash.IsGameHostRunning)
            throw GameHostDown();

        GameHostScreenshot? shot;
        try
        {
            shot = _flash.Screenshot(maxWidth, Timeout);
        }
        catch (IOException)
        {
            throw GameHostDown();
        }
        catch (TimeoutException)
        {
            throw RpcErrors.Of(ErrorCode.Timeout, $"The Game Host didn't capture a frame within {Timeout.TotalSeconds:0} s.");
        }

        if (shot is null)
            throw RpcErrors.Of(ErrorCode.GameHostDown, "The Game Host couldn't capture a frame; see 'skua logs debug'.");
        return new ScreenshotResult(shot.Png, shot.Width, shot.Height, shot.Frame);
    }

    private static Exception GameHostDown() => RpcErrors.Of(ErrorCode.GameHostDown, "The Game Host isn't running.");
}
