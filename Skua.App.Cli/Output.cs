using System.Text.Json;
using Skua.Control;

namespace Skua.App.Cli;

/// <summary>Human-readable renderings of the Control Surface DTOs; <c>--json</c> prints the DTOs themselves.</summary>
internal static class Output
{
    public static JsonSerializerOptions JsonOptions { get; } = new(ControlJson.Options) { WriteIndented = true };

    public static string Status(StatusDto status)
    {
        EngineInfoDto engine = status.Engine;
        GameStatusDto game = status.Game;
        string gameLine = game.GameHostUp
            ? $"Game Host up{(game.State is { } state ? $", {Name(state)}" : "")}{(game.Server is { } server ? $" on {server}" : "")}"
            : "Game Host down";
        return $"""
            Engine  {engine.Name} (pid {engine.Pid}, up {engine.UptimeSec:0} s, build {engine.Build}, protocol {engine.Protocol})
            Game    {gameLine}
            """;
    }

    public static string EngineState(EngineStateDto engine) => engine.State switch
    {
        "running" when engine.Compatible == false =>
            $"Engine '{engine.Name}' is running (pid {engine.Pid}, build {engine.Build}) on protocol {engine.Protocol}, not {ControlProtocol.Version}; run 'skua engine stop'.",
        "running" => $"Engine '{engine.Name}' is running (pid {engine.Pid}, build {engine.Build}).",
        "startingOrHung" => $"Engine '{engine.Name}' is starting or hung; its socket {engine.Socket} doesn't answer.",
        _ => $"Engine '{engine.Name}' is stopped.",
    };

    private static string Name(GameState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
}
