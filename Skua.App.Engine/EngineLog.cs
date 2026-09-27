using System.Diagnostics;
using Skua.App.Engine.Logging;
using Skua.Control;

namespace Skua.App.Engine;

/// <summary>
/// The Engine's own diagnostics, on stderr (the log file when detached) and, once <see cref="Attach"/> has run, in the <c>debug</c> log.
/// </summary>
internal static class EngineLog
{
    private static EngineLogs? s_logs;

    /// <summary>Records diagnostics and Trace output in <paramref name="logs"/> from now on, and echoes them as recorded.</summary>
    public static void Attach(EngineLogs logs)
    {
        s_logs = logs;
        Trace.Listeners.Add(new Listener());
    }

    public static void Write(string message)
    {
        string text = s_logs?.Write(LogKind.Debug, message) ?? message;
        Console.Error.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {text}");
    }

    private sealed class Listener : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
                EngineLog.Write(message);
        }

        public override void WriteLine(string? message) => Write(message);
    }
}
