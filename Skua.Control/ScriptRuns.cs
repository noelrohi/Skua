using System.Text.Json;

namespace Skua.Control;

/// <summary>Where the Engine's Script state machine is.</summary>
public enum ScriptState
{
    /// <summary>No Script runs; a Script may start.</summary>
    Idle,

    /// <summary><c>script_start</c> is compiling the Script.</summary>
    Compiling,

    /// <summary>The Script runs, or waits for Core's auto-relogin to restart it.</summary>
    Running,

    /// <summary><c>script_stop</c> asked the Script to stop and its thread hasn't ended yet, or it never ended.</summary>
    Stopping,
}

/// <summary>How a run ended.</summary>
public enum ScriptOutcome
{
    /// <summary>The Script returned.</summary>
    Completed,

    /// <summary><c>script_stop</c> stopped it, or the Script stopped itself.</summary>
    Stopped,

    /// <summary>It threw, or Core's auto-relogin couldn't restart it.</summary>
    Error,

    /// <summary><c>script_stop</c> asked it to stop, but its thread didn't end in time.</summary>
    StopTimedOut,
}

/// <summary>How a run's Questions are answered.</summary>
public enum DialogMode
{
    /// <summary>A Question waits for an answer until the run's dialog timeout, then gets the fallback.</summary>
    Ask,

    /// <summary>A Question gets the fallback at once.</summary>
    Cancel,
}

/// <summary>The reply to <c>script_status</c>, also part of <c>status</c>.</summary>
/// <param name="Run">The run in progress, or null when idle.</param>
/// <param name="LastRun">The last run that ended since the Engine started, or null.</param>
public sealed record ScriptStatusDto(ScriptState State, ScriptRunDto? Run, ScriptRunResultDto? LastRun);

/// <summary>A run in progress. A restart by Core's auto-relogin is the same run.</summary>
/// <param name="Number">The run number, as the <c>run</c> of log entries recorded during it.</param>
/// <param name="Script">The Script as <c>script_start</c> named it: a Script Source path, or an absolute path.</param>
/// <param name="Relogins">How many times Core's auto-relogin restarted it.</param>
/// <param name="ReloggingIn">Whether its thread has ended for an auto-relogin that will restart it.</param>
/// <param name="ElapsedSec">How long it has run so far, to a tenth of a second, as the Engine's clock measures it.</param>
/// <param name="QuestIdleSec">
/// How many seconds since a quest the run farms progressed: it was accepted, one of its requirements rose while it was (see
/// <see cref="QuestRequirementDto.IdleSec"/>), the rise that met it included, or it was turned in. The run farms the accepted quests with a
/// requirement unmet, but not one left behind: with no progress for the stall time while another quest progressed, as a Script leaves an
/// earlier step's quest accepted, unless it has left all of them behind. At most <paramref name="ElapsedSec"/>, and null when the run farms none.
/// </param>
/// <param name="Kills">How many monsters the player was credited with killing during the run.</param>
/// <param name="KillsPerMin">
/// The run's kills per minute over the last 5 minutes, or over its time if shorter, but at least a minute; with <paramref name="QuestIdleSec"/>
/// it tells a Script grinding a rare drop from one that is stuck.
/// </param>
/// <param name="Deaths">How many times the player died during the run.</param>
/// <param name="Goal">What the Script is working toward, as its CoreBots log lines say; null when it logs none.</param>
public sealed record ScriptRunDto(
    int Number, string Script, DateTimeOffset StartedAt, int Relogins, bool ReloggingIn, DialogMode Dialogs, int DialogTimeoutSec, double ElapsedSec,
    double? QuestIdleSec = null, int Kills = 0, double? KillsPerMin = null, int Deaths = 0, ScriptGoalDto? Goal = null);

