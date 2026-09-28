using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Skua.Avalonia.Tests;

/// <summary>The Game View against the fake Game Host: frames in, clicks and keys out, live only while its window shows.</summary>
[Collection(nameof(GameViewTests))]
public sealed class GameViewTests(AppEngine app)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task Shows_the_Game_Hosts_frames_as_they_advance()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {

            await PumpUntilAsync(() => view.FrameNumber > 0, "a first frame");
            long first = view.FrameNumber;
            long drawn = view.Stats.Total;
            await PumpUntilAsync(() => view.FrameNumber >= first + 3, "the frame number to advance");
            // A frame counts in the stats once drawn, and renders may skip frames the view copied, so wait for a draw rather than count.
            await PumpUntilAsync(() => view.Stats.Total > drawn, "a newer frame to be drawn");

            // The fake's frame n is a solid colour whose red, green and blue are n's low, middle and high bytes.
            using WriteableBitmap? frame = window.CaptureRenderedFrame() as WriteableBitmap;
            Assert.NotNull(frame);
            long shown = view.FrameNumber;
            Assert.Equal(((byte)shown, (byte)(shown >> 8), (byte)(shown >> 16)), Pixel(frame!, 479, 275));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Keeps_showing_frames_after_the_Game_Host_restarts()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            await PumpUntilAsync(() => view.FrameNumber >= 60, "frames from the first Game Host");
            long before = view.FrameNumber;

            // A new Game Host has a new Frame Buffer, whose frame numbers start again at 1: the first of its frames the view shows is
            // numbered below the old count, unless the view ignores them until the new count passes the old one.
            app.Flash.InitializeFlash();

