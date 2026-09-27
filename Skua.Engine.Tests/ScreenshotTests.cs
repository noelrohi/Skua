using System.Buffers.Binary;
using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

public class ScreenshotTests
{
    /// <summary>The Game Client's stage size, which the fake Game Host captures at.</summary>
    private const int StageWidth = 958;
    private const int StageHeight = 550;

    [Fact]
    public async Task A_screenshot_is_a_PNG_of_the_stage_at_its_native_size()
    {
        await using EngineSandbox sandbox = new();
        (_, EngineConnection connection) = await sandbox.StartEngineAsync();
        using (connection)
        {
            ScreenshotResult shot = await connection.ScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal((StageWidth, StageHeight), (shot.Width, shot.Height));
            Assert.Equal((StageWidth, StageHeight), PngSize(shot.Png));
            Assert.True(shot.Frame > 0);
        }
    }

    [Fact]
    public async Task MaxWidth_scales_a_wider_frame_down_keeping_its_aspect_ratio()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).LogCalls();
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            ScreenshotResult small = await connection.ScreenshotAsync(479, TestContext.Current.CancellationToken);
            ScreenshotResult wide = await connection.ScreenshotAsync(2000, TestContext.Current.CancellationToken);

            Assert.Equal((479, 275), (small.Width, small.Height));
            Assert.Equal((479, 275), PngSize(small.Png));
            Assert.Equal((StageWidth, StageHeight), (wide.Width, wide.Height));
            Assert.Equal(["screenshot 479", "screenshot 2000"], await gameHost.CallsAsync());
        }
    }

    [Fact]
    public async Task Concurrent_screenshots_of_the_same_size_share_one_capture()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).LogCalls().Delay("screenshot", 500);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            ScreenshotResult[] shots = await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ =>
            {
                using EngineConnection other = await sandbox.ConnectAsync();
                return await other.ScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken);
            }));

            Assert.Single(shots.Select(s => s.Frame).Distinct());
            Assert.Equal(["screenshot 0"], await gameHost.CallsAsync());
        }
    }

    [Fact]
    public async Task A_screenshot_of_another_size_waits_for_the_capture_in_flight()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).LogCalls().Delay("screenshot", 1000);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            Task<ScreenshotResult> native = connection.ScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken);
            await WaitForCallsAsync(gameHost, 1);
            Task<ScreenshotResult> small = connection.ScreenshotAsync(100, TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.False(native.IsCompleted);
            Assert.Equal(["screenshot 0"], await gameHost.CallsAsync());
            Assert.Equal(100, (await small).Width);
            Assert.Equal(StageWidth, (await native).Width);
            Assert.Equal(["screenshot 0", "screenshot 100"], await gameHost.CallsAsync());
        }
    }

    [Fact]
    public async Task A_screenshot_without_a_Game_Host_fails_with_GameHostDown()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Exit(1);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            await WaitForAsync(async () => !(await connection.StatusAsync(TestContext.Current.CancellationToken)).Game.GameHostUp);

            ControlException error = await Assert.ThrowsAsync<ControlException>(
                () => connection.ScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCode.GameHostDown, error.Code);
        }
    }

    [Fact]
    public async Task A_Game_Host_that_captures_no_image_fails_the_screenshot_with_GameHostDown()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).NoImage();
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            ControlException error = await Assert.ThrowsAsync<ControlException>(
                () => connection.ScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCode.GameHostDown, error.Code);
            Assert.Contains("couldn't capture", error.Message);
        }
    }

    [Fact]
    public async Task A_screenshot_the_Game_Host_never_answers_fails_with_Timeout_after_10_s()
    {
        await using EngineSandbox sandbox = new();
        FakeGameHost gameHost = new FakeGameHost(sandbox).Delay("screenshot", 60_000);
        (_, EngineConnection connection) = await sandbox.StartEngineAsync(gameHost.Environment());
        using (connection)
        {
            Stopwatch waited = Stopwatch.StartNew();
            ControlException error = await Assert.ThrowsAsync<ControlException>(
                () => connection.ScreenshotAsync(cancellationToken: TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCode.Timeout, error.Code);
            Assert.InRange(waited.Elapsed.TotalSeconds, 9.5, 20);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_maxWidth_below_1_is_an_invalid_argument(int maxWidth)
    {
        await using EngineSandbox sandbox = new();
        (_, EngineConnection connection) = await sandbox.StartEngineAsync();
        using (connection)
        {
            ControlException error = await Assert.ThrowsAsync<ControlException>(
                () => connection.ScreenshotAsync(maxWidth, TestContext.Current.CancellationToken));

            Assert.Equal(ErrorCode.InvalidArgument, error.Code);
        }
    }

    private static Task WaitForCallsAsync(FakeGameHost gameHost, int count) =>
        WaitForAsync(async () => (await gameHost.CallsAsync()).Length >= count);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 200 && !await condition(); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
    }

    /// <summary>The width and height in a PNG's IHDR chunk, after checking the PNG signature.</summary>
    internal static (int Width, int Height) PngSize(byte[] png)
    {
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal("IHDR"u8.ToArray(), png[12..16]);
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }
}
