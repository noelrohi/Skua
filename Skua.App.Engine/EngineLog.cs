namespace Skua.App.Engine;

/// <summary>The Engine's own diagnostics, on stderr (the log file when detached).</summary>
internal static class EngineLog
{
    public static void Write(string message) => Console.Error.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {message}");
}
