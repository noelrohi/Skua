using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Skua.MacOS.GameHost;

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
    [InlineData(958, 550, 1, 479, 275, "479 275")]
    // Letterboxed: twice the stage's width, with bars of 250 above and below; the Game Host renders at twice the stage's size.
    [InlineData(1916, 1600, 1, 958, 800, "958 550")]
    // Pillarboxed: half the stage's height, with bars of 239.5 on either side; the Game Host never renders below the stage's size.
    [InlineData(958, 275, 1, 479, 137.5, "479 275")]
    // Retina: the stage's size in points is twice it in device pixels.
    [InlineData(958, 550, 2, 479, 275, "958 550")]
    public async Task A_click_arrives_at_the_viewport_pixel_under_it(int width, int height, double scaling, double x, double y, string at)
    {
        (Window window, GameView view) = await ShowAsync(width, height, scaling);
        try
        {
            int from = AppEngine.Calls().Length;

            window.MouseDown(new Point(x, y), MouseButton.Left);
            window.MouseUp(new Point(x, y), MouseButton.Left);

            string[] calls = await WaitForCallsAsync(from, c => c.Contains($"input mouseUp {at} left"));
            Assert.Equal(["input focusGained", $"input mouseDown {at} left", $"input mouseUp {at} left"],
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

    [AvaloniaFact]
    public async Task On_a_Retina_display_the_Game_Host_renders_at_the_views_device_pixels_and_they_are_drawn_one_to_one()
    {
        (Window window, GameView view) = await ShowAsync(958, 550, 2);
        try
        {
            Assert.Equal(new PixelSize(1916, 1100), view.Viewport);
            Assert.Contains("\"viewportWidth\":1916,\"viewportHeight\":1100", app.Flash.Stats(Timeout));
            await PumpUntilAsync(() => view.FrameSize == new PixelSize(1916, 1100) && view.Stats.Total > 0, "a frame at twice the stage's size");

            // One frame pixel to one device pixel, on whole device pixels: sharp.
            Assert.Equal(new Rect(0, 0, 958, 550), view.ImageRect);
            long drawn = view.Stats.Total;
            await PumpUntilAsync(() => view.Stats.Total > drawn, "a newer frame to be drawn");
            using WriteableBitmap? frame = window.CaptureRenderedFrame() as WriteableBitmap;
            Assert.Equal(new PixelSize(1916, 1100), frame!.PixelSize);
            long shown = view.FrameNumber;
            Assert.Equal(((byte)shown, (byte)(shown >> 8), (byte)(shown >> 16)), Pixel(frame, 1915, 1099));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Resizing_the_window_has_the_Game_Host_render_at_the_new_size_up_to_the_Frame_Buffers()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            int from = AppEngine.Calls().Length;

            window.Width = 1437;
            window.Height = 825;
            await WaitForCallsAsync(from, c => c.Contains("view viewport 1437x825 1"));
            await PumpUntilAsync(() => view.FrameSize == new PixelSize(1437, 825), "a frame at the new size");

            window.Width = 4000;
            window.Height = 3000;
            // Three times the stage at most, the largest frame the Frame Buffer holds.
            await WaitForCallsAsync(from, c => c.Contains("view viewport 2874x1650 1"));
            await PumpUntilAsync(() => view.FrameSize == new PixelSize(2874, 1650), "a frame at the largest size");
            Assert.Equal(new PixelSize(2874, 1650), view.Viewport);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Going_headless_renders_at_the_stage_size_again_and_going_live_at_the_views()
    {
        (Window window, GameView view) = await ShowAsync(958, 550, 2);
        try
        {
            int from = AppEngine.Calls().Length;

            window.WindowState = WindowState.Minimized;
            await WaitForCallsAsync(from, c => c.Contains("view headless"));
            Assert.Contains("\"viewportWidth\":958,\"viewportHeight\":550", app.Flash.Stats(Timeout));

            window.WindowState = WindowState.Normal;
            string[] calls = await WaitForCallsAsync(from, c => c.Contains("view viewport 1916x1100 2"));
            // In order, though a view an earlier test left may add lines between them.
            int headless = Array.IndexOf(calls, "view headless");
            int live = Array.IndexOf(calls, "view live", headless + 1);
            Assert.True(headless >= 0 && live > headless && Array.IndexOf(calls, "view viewport 1916x1100 2", live + 1) == live + 1,
                string.Join(", ", calls.Where(c => c.StartsWith("view ", StringComparison.Ordinal))));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_cursor_changes_over_the_games_buttons()
    {
        // A button on the stage from (100, 100) to (300, 200).
        await AppEngine.DoAsync("button 100 100 200 100");
        (Window window, GameView view) = await ShowAsync(958, 550, 2);
        try
        {
            window.MouseMove(new Point(50, 50));
            await PumpUntilAsync(() => view.CursorType == StandardCursorType.Arrow, "the arrow off the button");

            // Stage (150, 150) is (300, 300) in the Game Host's Retina viewport; the fake maps it back, as Ruffle does.
            window.MouseMove(new Point(150, 150));
            await PumpUntilAsync(() => view.CursorType == StandardCursorType.Hand, "the hand over the button");
            Assert.NotNull(view.Cursor);

            window.MouseMove(new Point(400, 400));
            await PumpUntilAsync(() => view.CursorType == StandardCursorType.Arrow, "the arrow again off the button");
        }
        finally
        {
            window.MouseMove(new Point(-5, -5));
            window.Close();
            await AppEngine.DoAsync("button 0 0 0 0");
        }
    }

    [AvaloniaFact]
    public async Task Command_V_pastes_the_Macs_clipboard_into_the_game_after_the_keys_before_it()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            view.Focus();
            await window.Clipboard!.SetTextAsync("from the Mac ✓");
            int from = AppEngine.Calls().Length;

            await PressAsync(window, Key.V, RawInputModifiers.Meta, PhysicalKey.V, "v");
            await PressAsync(window, Key.A, RawInputModifiers.None, PhysicalKey.A, "a", "a");

            string[] calls = await WaitForCallsAsync(from, c => c.Contains("input keyUp KeyA a"));
            Assert.Equal(
                ["input keyDown KeyV v", "input clipboard from the Mac ✓", "input textControl Paste", "input keyUp KeyV v", "input keyDown KeyA a", "input text a", "input keyUp KeyA a"],
                calls.Where(c => !c.StartsWith("input mouse", StringComparison.Ordinal) && !c.StartsWith("input focus", StringComparison.Ordinal)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Text_the_game_copies_goes_on_the_Macs_clipboard()
    {
        await AppEngine.DoAsync("selection from the game ✓");
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            view.Focus();
            await window.Clipboard!.SetTextAsync("before");

            await PressAsync(window, Key.C, RawInputModifiers.Meta, PhysicalKey.C, "c");

            string text = "";
            await PumpUntilAsync(() =>
            {
                Task<string?> read = window.Clipboard!.TryGetTextAsync();
                text = read.IsCompleted ? read.Result ?? "" : text;
                return text == "from the game ✓";
            }, "the game's text on the clipboard");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void A_trackpad_scrolls_by_device_pixels_and_a_mouse_wheel_by_lines()
    {
        Assert.Equal(new GameInput.Wheel(-24, Pixels: true), MacScroll.Wheel(precise: true, scrollingDeltaY: -12, scaling: 2));
        Assert.Equal(new GameInput.Wheel(3, Pixels: false), MacScroll.Wheel(precise: false, scrollingDeltaY: 3, scaling: 2));
    }

    [AvaloniaFact]
    public async Task Without_AppKits_event_the_wheel_goes_as_lines()
    {
        (Window window, GameView view) = await ShowAsync(958, 550);
        try
        {
            int from = AppEngine.Calls().Length;

            window.MouseWheel(new Point(100, 100), new Vector(0, -2));

            await WaitForCallsAsync(from, c => c.Contains("input wheel -2 lines"));
        }
        finally
        {
            window.Close();
        }
    }

    private async Task<(Window, GameView)> ShowAsync(int width, int height, double scaling = 1)
    {
        GameView view = new(app.Flash);
        Window window = new() { Width = width, Height = height, Content = view };
        window.Show();
        if (scaling != 1)
            window.SetRenderScaling(scaling);
        GameViewport expected = GameView.ViewportFor(new Size(width, height), scaling);
        await PumpUntilAsync(() => view.IsLive && view.Bounds.Width > 0 && view.Viewport == new PixelSize(expected.Width, expected.Height), "the view to be live");
        // The fake logs input as it reads it, and answers stats in the same order: once they are answered, whatever an earlier test sent,
        // such as the focus lost as its window closed, is in the call log, before a test counts where its own calls start.
        app.Flash.Stats(Timeout);
        return (window, view);
    }

    /// <summary>Presses and releases a key, with its typed text if any, releasing it however the press went.</summary>
    private static async Task PressAsync(Window window, Key key, RawInputModifiers modifiers, PhysicalKey physical, string symbol, string? text = null)
    {
        try
        {
            window.KeyPress(key, modifiers, physical, symbol);
            if (text is not null)
                window.KeyTextInput(text);
        }
        finally
        {
            window.KeyRelease(key, modifiers, physical, symbol);
        }
        await PumpUntilAsync(() => true, "the key's handlers");
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
