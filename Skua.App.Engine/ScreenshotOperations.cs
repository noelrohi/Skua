using System.Diagnostics;
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

    /// <remarks>
    /// The timeout covers the whole call, including a wait for a capture at another size. A caller that gives up doesn't end the capture:
    /// others may share it.
    /// </remarks>
    public async Task<ScreenshotResult> TakeAsync(int? maxWidth, CancellationToken cancellationToken)
    {
        if (maxWidth < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"maxWidth must be at least 1, not {maxWidth}.");
        uint width = (uint)(maxWidth ?? 0);
        Stopwatch waited = Stopwatch.StartNew();

        while (true)
        {
            TimeSpan left = Timeout - waited.Elapsed;
            if (left <= TimeSpan.Zero)
                throw TimedOut();

            Task<ScreenshotResult> capture;
            bool shared;
            lock (_lock)
            {
                if (_capture is null || _capture.IsCompleted)
                {
                    _capture = Task.Run(() => Capture(width, left));
                    _captureWidth = width;
                }
                capture = _capture;
                shared = _captureWidth == width;
            }

            // A capture in flight started before this call, so it ends within this call's timeout too.
            if (shared)
                return await capture.WaitAsync(cancellationToken);
            // A capture at another size is in flight: take ours once it's done.
            if (await Task.WhenAny(capture, Task.Delay(left, cancellationToken)) != capture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw TimedOut();
            }
        }
    }

    private ScreenshotResult Capture(uint maxWidth, TimeSpan timeout)
    {
        if (!_flash.IsGameHostRunning)
            throw GameHostDown();

        GameHostScreenshot? shot;
        try
        {
            shot = _flash.Screenshot(maxWidth, timeout);
        }
        catch (IOException)
        {
            throw GameHostDown();
        }
        catch (TimeoutException)
        {
            throw TimedOut();
        }

        if (shot is null)
            throw RpcErrors.Of(ErrorCode.GameHostDown, "The Game Host couldn't capture a frame; see 'skua logs debug'.");
        return new ScreenshotResult(shot.Width, shot.Height, shot.Frame, shot.Png);
    }

    private static Exception GameHostDown() => RpcErrors.Of(ErrorCode.GameHostDown, "The Game Host isn't running.");

    private static Exception TimedOut() => RpcErrors.Of(ErrorCode.Timeout, $"The Game Host didn't capture a frame within {Timeout.TotalSeconds:0} s.");
}
