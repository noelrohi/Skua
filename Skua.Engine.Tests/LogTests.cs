using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

public class LogTests
{
    [Fact]
    public async Task The_event_log_starts_with_engine_started()
    {
        await using EngineSandbox sandbox = new();
        using EngineConnection connection = await sandbox.ConnectAsync();

        LogPage page = await connection.LogsAsync(LogKind.Events, cancellationToken: Ct);

        Assert.False(page.Gap);
        LogEntryDto first = page.Entries[0];
        Assert.Equal(EventTypes.EngineStarted, first.Type);
        Assert.Equal(LogKind.Events, first.Kind);
        Assert.Null(first.Run);
        Assert.Null(first.Text);
        Assert.InRange(first.Ts, DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(connection.Hello.Pid, first.Data!.Value.GetProperty("pid").GetInt32());
        Assert.Equal("default", first.Data!.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Logs_pages_through_every_entry_without_gaps_or_repeats()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Repeat(450, "send F trace {i}");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 450);

            List<string> texts = [];
            string? cursor = null;
            while (true)
            {
                LogPage page = await connection.LogsAsync(LogKind.Flash, cursor, cancellationToken: Ct);
                Assert.False(page.Gap);
                Assert.InRange(page.Entries.Count, 0, 200);
                texts.AddRange(page.Entries.Select(entry => entry.Text!));
                cursor = page.Next;
                if (page.Entries.Count == 0)
                    break;
            }

            Assert.Equal(Enumerable.Range(0, 450).Select(i => $"trace {i}"), texts);
        }
    }

