using System.Diagnostics;
using Skua.Engine.Logging;
using Skua.Control;

namespace Skua.Engine;

/// <summary>
/// The Engine's own diagnostics, on stderr in <c>skua-engine</c> (the log file when detached) and, once <see cref="Attach"/> has run, in the <c>debug</c> log.
/// </summary>
internal static class EngineLog
{
    private static EngineLogs? s_logs;
    private static bool s_echo = true;

    /// <summary>
    /// Records diagnostics and Trace output in <paramref name="logs"/> from now on, and echoes them as recorded on stderr unless
    /// <paramref name="echo"/> is false (the Mac App has no terminal).
    /// </summary>
    public static void Attach(EngineLogs logs, bool echo)
    {
        s_logs = logs;
        s_echo = echo;
        Trace.Listeners.Add(new Listener());
    }

    public static void Write(string message)
    {
        string text = s_logs?.Write(LogKind.Debug, message) ?? message;
        Echo(text);
    }

    /// <summary>Writes <paramref name="text"/> to stderr only, when echoing.</summary>
    public static void Echo(string text)
    {
        if (s_echo)
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
