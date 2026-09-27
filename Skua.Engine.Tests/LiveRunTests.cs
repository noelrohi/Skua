using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The live-game harness, dry-run against the fake Game Host with 2-second minutes, and its measurements.</summary>
public class LiveRunTests
{
    private static readonly TimeSpan DrySampleInterval = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_dry_run_of_the_smoke_test_samples_every_minute_and_fails_only_on_the_fakes_blank_screenshots()
    {
        await using DryRun dry = DryRun.Start();

        LiveRunResult result = await LiveRun.RunAsync("smoke", dry.Environment, dry.Options,
            run => LiveScenarios.SmokeAsync(run, "Galanoth", LiveScenarios.Leveling, battleonMinutes: 2, scriptMinutes: 3));

        // The fake Game Host captures a flat colour, which the screenshot check calls blank; nothing else fails.
        Assert.Equal(["battleon", "leveling"], result.Failures.Select(f => f.Split(' ')[1]));
        Assert.All(result.Failures, f => Assert.Contains("the frame is blank", f));
        Assert.Equal(["battleon", "battleon", "leveling", "leveling", "leveling"], result.Samples.Select(s => s.Phase));
        Assert.All(result.Samples, s =>
        {
            Assert.True(s.GameHost.FootprintMb > 0);
            Assert.True(s.Engine.FootprintMb > 0);
            Assert.True(s.GameHost.RssMb > 0);
            Assert.NotEmpty(s.GetterMs);
        });
        Assert.All(result.Samples.Where(s => s.Phase == "leveling").Skip(1), s => Assert.True(s.ScriptLines > 0));
        Assert.All(result.Samples, s => Assert.Equal(("battleon", 5000), (s.Player!.Map, s.Player.Gold)));
        Assert.NotEmpty(result.Stats);
        string report = await File.ReadAllTextAsync(Path.Combine(result.OutDir, "report.txt"), TestContext.Current.CancellationToken);
        Assert.Contains("logged in on Galanoth", report);
        Assert.Contains("summary leveling: 3 samples", report);
        Assert.True(File.Exists(Path.Combine(result.OutDir, "screenshot-battleon.png")));
    }