    [Fact]
    public async Task Logs_tail_returns_the_newest_entries_after_the_cursor_in_seq_order()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Repeat(450, "send F trace {i}");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 450);

            LogPage tail = await connection.LogsAsync(LogKind.Flash, null, null, 3, Ct);
            LogPage after = await connection.LogsAsync(LogKind.Flash, max: 448, cancellationToken: Ct);
            LogPage tailAfter = await connection.LogsAsync(LogKind.Flash, after.Next, null, 5, Ct);
            LogPage next = await connection.LogsAsync(LogKind.Flash, tail.Next, cancellationToken: Ct);
            ControlException both = await Assert.ThrowsAsync<ControlException>(() => connection.LogsAsync(LogKind.Flash, null, 5, 5, Ct));

            Assert.Equal(["trace 447", "trace 448", "trace 449"], tail.Entries.Select(e => e.Text));
            Assert.False(tail.Gap);
            Assert.Equal(["trace 448", "trace 449"], tailAfter.Entries.Select(e => e.Text));
            Assert.Empty(next.Entries);
            Assert.Equal(ErrorCode.InvalidArgument, both.Code);
        }
    }

    [Fact]
    public async Task Kind_all_merges_every_kind_in_seq_order()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Send('F', "a trace").Log(2, "a warning");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 1);

            LogPage page = await connection.LogsAsync(LogKind.All, max: 1000, cancellationToken: Ct);

            Assert.Equal(page.Entries.Select(e => e.Seq).Order(), page.Entries.Select(e => e.Seq));
            Assert.Equal(page.Entries.Count, page.Entries.Select(e => e.Seq).Distinct().Count());
            Assert.Contains(page.Entries, e => e.Kind == LogKind.Events && e.Type == EventTypes.GameHostStarted);
            Assert.Contains(page.Entries, e => e.Kind == LogKind.Flash && e.Text == "a trace");
            Assert.Contains(page.Entries, e => e.Kind == LogKind.Debug && e.Text!.Contains("a warning"));
        }
    }

    [Fact]
    public async Task After_eviction_logs_reports_a_gap_and_resumes_from_the_oldest_entry_held()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Control();
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            // The Engine's own line about the Game Host comes before the cursor, so only the Game Host's lines come after it.
            await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.StartsWith("Game Host started"));
            string before = (await connection.LogsAsync(LogKind.Debug, cancellationToken: Ct)).Next;
            await gameHost.DoAsync("repeat 10050 log 2 line {i}");
            await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.EndsWith("line 10049"));

            List<LogEntryDto> held = [];
            LogPage page = await connection.LogsAsync(LogKind.Debug, before, 1000, Ct);
            Assert.True(page.Gap);
            while (page.Entries.Count > 0)
            {
                held.AddRange(page.Entries);
                page = await connection.LogsAsync(LogKind.Debug, page.Next, 1000, Ct);
                Assert.False(page.Gap);
            }

            Assert.Equal(10_000, held.Count);
            Assert.EndsWith("line 10049", held[^1].Text);
            Assert.EndsWith("line 50", held[0].Text);
            Assert.True((await connection.LogsAsync(LogKind.Debug, cancellationToken: Ct)).Gap);
            LogPage tail = await connection.LogsAsync(LogKind.Debug, null, null, 5, Ct);
            Assert.False(tail.Gap);
            Assert.EndsWith("line 10049", tail.Entries[^1].Text);
        }
    }

    [Fact]
    public async Task A_cursor_from_before_an_Engine_restart_reports_a_gap()
    {
        await using EngineSandbox sandbox = new();
        string cursor;
        using (EngineConnection first = await sandbox.ConnectAsync())
            cursor = (await first.LogsAsync(LogKind.Events, cancellationToken: Ct)).Next;
        await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, Ct);

        using EngineConnection second = await sandbox.ConnectAsync();
        LogPage page = await second.LogsAsync(LogKind.Events, cursor, cancellationToken: Ct);

        Assert.True(page.Gap);
        Assert.Equal(EventTypes.EngineStarted, page.Entries[0].Type);
        Assert.Equal(second.Hello.Pid, page.Entries[0].Data!.Value.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task Subscribe_replays_from_a_pull_cursor_then_follows_live_entries_without_missing_any()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Control().Repeat(3, "send F early {i}");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 3);
            LogPage pulled = await connection.LogsAsync(LogKind.Flash, cancellationToken: Ct);
            Assert.Equal(["early 0", "early 1", "early 2"], pulled.Entries.Select(e => e.Text));
            // Recorded after the pull and before the subscribe, so the subscribe replays them.
            await gameHost.DoAsync("repeat 3 send F between {i}");
            await connection.WaitForLogsAsync(LogKind.Flash, 6);

            List<string> followed = [];
            bool live = false;
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            stop.CancelAfter(TimeSpan.FromSeconds(20));
            await foreach (LogPage page in connection.SubscribeAsync([LogKind.Flash], pulled.Next, stop.Token))
            {
                Assert.False(page.Gap);
                followed.AddRange(page.Entries.Select(e => e.Text!));
                // Recorded once the replay has arrived, so the subscribe follows them live.
                if (!live && followed.Count >= 3)
                {
                    live = true;
                    await gameHost.DoAsync("repeat 3 send F live {i}");
                }
                if (followed.Count >= 6)
                    break;
            }

            Assert.Equal(["between 0", "between 1", "between 2", "live 0", "live 1", "live 2"], followed);
        }
    }

    [Fact]
    public async Task Every_entry_is_also_written_to_the_JSONL_file_of_this_start()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Repeat(20, "send F trace {i}").Log(1, "an error");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Debug, 1, e => e.Text!.Contains("an error"));
            List<LogEntryDto> held = (await connection.LogsAsync(LogKind.All, max: 1000, cancellationToken: Ct)).Entries.ToList();

            string file = Assert.Single(Directory.GetFiles(sandbox.Endpoint.LogFilesDir, "*.jsonl"));
            List<LogEntryDto> written = (await File.ReadAllLinesAsync(file, Ct))
                .Select(line => JsonSerializer.Deserialize<LogEntryDto>(line, ControlJson.Options)!)
                .Take(held.Count)
                .ToList();

            Assert.Equal(held.Select(e => (e.Seq, e.Ts, e.Kind, e.Text, e.Type)), written.Select(e => (e.Seq, e.Ts, e.Kind, e.Text, e.Type)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public async Task Each_start_writes_a_new_JSONL_file_and_only_the_last_10_are_kept()
    {
        await using EngineSandbox sandbox = new();
        Directory.CreateDirectory(sandbox.Endpoint.LogFilesDir);
        string[] older = Enumerable.Range(0, 12).Select(i => Path.Combine(sandbox.Endpoint.LogFilesDir, $"2020-01-01T00-00-{i:00}.000Z.jsonl")).ToArray();
        foreach (string file in older)
            await File.WriteAllTextAsync(file, "", Ct);

        (await sandbox.ConnectAsync()).Dispose();
        await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, Ct);
        using EngineConnection connection = await sandbox.ConnectAsync();

        string[] kept = Directory.GetFiles(sandbox.Endpoint.LogFilesDir).Order().ToArray();
        Assert.Equal(10, kept.Length);
        Assert.Equal(older[^8..], kept[..8]);
        string newest = (await File.ReadAllLinesAsync(kept[^1], Ct))[0];
        Assert.Equal(connection.Hello.Pid, JsonSerializer.Deserialize<LogEntryDto>(newest, ControlJson.Options)!.Data!.Value.GetProperty("pid").GetInt32());
    }

    [Fact]
    public async Task An_oversized_text_is_cut_to_16_KB_on_a_character_boundary_and_flagged()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Send('F', new string('€', 7000)).Send('F', "short");
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            List<LogEntryDto> entries = await connection.WaitForLogsAsync(LogKind.Flash, 2);

            Assert.True(entries[0].Truncated);
            Assert.InRange(Encoding.UTF8.GetByteCount(entries[0].Text!), 16 * 1024 - 2, 16 * 1024);
            Assert.All(entries[0].Text!, c => Assert.Equal('€', c));
            Assert.False(entries[1].Truncated);
            Assert.Equal("short", entries[1].Text);
        }
    }

    [Fact]
    public async Task A_logs_reply_stays_within_1_MB_and_the_next_page_continues_it()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Repeat(100, "send F {i} " + new string('x', 15_000));
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 100);

            LogPage first = await connection.LogsAsync(LogKind.Flash, max: 1000, cancellationToken: Ct);
            LogPage second = await connection.LogsAsync(LogKind.Flash, first.Next, 1000, Ct);

            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(first, ControlJson.Options).Length, 900_000, 1024 * 1024);
            Assert.Equal(Enumerable.Range(0, 100).Select(i => $"{i} "), first.Entries.Concat(second.Entries).Select(e => e.Text![..(e.Text!.IndexOf(' ') + 1)]));
            Assert.All(first.Entries, e => Assert.False(e.Truncated));
        }
    }

    [Fact]
    public async Task A_registered_secret_never_reaches_the_rings_the_file_subscribe_or_stderr()
    {
        const string secret = "hunter2-SECRET";
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox)
            .Send('F', $"login failed for password {secret}")
            .Log(2, $"{secret}{secret}")
            .Send('F', new string('x', 16 * 1024 - 4) + secret)
            .Send('F', "done");
        IDictionary<string, string> environment = gameHost.Environment();
        environment["SKUA_REDACT"] = $"{secret}\nanother-secret";
        string swf = Path.Combine(sandbox.SkuaDir, secret, "skua.swf");
        Directory.CreateDirectory(Path.GetDirectoryName(swf)!);
        await File.WriteAllBytesAsync(swf, [], Ct);
        environment["SKUA_SWF"] = swf;
        (Process engine, EngineConnection connection) = await sandbox.StartEngineAsync(environment);
        using (connection)
        {
            await connection.WaitForLogsAsync(LogKind.Flash, 1, e => e.Text == "done");

            LogPage all = await connection.LogsAsync(LogKind.All, max: 1000, cancellationToken: Ct);
            LogPage replayed = await connection.SubscribeAsync([LogKind.All], cancellationToken: Ct).FirstAsync(Ct);

            // Redaction runs before the size cap, so a cut never leaves part of a secret behind.
            Assert.DoesNotContain("hunt", JsonSerializer.Serialize(all, ControlJson.Options));
            Assert.DoesNotContain("hunt", JsonSerializer.Serialize(replayed, ControlJson.Options));
            Assert.Contains(all.Entries, e => e.Text == "login failed for password [redacted]");
            Assert.Contains(all.Entries, e => e.Type == EventTypes.GameHostStarted && e.Data!.Value.GetProperty("swf").GetString() == Path.Combine(sandbox.SkuaDir, "[redacted]", "skua.swf"));
        }

        await EngineClient.StopAsync(sandbox.Endpoint, EngineSandbox.StopTimeout, Ct);
        string file = Assert.Single(Directory.GetFiles(sandbox.Endpoint.LogFilesDir));
        Assert.DoesNotContain("hunt", await File.ReadAllTextAsync(file, Ct));
        Assert.DoesNotContain("hunt", await engine.StandardError.ReadToEndAsync(Ct));
    }

    [Fact]
    public async Task The_Game_Host_lifecycle_and_a_broken_Bridge_are_events()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Corrupt().Sleep(100).Exit(3);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            // The Bridge's reader and the process's exit are separate threads, so the error and the exit can be logged in either order.
            await connection.WaitForLogsAsync(LogKind.Events, 2, e => e.Type is EventTypes.BridgeError or EventTypes.GameHostExited);
            IReadOnlyList<LogEntryDto> events = (await connection.LogsAsync(LogKind.Events, cancellationToken: Ct)).Entries;

            Assert.Equal([EventTypes.EngineStarted, EventTypes.GameHostStarted], events.Take(2).Select(e => e.Type));
            Assert.Equal([EventTypes.BridgeError, EventTypes.GameHostExited], events.Skip(2).Select(e => e.Type).Order());
            Assert.Equal(await gameHost.PidAsync(), events[1].Data!.Value.GetProperty("pid").GetInt32());
            Assert.Equal(EngineSandbox.FakeGameHostExecutable, events[1].Data!.Value.GetProperty("executable").GetString());
            Assert.Contains("invalid length", events.Single(e => e.Type == EventTypes.BridgeError).Data!.Value.GetProperty("error").GetString());
            Assert.Equal(3, events.Single(e => e.Type == EventTypes.GameHostExited).Data!.Value.GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task An_Engine_that_cannot_write_its_log_file_still_serves_its_logs()
    {
        await using EngineSandbox sandbox = new();
        Directory.CreateDirectory(Path.GetDirectoryName(sandbox.Endpoint.LogFilesDir)!);
        await File.WriteAllTextAsync(sandbox.Endpoint.LogFilesDir, "a file where the folder should be", Ct);

        using EngineConnection connection = await sandbox.ConnectAsync();
        LogPage page = await connection.LogsAsync(LogKind.All, cancellationToken: Ct);

        Assert.Equal(EventTypes.EngineStarted, page.Entries[0].Type);
        Assert.Contains(page.Entries, e => e.Text?.StartsWith("Not writing a log file") == true);
    }

    [Fact]
    public async Task A_malformed_cursor_is_an_invalid_argument()
    {
        await using EngineSandbox sandbox = new();
        using EngineConnection connection = await sandbox.ConnectAsync();

        ControlException error = await Assert.ThrowsAsync<ControlException>(() => connection.LogsAsync(LogKind.All, "not-a-cursor", cancellationToken: Ct));

        Assert.Equal(ErrorCode.InvalidArgument, error.Code);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