            await PumpUntilAsync(() => view.FrameNumber != before, "a frame from the restarted Game Host");
            Assert.True(view.FrameNumber < before, $"showed frame {view.FrameNumber} after {before}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(958, 550, 479, 275)]
    // Letterboxed: twice the stage's width, with bars of 250 above and below.
    [InlineData(1916, 1600, 958, 800)]
    // Pillarboxed: half the stage's height, with bars of 239.5 on either side.
    [InlineData(958, 275, 479, 137.5)]
    public async Task A_click_arrives_at_the_viewport_pixel_under_it(int width, int height, double x, double y)
    {
        (Window window, GameView view) = await ShowAsync(width, height);
        try
        {
            int from = AppEngine.Calls().Length;

            window.MouseDown(new Point(x, y), MouseButton.Left);
            window.MouseUp(new Point(x, y), MouseButton.Left);

            string[] calls = await WaitForCallsAsync(from, c => c.Contains("input mouseUp 479 275 left"));
            Assert.Equal(["input focusGained", "input mouseDown 479 275 left", "input mouseUp 479 275 left"],
                calls.Where(c => c is "input focusGained" or "input focusLost" || c.StartsWith("input mouseDown", StringComparison.Ordinal) || c.StartsWith("input mouseUp", StringComparison.Ordinal)));
            Assert.True(view.IsFocused, "the Game View takes focus on click");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Keys_and_typed_text_arrive_in_order()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            view.Focus();
            int from = AppEngine.Calls().Length;

            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            window.KeyTextInput("a");
            window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            window.KeyPress(Key.LeftShift, RawInputModifiers.Shift, PhysicalKey.ShiftLeft, null!);
            window.KeyPress(Key.B, RawInputModifiers.Shift, PhysicalKey.B, "B");
            window.KeyTextInput("B");
            window.KeyRelease(Key.B, RawInputModifiers.Shift, PhysicalKey.B, "B");
            window.KeyRelease(Key.LeftShift, RawInputModifiers.None, PhysicalKey.ShiftLeft, null!);
            window.KeyTextInput("é");
            window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, "\b");
            window.KeyRelease(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, "\b");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
            window.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");

            string[] calls = await WaitForCallsAsync(from, c => c.Contains("input keyUp Tab Tab"));
            Assert.Equal(
                [
                    "input keyDown KeyA a",
                    "input text a",
                    "input keyUp KeyA a",
                    "input keyDown ShiftLeft Shift left",
                    "input keyDown KeyB B",
                    "input text B",
                    "input keyUp KeyB B",
                    "input keyUp ShiftLeft Shift left",
                    "input text é",
                    "input keyDown Backspace Backspace",
                    "input textControl Backspace",
                    "input keyUp Backspace Backspace",
                    "input keyDown Tab Tab",
                    "input keyUp Tab Tab",
                ],
                calls.Where(c => c.StartsWith("input key", StringComparison.Ordinal) || c.StartsWith("input text", StringComparison.Ordinal)));
            Assert.True(view.IsFocused, "Tab goes to the game, not to Avalonia's focus navigation");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// On macOS, Avalonia raises no text input for a key down that was handled (Avalonia.Native's <c>AvnView keyDown</c>), so a key that
    /// types text must stay unhandled or nothing can be typed in the game. The headless platform doesn't model that, so this checks the
    /// contract itself.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Key.A, PhysicalKey.A, "a", KeyModifiers.None, false)]
    [InlineData(Key.A, PhysicalKey.A, "A", KeyModifiers.Shift, false)]
    [InlineData(Key.D7, PhysicalKey.Digit7, "7", KeyModifiers.None, false)]
    [InlineData(Key.Space, PhysicalKey.Space, " ", KeyModifiers.None, false)]
    [InlineData(Key.E, PhysicalKey.E, "é", KeyModifiers.Alt, false)]
    [InlineData(Key.Tab, PhysicalKey.Tab, "\t", KeyModifiers.None, true)]
    [InlineData(Key.Back, PhysicalKey.Backspace, "\b", KeyModifiers.None, true)]
    [InlineData(Key.Enter, PhysicalKey.Enter, "\r", KeyModifiers.None, true)]
    [InlineData(Key.Left, PhysicalKey.ArrowLeft, "\uF702", KeyModifiers.None, true)]
    [InlineData(Key.V, PhysicalKey.V, "v", KeyModifiers.Meta, true)]
    [InlineData(Key.LeftShift, PhysicalKey.ShiftLeft, null, KeyModifiers.Shift, true)]
    public async Task A_key_that_types_text_leaves_its_key_down_unhandled_so_macOS_raises_the_text(
        Key key, PhysicalKey physical, string? symbol, KeyModifiers modifiers, bool handled)
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            view.Focus();
            KeyEventArgs e = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physical, KeySymbol = symbol, KeyModifiers = modifiers };

            view.RaiseEvent(e);

            Assert.Equal(handled, e.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Minimising_the_window_goes_headless_and_restoring_it_goes_live()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            List<string> changes = [];
            view.LiveChanged += changes.Add;
            Assert.True(view.IsLive);
            Assert.Contains("\"live\":true", app.Flash.Stats(Timeout));
            int from = AppEngine.Calls().Length;

            window.WindowState = WindowState.Minimized;
            await WaitForCallsAsync(from, c => c.Contains("view headless"));
            Assert.False(view.IsLive);
            Assert.Contains("\"live\":false", app.Flash.Stats(Timeout));

            window.WindowState = WindowState.Normal;
            await WaitForCallsAsync(from, c => c.Contains("view live"));
            Assert.True(view.IsLive);
            Assert.Contains("\"live\":true", app.Flash.Stats(Timeout));
            // The debug log says why, so a Game View that stops can be told apart from a covered window.
            Assert.Equal(["headless: the window is minimised", "live"], changes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Closing_the_window_goes_headless()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        int from = AppEngine.Calls().Length;

        window.Close();

        await WaitForCallsAsync(from, c => c.Contains("view headless"));
        Assert.False(view.IsLive);
    }

    [AvaloniaFact]
    public async Task The_pointer_leaving_the_view_sends_mouse_leave()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            int from = AppEngine.Calls().Length;

            window.MouseMove(new Point(100, 100));
            window.MouseMove(new Point(-5, -5));

            string[] calls = await WaitForCallsAsync(from, c => c.Contains("input mouseLeave"));
            Assert.Contains("input mouseMove 100 100", calls);
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<(Window, GameView)> ShowAsync(int width, int height)
    {
        GameView view = new(app.Flash);
        Window window = new() { Width = width, Height = height, Content = view };
        window.Show();
        await PumpUntilAsync(() => view.IsLive && view.Bounds.Width > 0, "the view to be live");
        // The fake logs input as it reads it, and answers stats in the same order: once they are answered, whatever an earlier test sent,
        // such as the focus lost as its window closed, is in the call log, before a test counts where its own calls start.
        app.Flash.Stats(Timeout);
        return (window, view);
    }

    private static async Task PumpUntilAsync(Func<bool> done, string what)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!done())
        {
            if (waited.Elapsed > Timeout)
                throw new TimeoutException($"Waited {Timeout.TotalSeconds} s for {what}.");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>Waits until the calls logged after the first <paramref name="from"/> satisfy <paramref name="done"/>, and returns them.</summary>
    private static async Task<string[]> WaitForCallsAsync(int from, Func<string[], bool> done)
    {
        string[] calls = [];
        await PumpUntilAsync(() => done(calls = AppEngine.Calls()[from..]), "the fake Game Host's call log");
        return calls;
    }

    private static unsafe (byte, byte, byte) Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using ILockedFramebuffer locked = bitmap.Lock();
        byte* p = (byte*)locked.Address + (y * locked.RowBytes) + (x * 4);
        // Skia's headless frames are BGRA.
        return locked.Format == PixelFormats.Rgba8888 ? (p[0], p[1], p[2]) : (p[2], p[1], p[0]);
    }
}
