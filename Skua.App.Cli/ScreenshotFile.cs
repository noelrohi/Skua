using Skua.Control;

namespace Skua.App.Cli;

/// <summary>What <c>skua screenshot</c> reports: the PNG file it wrote, and the frame in it.</summary>
public sealed record ScreenshotFile(string Path, int Width, int Height, long Frame)
{
    /// <summary>Writes the screenshot to <paramref name="path"/>, or to a new file in the current directory when it is null.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when the file can't be written.</exception>
    public static async Task<ScreenshotFile> WriteAsync(ScreenshotResult shot, string? path, CancellationToken cancellationToken)
    {
        string fullPath = System.IO.Path.GetFullPath(path ?? $"skua-screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        try
        {
            await File.WriteAllBytesAsync(fullPath, shot.Png, cancellationToken);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ControlException(ErrorCode.InvalidArgument, $"Couldn't write the screenshot to {fullPath}: {e.Message}", e);
        }
        return new ScreenshotFile(fullPath, shot.Width, shot.Height, shot.Frame);
    }
}