/// <summary>What a run's Script is working toward and why: each step is what the one before it needs.</summary>
/// <param name="Quest">The quest it does, by name.</param>
/// <param name="Buy">The item it farms the materials to buy.</param>
/// <param name="Farm">The material, or quest item, it farms.</param>
/// <param name="Now">What it is doing for it, e.g. <c>killing Inquisitor Hobo for Inquisitor Bones</c>.</param>
/// <param name="Resets">How many times a death has reset the farm's wave since it began, as <c>Death - Resetting</c> logs it.</param>
/// <param name="LastResetAt">When the last of those was.</param>
public sealed record ScriptGoalDto(string? Quest, GoalItemDto? Buy, GoalItemDto? Farm, string? Now, int Resets, DateTimeOffset? LastResetAt);

/// <param name="Want">How many the Script wants.</param>
/// <param name="Have">How many the player owns, inventory, temporary inventory and bank, as last sampled; null before a sample.</param>
/// <param name="PerHour">How many it gained an hour since the step began, from the count logged then; null before a gain.</param>
public sealed record GoalItemDto(string Item, int Want, int? Have, double? PerHour);

/// <summary>A run that ended.</summary>
/// <param name="Error">Why it failed, for <see cref="ScriptOutcome.Error"/>: the exception's type and message.</param>
public sealed record ScriptRunResultDto(int Number, string Script, ScriptOutcome Outcome, string? Error, DateTimeOffset StartedAt, double DurationSec, int Relogins);

/// <summary>The reply to <c>script_start</c>.</summary>
/// <param name="Run">The new run's number; a short Script may have ended by the time this returns.</param>
public sealed record ScriptStartResult(int Run, ScriptStatusDto Status);

/// <summary>The reply to <c>script_stop</c>.</summary>
/// <param name="WasRunning">Whether a run was in progress.</param>
/// <param name="Ended">Whether the Script thread has ended; false means it didn't stop in time and the run ended as <see cref="ScriptOutcome.StopTimedOut"/>.</param>
public sealed record ScriptStopResult(bool WasRunning, bool Ended, ScriptStatusDto Status);

public enum ScriptWaitReason
{
    /// <summary>No run is in progress: it ended, or none was running.</summary>
    Ended,

    /// <summary>A Question is pending; <c>dialogs</c> lists it.</summary>
    Question,

    /// <summary>The run is still in progress.</summary>
    Timeout,
}

/// <summary>The reply to <c>script_wait</c>.</summary>
public sealed record ScriptWaitResult(ScriptWaitReason Reason, ScriptStatusDto Status);

/// <summary>One option of a Script.</summary>
/// <param name="Key">What names it in <c>script_start</c>'s options: its name, or <c>&lt;group&gt;:&lt;name&gt;</c> for an option in a group.</param>
/// <param name="Category">The group it shows under: <c>Options</c>, or its group's name.</param>
/// <param name="Type"><c>bool</c>, <c>int</c>, <c>number</c>, <c>string</c> or <c>enum</c>.</param>
/// <param name="Value">The stored value, or the default when none is stored.</param>
/// <param name="Choices">An enum's values, else null.</param>
/// <param name="Transient">Whether its value resets on every start, so it isn't stored and can't be set.</param>
/// <param name="Text">
/// Whether it is only text shown among the options, such as a merge shop's "Mode Explanation": its name is blank, so nothing reads it and it
/// can't be set.
/// </param>
public sealed record ScriptOptionDto(
    string Key, string Category, string Name, string DisplayName, string? Description, string Type, string Value, string Default,
    IReadOnlyList<string>? Choices, bool Transient, bool Text = false);

/// <summary>The reply to <c>script_options</c>.</summary>
/// <param name="Storage">The options storage the values are kept in, shared by Scripts that name the same one.</param>
public sealed record ScriptOptionsResult(string Script, string Storage, IReadOnlyList<ScriptOptionDto> Options);

/// <summary>The reply to <c>eval</c>.</summary>
/// <param name="Value">What the snippet returned, as JSON on a best-effort basis; null when it returned nothing or threw.</param>
/// <param name="Logs">The Script log lines the snippet's thread wrote.</param>
/// <param name="Error">What the snippet threw, with its stack, or null.</param>
public sealed record EvalResult(JsonElement? Value, IReadOnlyList<string> Logs, string? Error);
