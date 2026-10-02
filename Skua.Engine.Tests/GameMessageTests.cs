using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Game messages (#171): recorded as <c>game</c> entries from the game's packets, followed live, and sent; only ever to the fake game.</summary>
public class GameMessageTests
{
    [Fact]
    public async Task Chat_whispers_and_server_messages_are_recorded_as_game_entries()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        foreach (string packet in (string[])[
                     "%xt%chatm%-1%zone~hello everyone%Bob%1000%",
                     "%xt%chatm%-1%party~on my way~ almost%Ann%",
                     "%xt%chatm%-1%guild~raid at 8%Cid%",
                     "%xt%whisper%-1%psst%Dee%skuatester%0%",
                     "%xt%server%-1%The server restarts in 5 minutes.%",
                     "%xt%warning%-1%You must be level 20 to enter.%",
                     // Not a game message.
                     "%xt%uotls%-1%Bob%afk:true%",
                 ])
            await session.GameHost.DoAsync($"server-packet {packet}");

        List<LogEntryDto> game = await session.Connection.WaitForLogsAsync(LogKind.Game, 6);

        Assert.All(game, e => Assert.Equal(LogKind.Game, e.Kind));
        Assert.Equal(
        [
            "zone Bob: hello everyone",
            "party Ann: on my way~ almost",
            "guild Cid: raid at 8",
            "whisper Dee→skuatester: psst",
            "server : The server restarts in 5 minutes.",
            "warning : You must be level 20 to enter.",
        ], game.Select(Describe));
    }

    [Fact]
    public async Task Logs_game_reads_the_game_entries_with_after_max_and_tail()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        for (int i = 0; i < 3; i++)
            await session.GameHost.DoAsync($"server-packet %xt%chatm%-1%zone~line {i}%Bob%");
        await session.Connection.WaitForLogsAsync(LogKind.Game, 3);

        ProcessResult first = await sandbox.RunCliAsync("logs", "game", "--max", "2", "--json");
        string next = JsonDocument.Parse(first.Stdout).RootElement.GetProperty("next").GetString()!;
        ProcessResult rest = await sandbox.RunCliAsync("logs", "game", "--after", next);
        ProcessResult tail = await sandbox.RunCliAsync("logs", "game", "--tail", "1");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(["line 0", "line 1"], JsonDocument.Parse(first.Stdout).RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("text").GetString()));
        Assert.Equal(0, rest.ExitCode);
        Assert.Contains(" game [zone] Bob: line 2", rest.Stdout);
        Assert.DoesNotContain("line 1", rest.Stdout);
        Assert.Contains(" game [zone] Bob: line 2", tail.Stdout);
        Assert.DoesNotContain("line 1", tail.Stdout);
    }

    [Fact]
    public async Task Subscribe_pushes_a_game_entry_recorded_after_it_began_and_ends_when_its_client_disconnects()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        string after = (await session.Connection.LogsAsync(LogKind.Game, null, null, 1, Ct)).Next;

        EngineConnection follower = await sandbox.ConnectAsync();
        LogPage page;
        try
        {
            await using IAsyncEnumerator<LogPage> pages = follower.SubscribeAsync([LogKind.Game], after, Ct).GetAsyncEnumerator(Ct);
            ValueTask<bool> next = pages.MoveNextAsync();
            await session.GameHost.DoAsync("server-packet %xt%chatm%-1%zone~live%Bob%");
            Assert.True(await next.AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct));
            page = pages.Current;
        }
        finally
        {
            follower.Dispose();
        }

        Assert.Equal(["zone Bob: live"], page.Entries.Select(Describe));
        await session.Connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains("A subscribe to game ended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chat_send_sends_zone_chat_and_a_whisper_to_the_game_server()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        int room = (await session.Connection.MapAsync(Ct)).RoomId;

        ChatSendResult zone = await session.Connection.ChatSendAsync("hello there", cancellationToken: Ct);
        ChatSendResult whisper = await session.Connection.ChatSendAsync("psst", "Dee", Ct);

        Assert.Equal(new ChatSendResult("zone", null, "hello there"), zone);
        Assert.Equal(new ChatSendResult("whisper", "Dee", "psst"), whisper);
        Assert.Equal(
            [$"send %xt%zm%message%{room}%hello there%zone%", "send %xt%zm%whisper%1%psst%Dee%"],
            (await session.GameHost.CallsAsync()).Where(c => c.StartsWith("send %xt%zm%message%", StringComparison.Ordinal) || c.StartsWith("send %xt%zm%whisper%", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Chat_send_refuses_before_login_and_text_that_would_break_the_packet()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);

        ControlException notPlaying = await Assert.ThrowsAsync<ControlException>(() => session.Connection.ChatSendAsync("hi", cancellationToken: Ct));
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        ControlException percent = await Assert.ThrowsAsync<ControlException>(() => session.Connection.ChatSendAsync("100% done", cancellationToken: Ct));
        ControlException empty = await Assert.ThrowsAsync<ControlException>(() => session.Connection.ChatSendAsync("  ", cancellationToken: Ct));
        ControlException name = await Assert.ThrowsAsync<ControlException>(() => session.Connection.ChatSendAsync("hi", "a%b", Ct));

        Assert.Equal(ErrorCode.NotLoggedIn, notPlaying.Code);
        Assert.Equal([ErrorCode.InvalidArgument, ErrorCode.InvalidArgument, ErrorCode.InvalidArgument], [percent.Code, empty.Code, name.Code]);
        Assert.DoesNotContain(await session.GameHost.CallsAsync(), c => c.StartsWith("send %xt%zm%message%", StringComparison.Ordinal) || c.StartsWith("send %xt%zm%whisper%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skua_chat_send_and_whisper_send_through_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture session = await GameFixture.StartAsync(sandbox);
        await session.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        ProcessResult send = await sandbox.RunCliAsync("chat", "send", "hello there");
        ProcessResult whisper = await sandbox.RunCliAsync("chat", "whisper", "Dee", "psst", "--json");

        Assert.Equal(0, send.ExitCode);
        Assert.Equal("Sent to zone: hello there", send.Stdout.Trim());
        Assert.Equal(0, whisper.ExitCode);
        Assert.Equal("Dee", JsonDocument.Parse(whisper.Stdout).RootElement.GetProperty("to").GetString());
        await session.GameHost.WaitForCallAsync("send %xt%zm%whisper%1%psst%Dee%");
    }

    /// <summary>A game entry as <c>channel from: text</c>, with <c>→to</c> after a whisper's sender.</summary>
    private static string Describe(LogEntryDto entry)
    {
        JsonElement data = entry.Data!.Value;
        string to = data.TryGetProperty("to", out JsonElement value) ? $"→{value.GetString()}" : "";
        return $"{data.GetProperty("channel").GetString()} {data.GetProperty("from").GetString()}{to}: {entry.Text}";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
