using System.Text.Json;
using System.Text.Json.Serialization;

namespace Skua.Control;

/// <summary>What an entry records. <see cref="All"/> only selects in requests; no entry carries it.</summary>
public enum LogKind
{
    All,

    /// <summary>Verbatim <c>ScriptLog</c> lines.</summary>
    Script,

    /// <summary>Trace output, the Engine's own diagnostics and the Game Host's logs.</summary>
    Debug,

    /// <summary>Bridge call failures, AS3 <c>trace()</c> and uncaught AS3 errors.</summary>
    Flash,

    /// <summary>Typed entries; see <see cref="EventTypes"/>.</summary>
    Events,
}

/// <summary>One log or event entry. It has either <see cref="Text"/> or <see cref="Type"/> and <see cref="Data"/>.</summary>
/// <param name="Seq">The Engine-wide sequence number; it orders entries of every kind.</param>
/// <param name="Ts">When the entry was recorded, in UTC milliseconds since the Unix epoch.</param>
/// <param name="Kind">The log the entry belongs to; never <see cref="LogKind.All"/>.</param>
/// <param name="Run">The Script run number, or null outside a run.</param>
/// <param name="Text">The line, for every kind but <see cref="LogKind.Events"/>.</param>
/// <param name="Type">The event type, one of <see cref="EventTypes"/>.</param>
/// <param name="Data">The event's fields.</param>
/// <param name="Truncated">Whether a field was cut to its size cap.</param>
public sealed record LogEntryDto(
    long Seq,
    long Ts,
    LogKind Kind,
    int? Run,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false);

/// <summary>A page of entries in seq order.</summary>
/// <param name="Entries">The entries, oldest first.</param>
/// <param name="Next">The opaque cursor to pass as <c>after</c> for the entries that follow.</param>
/// <param name="Gap">Whether entries after the given cursor are no longer held: they were evicted, or the Engine restarted.</param>
public sealed record LogPage(IReadOnlyList<LogEntryDto> Entries, string Next, bool Gap);

/// <summary>The type names of <see cref="LogKind.Events"/> entries. They are stable contract strings.</summary>
public static class EventTypes
{
    /// <summary>The Engine started: <c>{name, build, protocol, pid}</c>.</summary>
    public const string EngineStarted = "engine.started";

    /// <summary>The Game Host started: <c>{pid, executable, swf}</c>.</summary>
    public const string GameHostStarted = "gamehost.started";

    /// <summary>The Game Host exited: <c>{code}</c>.</summary>
    public const string GameHostExited = "gamehost.exited";

    /// <summary>A Bridge call failed (<c>{function, args, error}</c>) or the Bridge stream broke (<c>{error}</c>).</summary>
    public const string BridgeError = "bridge.error";
}
