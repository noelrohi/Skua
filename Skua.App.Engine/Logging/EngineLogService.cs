using CommunityToolkit.Mvvm.Messaging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;

namespace Skua.App.Engine.Logging;

/// <summary>
/// Core's <see cref="ILogService"/> on the Engine: Script, debug and flash lines go to <see cref="EngineLogs"/>,
/// and every Bridge call failure is a flash line plus a <c>bridge.error</c> event.
/// </summary>
internal sealed class EngineLogService : ILogService
{
    private readonly EngineLogs _logs;

    public EngineLogService(EngineLogs logs)
    {
        _logs = logs;
        WeakReferenceMessenger.Default.Register<EngineLogService, FlashErrorMessage>(this, static (recipient, message) => recipient.OnFlashError(message));
    }

    public void DebugLog(string message)
    {
        if (message is not null)
            EngineLog.Write(message);
    }

    public void ScriptLog(string message)
    {
        if (message is not null)
            _logs.Write(LogKind.Script, message);
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
        // The same line as Core's LogService writes.
        FlashLog($"{message.Function} Args[{args.Length}] {(args.Length > 0 ? $"= {{{string.Join(",", args)}}} " : "")}threw {message.Exception.GetType().Name}: {message.Exception.Message}");
        _logs.Event(EventTypes.BridgeError, new { function = message.Function, args, error = $"{message.Exception.GetType().Name}: {message.Exception.Message}" });
    }
}
