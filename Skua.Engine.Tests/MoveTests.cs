using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>join</c> and <c>jump</c> against the fake Game Host's simulated game.</summary>
public class MoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Join_reports_the_final_location_and_whether_the_player_was_already_there()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        LocationResult battleon = await session.Connection.JoinAsync("battleon", cancellationToken: Ct);
        LocationResult yulgar = await session.Connection.JoinAsync("yulgar", cancellationToken: Ct);
        LocationResult upstairs = await session.Connection.JoinAsync("Yulgar", "upstairs", "Left", cancellationToken: Ct);
        LocationResult room = await session.Connection.JoinAsync("battleon-1234", "r2", cancellationToken: Ct);

        Assert.Equal(new LocationResult("battleon", "Enter", "Spawn", AlreadyThere: true), battleon);
        Assert.Equal(new LocationResult("yulgar", "Enter", "Spawn", AlreadyThere: false), yulgar);
        Assert.Equal(new LocationResult("yulgar", "Upstairs", "Left", AlreadyThere: false), upstairs);
        Assert.Equal(new LocationResult("battleon", "r2", "Spawn", AlreadyThere: false), room);
        List<LogEntryDto> joined = await session.Connection.WaitForLogsAsync(LogKind.Events, 3, e => e.Type == EventTypes.MapJoined);
        Assert.Equal(["battleon", "yulgar", "battleon"], joined.Select(e => e.Data!.Value.GetProperty("map").GetString()));
        Assert.Equal(["tfer yulgar Enter Spawn", "tfer battleon-1234 r2 Spawn"], (await session.GameHost.CallsAsync()).Where(c => c.StartsWith("tfer ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Join_and_jump_fail_with_NotLoggedIn_before_a_login()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);

        ControlException join = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JoinAsync("battleon", cancellationToken: Ct));
        ControlException jump = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JumpAsync("Enter", cancellationToken: Ct));

        Assert.Equal((ErrorCode.NotLoggedIn, ErrorCode.NotLoggedIn), (join.Code, jump.Code));
        Assert.DoesNotContain(await session.GameHost.CallsAsync(), c => c.StartsWith("tfer ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_map_that_never_loads_times_out_and_leaves_the_player_where_they_were()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("lock-map ultradage");

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JoinAsync("ultradage", timeoutSec: 2, cancellationToken: Ct));
        LocationResult after = await session.Connection.JumpAsync("Enter", cancellationToken: Ct);

        Assert.Equal(ErrorCode.Timeout, e.Code);
        Assert.Contains("still on battleon", e.Message);
        Assert.Equal(new LocationResult("battleon", "Enter", "Spawn", AlreadyThere: true), after);
    }

    [Fact]
    public async Task Jump_moves_within_the_map_and_refuses_a_cell_the_map_lacks()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        LocationResult r2 = await session.Connection.JumpAsync("R2", "Right", cancellationToken: Ct);
        LocationResult again = await session.Connection.JumpAsync("r2", cancellationToken: Ct);
        ControlException missing = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JumpAsync("Nowhere", cancellationToken: Ct));

        Assert.Equal(new LocationResult("battleon", "r2", "Right", AlreadyThere: false), r2);
        Assert.Equal(new LocationResult("battleon", "r2", "Right", AlreadyThere: true), again);
        Assert.Equal(ErrorCode.InvalidArgument, missing.Code);
        Assert.Contains("Enter, r2, r3", missing.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("battle on")]
    [InlineData("battleon%xt%zm%")]
    public async Task A_malformed_map_is_an_invalid_argument(string map)
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JoinAsync(map, cancellationToken: Ct));
        ControlException cell = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JoinAsync("yulgar", "Enter%", cancellationToken: Ct));

        Assert.Equal((ErrorCode.InvalidArgument, ErrorCode.InvalidArgument), (e.Code, cell.Code));
        Assert.DoesNotContain(await session.GameHost.CallsAsync(), c => c.StartsWith("tfer ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_move_while_one_runs_is_Busy()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        await session.GameHost.DoAsync("lock-map ultradage");

        Task<LocationResult> slow = session.Connection.JoinAsync("ultradage", timeoutSec: 3, cancellationToken: Ct);
        await Task.Delay(500, Ct);
        ControlException busy = await Assert.ThrowsAsync<ControlException>(() => session.Connection.JumpAsync("r2", cancellationToken: Ct));

        Assert.Equal(ErrorCode.Busy, busy.Code);
        Assert.Equal(ErrorCode.Timeout, (await Assert.ThrowsAsync<ControlException>(() => slow)).Code);
    }
}