    [Fact]
    public async Task A_failed_run_leaves_a_screenshot_and_the_logs_since_its_start_in_its_output()
    {
        await using DryRun dry = DryRun.Start();

        LiveRunResult result = await LiveRun.RunAsync("death", dry.Environment, dry.Options, async run =>
        {
            await run.LoginAsync("Galanoth");
            await dry.GameHost.DoAsync("die");
            await run.PhaseAsync(new LivePhase("battleon", 2));
        });

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.StartsWith("player.death", StringComparison.Ordinal));
        byte[] screenshot = await File.ReadAllBytesAsync(Path.Combine(result.OutDir, "failure-screenshot.png"), TestContext.Current.CancellationToken);
        Assert.Equal((LiveMetrics.StageWidth, LiveMetrics.StageHeight), (LiveMetrics.DecodePng(screenshot)!.Width, LiveMetrics.DecodePng(screenshot)!.Height));
        string[] logs = await File.ReadAllLinesAsync(Path.Combine(result.OutDir, "logs-all.jsonl"), TestContext.Current.CancellationToken);
        List<LogEntryDto> entries = logs.Select(l => JsonSerializer.Deserialize<LogEntryDto>(l, ControlJson.Options)!).ToList();
        Assert.Equal(EventTypes.EngineStarted, entries[0].Type);
        Assert.Contains(entries, e => e.Type == EventTypes.PlayerDeath);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(result.OutDir, "engine-logs")));
        Assert.DoesNotContain(dry.Keychain.Password, string.Join('\n', logs));
    }

    [Fact]
    public async Task A_disconnect_ends_the_run_at_once_and_stops_the_Script_so_nothing_logs_in_again()
    {
        await using DryRun dry = DryRun.Start();
        ScriptStatusDto? after = null;

        LiveRunResult result = await LiveRun.RunAsync("disconnect", dry.Environment, dry.Options, async run =>
        {
            try
            {
                await run.LoginAsync("Galanoth");
                await run.StartScriptAsync(LiveScenarios.Leveling);
                await dry.GameHost.DoAsync("lose-connection Connection lost");
                await run.PhaseAsync(new LivePhase("leveling", 30, Script: true));
            }
            finally
            {
                await Task.Delay(500, TestContext.Current.CancellationToken);
                after = await run.Connection.ScriptStatusAsync(TestContext.Current.CancellationToken);
            }
        });

        Assert.Contains(result.Failures, f => f.StartsWith(EventTypes.GameDisconnected, StringComparison.Ordinal));
        Assert.True(result.Samples.Count < 3, $"The run went on for {result.Samples.Count} samples.");
        Assert.Equal(ScriptState.Idle, after!.State);
        Assert.Equal(1, dry.Keychain.Reads);
    }

    [Fact]
    public async Task A_dry_run_of_hidden_running_checks_the_lock_the_tick_rate_and_that_the_player_progresses()
    {
        await using DryRun dry = DryRun.Start();

        LiveRunResult result = await LiveRun.RunAsync("hidden", dry.Environment, dry.Options, async run =>
        {
            await run.LoginAsync("Galanoth");
            await run.StartScriptAsync(LiveScenarios.Leveling);
            await run.PhaseAsync(new LivePhase("hidden", LiveRun.ProgressWindow + 2, Script: true, Hidden: true));
        });

        // The screen isn't locked nor the display asleep, and the fake player's gold never changes, in a full window and the short one after it;
        // the fake Game Host reports 30 fps and ticks 1000/s with 35 ms gaps.
        Assert.Equal(LiveRun.ProgressWindow + 2, result.Failures.Count(f => f.EndsWith("the screen wasn't locked and the display was awake.", StringComparison.Ordinal)));
        Assert.Equal(["hidden minutes 0–5:", "hidden minutes 5–6:"],
            result.Failures.Where(f => f.Contains("5000 → 5000 gold", StringComparison.Ordinal)).Select(f => string.Join(' ', f.Split(' ')[..3])));
        Assert.DoesNotContain(result.Failures, f => f.Contains("tick", StringComparison.Ordinal) || f.Contains("fps", StringComparison.Ordinal));
        Assert.Contains("fps, ticks 1000–1000/s, largest tick gap 35 ms",
            await File.ReadAllTextAsync(Path.Combine(result.OutDir, "report.txt"), TestContext.Current.CancellationToken));
        Assert.True(result.Stats.Count >= 2);
    }

    [Fact]
    public async Task A_failed_login_ends_the_run_without_trying_again()
    {
        await using DryRun dry = DryRun.Start(g => g.Reject("Galanoth", "Server is Full"));

        LiveRunResult result = await LiveRun.RunAsync("rejected", dry.Environment, dry.Options, async run =>
        {
            await run.LoginAsync("Galanoth");
            await run.PhaseAsync(new LivePhase("battleon", 5));
        });

        Assert.Single(result.Failures, f => f.StartsWith("login on Galanoth failed with LoginFailed", StringComparison.Ordinal));
        Assert.Empty(result.Samples);
    }

    [Fact]
    public void Seeding_a_data_folder_answers_CoreBots_one_time_Questions_and_copies_the_Scripts_but_not_git()
    {
        DirectoryInfo dir = Directory.CreateTempSubdirectory("skua-live-");
        string checkout = Directory.CreateDirectory(Path.Combine(dir.FullName, "checkout")).FullName;
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        File.WriteAllText(Path.Combine(checkout, ".git", "HEAD"), "ref");
        Directory.CreateDirectory(Path.Combine(checkout, "Farm"));
        File.WriteAllText(Path.Combine(checkout, "Farm", "Leveling.cs"), "// leveling");
        string skuaDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "skua")).FullName;

        LiveRun.SeedDataFolder(skuaDir, checkout);

        Assert.Contains("genericDataConsent: False", File.ReadAllLines(Path.Combine(skuaDir, "DataCollectionSettings.txt")));
        Assert.Contains("discordV11", File.ReadAllLines(Path.Combine(skuaDir, "OneTimeMessages.txt")));
        Assert.Equal("// leveling", File.ReadAllText(Path.Combine(skuaDir, "Scripts", "Farm", "Leveling.cs")));
        Assert.False(Directory.Exists(Path.Combine(skuaDir, "Scripts", ".git")));
        dir.Delete(recursive: true);
    }

    [Fact]
    public void Footprint_reads_the_total_that_macOS_footprint_prints()
    {
        Assert.Equal(1433.6, LiveMetrics.ParseFootprintMb("zsh [99607]: 64-bit    Footprint: 1.4 GB (16384 bytes per page)")!.Value, 1);
        Assert.Equal(2.1875, LiveMetrics.ParseFootprintMb("zsh [99607]: 64-bit    Footprint: 2240 KB (16384 bytes per page)"));
        Assert.Equal(512, LiveMetrics.ParseFootprintMb("Footprint: 512 MB"));
        Assert.Null(LiveMetrics.ParseFootprintMb("footprint: process 123 not found"));
    }

    [Fact]
    public async Task Footprint_and_RSS_of_a_running_process_are_read_and_those_of_an_exited_one_are_null()
    {
        Assert.True(await LiveMetrics.FootprintMbAsync(Environment.ProcessId) > 1);
        Assert.True(await LiveMetrics.RssMbAsync(Environment.ProcessId) > 1);
        Assert.Null(await LiveMetrics.FootprintMbAsync(999_999));
        Assert.Null(await LiveMetrics.RssMbAsync(999_999));
        Assert.True(await LiveMetrics.LoadAverageAsync() >= 0);
    }

    [Fact]
    public void Percentiles_are_by_nearest_rank_and_the_slope_is_least_squares()
    {
        double[] latencies = [.. Enumerable.Range(1, 100).Select(i => (double)i)];

        Assert.Equal(51, LiveMetrics.Percentile(latencies, 0.5));
        Assert.Equal(100, LiveMetrics.Percentile(latencies, 0.99));
        Assert.True(double.IsNaN(LiveMetrics.Percentile([], 0.99)));
        Assert.Equal(2, LiveMetrics.Slope([(0, 1001), (1, 1001), (2, 1003), (3, 1007)]), 6);
        Assert.Equal(0, LiveMetrics.Slope([(0, 1000)]));
    }

    [Fact]
    public void The_screen_counts_as_locked_only_when_ioreg_says_so()
    {
        Assert.True(LiveMetrics.ParseScreenLocked("<dict><key>CGSSessionScreenIsLocked</key>\n\t\t\t<true/><key>kCGSSessionOnConsoleKey</key><true/></dict>"));
        Assert.False(LiveMetrics.ParseScreenLocked("<dict><key>kCGSSessionOnConsoleKey</key><true/></dict>"));
        Assert.False(LiveMetrics.ParseScreenLocked("<dict><key>CGSSessionScreenIsLocked</key><false/></dict>"));
    }

    [Fact]
    public void Game_Host_stats_lines_give_its_tick_rate_and_largest_tick_gap()
    {
        GameHostStats first = LiveMetrics.ParseStats(1, """[gamehost] stats {"uptimeMs":60000,"ticks":1800,"frameRate":30,"maxTickGapMs":40,"threaded":true}""")!;
        GameHostStats second = LiveMetrics.ParseStats(2, """[gamehost] stats {"uptimeMs":120000,"ticks":3900,"frameRate":30,"maxTickGapMs":35}""")!;

        Assert.Equal(35, second.MaxTickGapMs);
        Assert.Equal(35, LiveMetrics.TicksPerSecond(first, second));
        Assert.Null(LiveMetrics.ParseStats(3, "[gamehost] wgpu: something"));
        Assert.Null(LiveMetrics.ParseStats(3, "[gamehost] stats {broken"));
    }

    [Fact]
    public void A_screenshot_is_correct_only_at_the_stage_size_and_showing_something()
    {
        byte[] scene = Png(LiveMetrics.StageWidth, LiveMetrics.StageHeight, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y)), filter: 1);
        byte[] blank = Png(LiveMetrics.StageWidth, LiveMetrics.StageHeight, (_, _) => (0, 0, 0), filter: 0);
        byte[] small = Png(479, 275, (x, y) => ((byte)x, (byte)y, 7), filter: 2);

        Assert.Null(LiveMetrics.ScreenshotProblem(scene));
        Assert.Equal(((byte)5, (byte)3, (byte)6), Pixel(LiveMetrics.DecodePng(scene)!, 5, 3));
        Assert.Contains("the frame is blank", LiveMetrics.ScreenshotProblem(blank));
        Assert.Contains("479x275", LiveMetrics.ScreenshotProblem(small));
        Assert.Contains("isn't a PNG", LiveMetrics.ScreenshotProblem(Encoding.UTF8.GetBytes("not a png")));
    }

    private static (byte, byte, byte) Pixel(Image image, int x, int y)
    {
        int at = (y * image.Width + x) * image.Channels;
        return (image.Pixels[at], image.Pixels[at + 1], image.Pixels[at + 2]);
    }

    /// <summary>An RGBA PNG whose rows all use <paramref name="filter"/> (0 none, 1 sub, 2 up).</summary>
    private static byte[] Png(int width, int height, Func<int, int, (byte R, byte G, byte B)> colour, byte filter)
    {
        byte[] raw = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            (byte r, byte g, byte b) = colour(x, y);
            int at = (y * width + x) * 4;
            (raw[at], raw[at + 1], raw[at + 2], raw[at + 3]) = (r, g, b, 255);
        }

        using MemoryStream rows = new();
        using (ZLibStream deflate = new(rows, CompressionLevel.Fastest, leaveOpen: true))
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                deflate.WriteByte(filter);
                for (int i = 0; i < stride; i++)
                {
                    int value = raw[y * stride + i];
                    int predictor = filter switch
                    {
                        1 => i >= 4 ? raw[y * stride + i - 4] : 0,
                        2 => y > 0 ? raw[(y - 1) * stride + i] : 0,
                        _ => 0,
                    };
                    deflate.WriteByte((byte)(value - predictor));
                }
            }
        }

        using MemoryStream png = new();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", rows.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A chunk with a zero CRC, which the decoder doesn't check.</summary>
    private static void Chunk(Stream png, string type, byte[] data)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        png.Write(Encoding.ASCII.GetBytes(type));
        png.Write(data);
        png.Write(new byte[4]);
    }

    /// <summary>An Engine environment with the fake game, servers API and Keychain, the stats every second, and a Scripts checkout.</summary>
    private sealed class DryRun : IAsyncDisposable
    {
        private readonly EngineSandbox _files;
        private readonly FakeAqApi _api;

        private DryRun(EngineSandbox files, FakeAqApi api, FakeKeychain keychain, FakeGameHost gameHost, Dictionary<string, string> environment, LiveRunOptions options)
        {
            _files = files;
            _api = api;
            Keychain = keychain;
            GameHost = gameHost;
            Environment = environment;
            Options = options;
        }

        public FakeKeychain Keychain { get; }

        public FakeGameHost GameHost { get; }

        public Dictionary<string, string> Environment { get; }

        public LiveRunOptions Options { get; }

        public static DryRun Start(Func<FakeGameHost, FakeGameHost>? configure = null)
        {
            // The fakes, the checkout and the output live in a sandbox of their own; the run starts its Engine in another.
            EngineSandbox files = new();
            FakeAqApi api = new(GameFixture.Servers);
            FakeKeychain keychain = new(files);
            FakeGameHost gameHost = new FakeGameHost(files).Game(keychain, GameFixture.Servers)
                .Stats("""{"uptimeMs":{n}000,"ticks":{n}000,"frameRate":30,"maxTickGapMs":35}""");
            gameHost = configure?.Invoke(gameHost) ?? gameHost;
            Dictionary<string, string> environment = GameFixture.Environment(gameHost, api, keychain);
            environment["SKUA_GAMEHOST_STATS_SEC"] = "1";

            string checkout = Path.Combine(files.SkuaDir, "checkout");
            Directory.CreateDirectory(Path.Combine(checkout, "Farm"));
            File.WriteAllText(Path.Combine(checkout, "Farm", "Leveling.cs"), TestScripts.Main("""
                while (!bot.ShouldExit)
                {
                    bot.Log("levelling");
                    Thread.Sleep(200);
                }
                """));
            LiveRunOptions options = new()
            {
                SampleInterval = DrySampleInterval,
                OutDir = Path.Combine(files.SkuaDir, "out"),
                ScriptsCheckout = checkout,
                // Visible, whatever the Mac's own screen and display are doing.
                Visibility = () => Task.FromResult((false, false)),
            };
            return new DryRun(files, api, keychain, gameHost, environment, options);
        }

        public async ValueTask DisposeAsync()
        {
            await _api.DisposeAsync();
            await _files.DisposeAsync();
        }
    }
}
