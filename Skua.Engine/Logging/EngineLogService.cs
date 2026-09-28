using CommunityToolkit.Mvvm.Messaging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.Engine.Logging;

/// <summary>
/// Core's <see cref="ILogService"/> on the Engine: Script, debug and flash lines go to <see cref="EngineLogs"/>,
/// and every Bridge call failure is a flash line plus a <c>bridge.error</c> event.
/// Every text line the Engine records, redacted as stored, is also sent as Core's <see cref="AddLogMessage"/>, so the Mac App's Logs panel
/// shows it live.
/// </summary>
internal sealed class EngineLogService : ILogService
{
    private static readonly AsyncLocal<List<string>?> s_captured = new();

    private readonly EngineLogs _logs;

    public EngineLogService(EngineLogs logs)
    {
        _logs = logs;
        WeakReferenceMessenger.Default.Register<EngineLogService, FlashErrorMessage>(this, static (recipient, message) => recipient.OnFlashError(message));
        logs.TextWritten += static (kind, text) =>
        {
            if (LogTypeOf(kind) is { } type)
                WeakReferenceMessenger.Default.Send(new AddLogMessage(type, text));
        };
    }

    private static LogType? LogTypeOf(LogKind kind) => kind switch
    {
        LogKind.Script => LogType.Script,
        LogKind.Debug => LogType.Debug,
        LogKind.Flash => LogType.Flash,
        _ => null,
    };

    public void DebugLog(string message)
    {
        if (message is not null)
            EngineLog.Write(message);
    }

    public void ScriptLog(string message)
    {
        if (message is null)
            return;
        string stored = _logs.Write(LogKind.Script, message);
        if (s_captured.Value is { } lines)
        {
            lock (lines)
                lines.Add(stored);
        }
    }

    /// <summary>Also collects, as stored, the Script log lines this async flow writes from now on, until disposed; for <c>eval</c>.</summary>
    public static IDisposable CaptureScriptLines(List<string> lines)
    {
        s_captured.Value = lines;
        return new Capture();
    }

    public void FlashLog(string message)
    {
        if (message is not null)
            _logs.Write(LogKind.Flash, message);
    }

    /// <summary>Does nothing: the logs are append-only, so cursors stay valid.</summary>
    public void ClearLog(LogType logType)
    {
    }

    public List<string> GetLogs(LogType logType) => _logs.Texts(logType switch
    {
        LogType.Script => LogKind.Script,
        LogType.Flash => LogKind.Flash,
        _ => LogKind.Debug,
    });

    private void OnFlashError(FlashErrorMessage message)
    {
        string[] args = message.Args.Select(arg => arg?.ToString() ?? "null").ToArray();
        // The login game function carries credentials, maybe not the Test Account's (from eval), so none of them is logged.
        if (message.Function is "callGameFunction" && args is ["login", ..])
            args = ["login", .. args.Skip(1).Select(_ => LogScrubber.Redacted)];
        // The same line as Core's LogService writes.
        FlashLog($"{message.Function} Args[{args.Length}] {(args.Length > 0 ? $"= {{{string.Join(",", args)}}} " : "")}threw {message.Exception.GetType().Name}: {message.Exception.Message}");
        _logs.Event(EventTypes.BridgeError, new { function = message.Function, args, error = $"{message.Exception.GetType().Name}: {message.Exception.Message}" });
    }

    private sealed class Capture : IDisposable
    {
        public void Dispose() => s_captured.Value = null;
    }
}
