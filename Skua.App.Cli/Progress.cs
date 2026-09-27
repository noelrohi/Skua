using System.Globalization;
using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>What <c>skua watch --json</c> prints at each interval: the player's progress and the run in progress.</summary>
/// <param name="Level">The player's level, or null when not playing; so for the other player fields.</param>
/// <param name="XpPercent">The XP toward the next level as a percentage, or null when not playing or at the top level.</param>
/// <param name="GoldGained">The gold gained (or, negative, lost) since the watch or follow began.</param>
/// <param name="Run">The run in progress, with its elapsed time, or null when idle.</param>
public sealed record ProgressDto(
    DateTimeOffset At, GameState State, int? Level, int? Xp, int? RequiredXp, double? XpPercent, int? Gold, int? GoldGained, string? Map, ScriptRunDto? Run);

/// <summary>Turns <c>status</c> replies into progress, counting the gold from the first one that has a player.</summary>
internal sealed class ProgressView
{
    private int? _startGold;

    public ProgressDto Read(StatusDto status)
    {
        PlayerDto? player = status.Game.Player;
        if (player is not null)
            _startGold ??= player.Gold;
        return new ProgressDto(DateTimeOffset.UtcNow, status.Game.State, player?.Level, player?.Xp, player?.RequiredXp, player?.XpPercent, player?.Gold,
            player?.Gold - _startGold, player?.Map, status.Script.Run);
    }

    /// <summary>One line, e.g. <c>Level 10 · XP 37.5% · 5,000 gold (+1,200) · battleon · run 1 · 12:05</c>.</summary>
    public static string Line(ProgressDto progress)
    {
        CultureInfo invariant = CultureInfo.InvariantCulture;
        string game = progress.Level is { } level
            ? string.Format(invariant, "Level {0} · XP {1} · {2:N0} gold ({3:+#,0;-#,0;+0}) · {4}",
                level, progress.XpPercent is { } percent ? percent.ToString("0.0", invariant) + "%" : "max", progress.Gold, progress.GoldGained, progress.Map)
            : $"Not playing ({Output.Name(progress.State)})";
        return progress.Run is { } run ? $"{game} · run {run.Number} · {Output.Duration(run.ElapsedSec)}" : game;
    }
}

/// <summary>
/// <c>skua watch</c>: the progress of the game and of the Script running, until interrupted, which leaves the Script running. On a terminal it is
/// the run's log under a live status line, as <c>skua script start --follow</c> shows it; otherwise, or with <c>--json</c>, one line per interval.
/// </summary>
internal static class Watch
{
    public static async Task<int> RunAsync(bool json, int intervalSec, CancellationToken cancellationToken)
    {
        try
        {
            using EngineConnection connection = await EngineClient.ConnectAsync(Cli.Options(), cancellationToken);
            if (!json && !Console.IsOutputRedirected)
                return await ScriptFollow.WatchAsync(connection, cancellationToken);

            ProgressView view = new();
            while (true)
            {
                ProgressDto progress = view.Read(await connection.StatusAsync(cancellationToken));
                Console.WriteLine(json
                    ? JsonSerializer.Serialize(progress, ControlJson.Options)
                    : $"{progress.At.ToLocalTime():HH:mm:ss} {ProgressView.Line(progress)}");
                await Task.Delay(TimeSpan.FromSeconds(intervalSec), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("skua: stopped watching; the Script still runs, and 'skua script stop' stops it.");
            return ExitCodes.Success;
        }
        catch (ControlException e)
        {
            return Cli.Fail(json, e);
        }
    }
}
