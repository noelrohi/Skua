using Skua.Control;

namespace Skua.App.Cli;

/// <summary>What <c>skua screenshot</c> reports: the PNG file it wrote, and the frame in it.</summary>
internal sealed record ScreenshotFile(string Path, int Width, int Height, long Frame)
{
    /// <summary>
    /// Writes the screenshot to <paramref name="path"/>, replacing any file there, or when it is null to a new
    /// <c>skua-screenshot-&lt;time&gt;.png</c> in the current directory that no other run has taken.
    /// </summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when the file can't be written.</exception>
    public static async Task<ScreenshotFile> WriteAsync(ScreenshotResult shot, string? path, CancellationToken cancellationToken)
    {
        string name = $"skua-screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        string fullPath = System.IO.Path.GetFullPath(path ?? $"{name}.png");
        try
        {
            // Concurrent runs share one capture, so their default names can match: each takes the next free one.
            for (int n = 2; ; n++)
            {
                try
                {
                    await using FileStream file = new(fullPath, path is null ? FileMode.CreateNew : FileMode.Create, FileAccess.Write);
                    await file.WriteAsync(shot.Png, cancellationToken);
                    break;
                }
                catch (IOException) when (path is null && File.Exists(fullPath))
                {
                    fullPath = System.IO.Path.GetFullPath($"{name}-{n}.png");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ControlException(ErrorCode.InvalidArgument, $"Couldn't write the screenshot to {fullPath}: {e.Message}", e);
        }
        return new ScreenshotFile(fullPath, shot.Width, shot.Height, shot.Frame);
    }
}
