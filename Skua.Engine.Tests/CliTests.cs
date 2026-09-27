using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

public class CliTests
{
    [Fact]
    public async Task Status_json_prints_the_status_DTO()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(0, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement engine = json.RootElement.GetProperty("engine");
        Assert.Equal("default", engine.GetProperty("name").GetString());
        Assert.Equal(ControlProtocol.Version, engine.GetProperty("protocol").GetInt32());
        Assert.False(json.RootElement.GetProperty("game").GetProperty("gameHostUp").GetBoolean());
        Assert.Equal("notStarted", json.RootElement.GetProperty("game").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Status_prints_human_text_by_default()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Engine  default", result.Stdout);
        Assert.Contains("Game Host down", result.Stdout);
    }

    [Fact]
    public async Task Engine_start_status_and_stop_control_the_Engine_lifetime()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult before = await sandbox.RunCliAsync("engine", "status", "--json");
        ProcessResult start = await sandbox.RunCliAsync("engine", "start");
        ProcessResult running = await sandbox.RunCliAsync("engine", "status", "--json");
        ProcessResult stop = await sandbox.RunCliAsync("engine", "stop");
        ProcessResult after = await sandbox.RunCliAsync("engine", "status");

        Assert.Equal("stopped", State(before));
        Assert.Equal(0, start.ExitCode);
        Assert.Contains("is running", start.Stdout);
        Assert.Equal("running", State(running));
        Assert.Equal(0, stop.ExitCode);
        Assert.False(File.Exists(sandbox.Endpoint.SocketPath));
        Assert.False(EngineLock.IsHeld(sandbox.Endpoint.LockPath));
        Assert.Contains("is stopped", after.Stdout);
    }

    [Fact]
    public async Task A_protocol_mismatch_exits_with_its_code_and_a_stop_hint()
    {
        await using EngineSandbox sandbox = new();
        await using OtherVersionEngine other = new(sandbox);

        ProcessResult human = await sandbox.RunCliAsync("status");
        ProcessResult json = await sandbox.RunCliAsync("status", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), human.ExitCode);
        Assert.Contains("Run 'skua engine stop'", human.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.ProtocolMismatch), json.ExitCode);
        using JsonDocument error = JsonDocument.Parse(json.Stdout);
        Assert.Equal("protocolMismatch", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_over_long_socket_path_exits_with_the_invalid_argument_code()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult longPath = await sandbox.RunCliAsync(
            new Dictionary<string, string> { [EngineEndpoint.SkuaDirVariable] = "/tmp/" + new string('x', 100) }, "status", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.InvalidArgument), longPath.ExitCode);
        using JsonDocument error = JsonDocument.Parse(longPath.Stdout);
        Assert.Equal("invalidArgument", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Every_error_code_has_its_own_nonzero_exit_code()
    {
        int[] codes = Enum.GetValues<ErrorCode>().Select(ExitCodes.For).ToArray();

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.DoesNotContain(codes, code => code is 0 or 1 or 2 or > 125);
    }

    private static string? State(ProcessResult result) =>
        JsonDocument.Parse(result.Stdout).RootElement.GetProperty("state").GetString();
}
