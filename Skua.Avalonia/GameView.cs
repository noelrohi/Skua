using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Skua.MacOS.GameHost;

namespace Skua.Avalonia;

/// <summary>
/// The Game View (ADR 0006): the live picture of the Game Client, scaled to fit with letterboxing, read from the Game Host's Frame Buffer
/// on each display refresh. Clicks, keys, text and the wheel in it go to the game; the game's cursor shows over it, and ⌘C, ⌘X and ⌘V in
/// its text fields use the Mac's clipboard.
/// </summary>
/// <remarks>
/// It is live (the Game Host renders every 33 ms at the view's size in device pixels, and writes each frame) while its window is on
/// screen, and not while the window is minimised, hidden or fully covered, or once the view leaves the window.
/// </remarks>
public sealed class GameView : global::Avalonia.Controls.Control
{
    private static readonly PixelSize Stage = new(GameHostLaunch.StageWidth, GameHostLaunch.StageHeight);

    private readonly BridgeFlashUtil _flash;
    /// <summary>The frame on screen; the next one is read into <see cref="_back"/>, so a frame dropped mid-copy is never shown.</summary>
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _back;
    /// <summary>The Frame Buffer read last: a restarted Game Host has a new one, whose frame numbers start again.</summary>
    private FrameBuffer? _source;
    private PixelSize _frameSize = Stage;
    private long _lastNumber;
    private bool _live;
    /// <summary>The viewport the Game Host renders at while live.</summary>
    private GameViewport? _viewport;
    private IDisposable? _occlusion;
    /// <summary>Input that waits for a Paste's clipboard text, so it reaches the game after the paste; null when no Paste waits.</summary>
    private List<GameInput>? _held;
    private bool _framePending;
    private Window? _window;
    private bool _pointerInside;
    private bool _gameFocused;
    /// <summary>The write stamp of a frame copied but not yet drawn; its age is measured when it is.</summary>
    private long? _undrawnStamp;

    public GameView(BridgeFlashUtil flash)
    {
        _flash = flash;
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Frames shown and their ages; the host logs them.</summary>
    public GameViewStats Stats { get; } = new();

    /// <summary>The number of the frame on screen, or 0 before the first.</summary>
    public long FrameNumber => _lastNumber;

    /// <summary>The size of the frame on screen, in Game Host viewport pixels; the stage size before the first.</summary>
    public PixelSize FrameSize => _frameSize;

    /// <summary>Whether the view is live, as it last told the Game Host.</summary>
    public bool IsLive => _live;

    /// <summary>The viewport the Game Host renders at, in device pixels: the view's while live, else the stage's.</summary>
    public PixelSize Viewport => _live && _viewport is { } v ? new PixelSize(v.Width, v.Height) : Stage;

    /// <summary>The cursor the game shows over the view, as it last reported it.</summary>
    public StandardCursorType CursorType { get; private set; } = StandardCursorType.Arrow;

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>
    /// Where the frame is drawn in this control: scaled to fit, keeping its aspect ratio, centred. A frame rendered at the view's size is
    /// drawn one frame pixel to one device pixel, on whole device pixels, so it stays sharp.
    /// </summary>
    public Rect ImageRect => ImageRectFor(Bounds.Size, FrameSize, Scaling);

    /// <inheritdoc cref="ImageRect"/>
    public static Rect ImageRectFor(Size bounds, PixelSize frame, double scaling)
    {
        Rect fit = Letterbox(bounds, frame);
        if (fit.Width <= 0 || scaling <= 0)
            return fit;
        Size exact = new(frame.Width / scaling, frame.Height / scaling);
        // Within a device pixel of fitting, the frame was rendered for this size, give or take the rounding; any other is resampled anyway.
        if (Math.Abs(exact.Width - fit.Width) * scaling > 1 || Math.Abs(exact.Height - fit.Height) * scaling > 1)
            return fit;
        return new Rect(
            Math.Round((bounds.Width - exact.Width) / 2 * scaling) / scaling,
            Math.Round((bounds.Height - exact.Height) / 2 * scaling) / scaling,
            exact.Width,
            exact.Height);
    }

    /// <summary>
    /// The viewport a live view has the Game Host render at: the stage's letterboxed rectangle in device pixels, keeping the stage's
    /// aspect ratio, never smaller than the stage (screenshots are scaled down from it, never up) nor larger than the Frame Buffer holds.
    /// </summary>
    public static GameViewport ViewportFor(Size bounds, double scaling)
    {
        double scale = Math.Clamp(Letterbox(bounds, Stage).Width / Stage.Width * scaling, 1, GameHostLaunch.MaxViewScale);
        if (double.IsNaN(scale))
            scale = 1;
        return new GameViewport((int)Math.Round(Stage.Width * scale), (int)Math.Round(Stage.Height * scale), scaling);
    }

    /// <summary>Maps a point in this control to Game Host viewport pixels.</summary>
    public Point ToViewport(Point point)
    {
        Rect image = ImageRect;
        if (image.Width <= 0 || image.Height <= 0)
            return default;
        PixelSize viewport = Viewport;
        return new Point((point.X - image.X) * viewport.Width / image.Width, (point.Y - image.Y) * viewport.Height / image.Height);
    }

    /// <summary>The largest rectangle of <paramref name="frame"/>'s aspect ratio that fits <paramref name="bounds"/>, centred.</summary>
    public static Rect Letterbox(Size bounds, PixelSize frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            return default;
        double scale = Math.Min(bounds.Width / frame.Width, bounds.Height / frame.Height);
        Size size = new(frame.Width * scale, frame.Height * scale);
        return new Rect(new Point((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2), size);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is not null && _lastNumber > 0)
            context.DrawImage(_bitmap, new Rect(0, 0, _frameSize.Width, _frameSize.Height), ImageRect);
        if (_undrawnStamp is { } stamp)
        {
            Stats.Shown(FrameBuffer.Now() - stamp);
            _undrawnStamp = null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.Activated += OnWindowActivated;
            _window.Deactivated += OnWindowDeactivated;
            _window.ScalingChanged += OnScalingChanged;
            _occlusion = WindowOcclusion.Observe(_window, UpdateLive);
        }
        _flash.CursorChanged += OnCursorChanged;
        _flash.ClipboardCopied += OnClipboardCopied;
        ShowCursor(_flash.Cursor);
        UpdateLive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _occlusion?.Dispose();
        _occlusion = null;
        _flash.CursorChanged -= OnCursorChanged;
        _flash.ClipboardCopied -= OnClipboardCopied;
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
            _window.ScalingChanged -= OnScalingChanged;
            _window = null;
        }
        UpdateGameFocus();
        SetLive(false);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
            UpdateLive();
    }

    /// <summary>Raised when the view goes live or stops being live, with why, e.g. <c>headless: occluded (occlusionState 0x2000)</c>.</summary>
    public event Action<string>? LiveChanged;

    /// <summary>Decides again whether the view is live: in a window that is visible, not minimised and not fully covered.</summary>
    public void UpdateLive()
    {
        if (_window is null)
        {
            SetLive(false, "headless: not in a window");
            return;
        }
        nuint? state = WindowOcclusion.State(_window);
        bool occluded = WindowOcclusion.IsOccluded(_window);
        string occlusion = state is { } s ? $" (occlusionState 0x{s:x})" : "";
        if (!_window.IsVisible)
            SetLive(false, "headless: the window is hidden" + occlusion);
        else if (_window.WindowState == WindowState.Minimized)
            SetLive(false, "headless: the window is minimised" + occlusion);
        else if (occluded)
            SetLive(false, "headless: the window is fully covered" + occlusion);
        else
            SetLive(true, "live" + occlusion);
    }

    private void SetLive(bool live, string reason = "headless: left the window")
    {
        if (live == _live)
            return;
        _live = live;
        _viewport = live ? ViewportFor(Bounds.Size, Scaling) : null;
        LiveChanged?.Invoke(reason);
        _flash.SetLive(live, _viewport);
        if (live)
            RequestFrame();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateViewport();
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateViewport();

    /// <summary>Has a live Game Host render at the view's new size, e.g. after a resize or a move to a display of another scale.</summary>
    private void UpdateViewport()
    {
        GameViewport viewport = ViewportFor(Bounds.Size, Scaling);
        if (!_live || viewport == _viewport)
            return;
        _viewport = viewport;
        _flash.SetLive(true, viewport);
    }

    private void RequestFrame()
    {
        if (_framePending || !_live || TopLevel.GetTopLevel(this) is not { } topLevel)
            return;
        _framePending = true;
        topLevel.RequestAnimationFrame(_ =>
        {
            _framePending = false;
            ShowLatestFrame();
            RequestFrame();
        });
    }

    /// <summary>Copies the Frame Buffer's latest frame into the bitmap if it is newer than the one on screen.</summary>
    public unsafe bool ShowLatestFrame()
    {
        if (_flash.FrameBuffer is not { } frameBuffer)
            return false;
        if (!ReferenceEquals(frameBuffer, _source))
        {
            _source = frameBuffer;
            _lastNumber = 0;
        }
        // Frames are as large as the view in device pixels; the next one read is most likely the latest one's size.
        if (frameBuffer.LatestSize() is not { } latest)
            return false;
        PixelSize size = new(latest.Width, latest.Height);
        if (_back is null || _back.PixelSize != size)
        {
            _back?.Dispose();
            _back = new WriteableBitmap(size, new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
        }

        FrameInfo? frame;
        using (ILockedFramebuffer locked = _back.Lock())
            frame = frameBuffer.TryRead(_lastNumber, (byte*)locked.Address, locked.RowBytes, locked.Size.Height);
        if (frame is not { } shown)
            return false;

        (_bitmap, _back) = (_back, _bitmap);
        _lastNumber = shown.Number;
        _frameSize = new PixelSize(shown.Width, shown.Height);
        _undrawnStamp = shown.Stamp;
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointerInside = true;
        Point p = ToViewport(e.GetPosition(this));
        Send(new GameInput.MouseMove((float)p.X, (float)p.Y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(NavigationMethod.Pointer);
        PointerPoint point = e.GetCurrentPoint(this);
        Point p = ToViewport(point.Position);
        Send(new GameInput.MouseDown((float)p.X, (float)p.Y, Button(point.Properties.PointerUpdateKind)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        PointerPoint point = e.GetCurrentPoint(this);
        Point p = ToViewport(point.Position);
        Send(new GameInput.MouseUp((float)p.X, (float)p.Y, Button(point.Properties.PointerUpdateKind)));
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_pointerInside)
            return;
        _pointerInside = false;
        Send(new GameInput.MouseLeave());
    }

    /// <summary>
    /// Sends the wheel as Ruffle's desktop player does: lines from a mouse wheel, device pixels from a trackpad. Avalonia's delta doesn't
    /// say which, so it comes from the event AppKit is dispatching; without one (e.g. headless) Avalonia's delta goes as lines.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        GameInput.Wheel wheel = MacScroll.Current(Scaling) ?? new GameInput.Wheel((float)e.Delta.Y, Pixels: false);
        if (wheel.Delta != 0)
            Send(wheel);
        e.Handled = true;
    }

    /// <summary>Sends the key, then the text-editing command it stands for; typed text arrives separately, as text input.</summary>
    /// <remarks>
    /// A key that types text is left unhandled: on macOS, Avalonia raises no text input for a key down that was handled. Every other
    /// key is handled, so Tab and the arrows move focus in the game, not between Avalonia controls.
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Send(new GameInput.KeyDown(GameKeys.Map(e.PhysicalKey, e.Key, e.KeySymbol)));
        if (GameKeys.TextControl(e.Key, e.KeyModifiers) is "Paste")
            Paste();
        else if (GameKeys.TextControl(e.Key, e.KeyModifiers) is { } code)
            Send(new GameInput.TextControl(code));
        e.Handled = !GameKeys.TypesText(e.Key, e.KeyModifiers, e.KeySymbol);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        Send(new GameInput.KeyUp(GameKeys.Map(e.PhysicalKey, e.Key, e.KeySymbol)));
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Text is { } text)
        {
            for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            {
                int codePoint = char.ConvertToUtf32(text, i);
                if (GameKeys.IsTyped(codePoint))
                    Send(new GameInput.Text(codePoint));
            }
        }
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateGameFocus();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        UpdateGameFocus();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        UpdateGameFocus();
        UpdateLive();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => UpdateGameFocus();

    /// <summary>The game has focus while this view has it in the active window, as a movie in Flash has it while its window does.</summary>
    private void UpdateGameFocus()
    {
        bool focused = IsFocused && _window is { IsActive: true };
        if (focused == _gameFocused)
            return;
        _gameFocused = focused;
        Send(focused ? new GameInput.FocusGained() : new GameInput.FocusLost());
    }

    private void Send(GameInput input)
    {
        if (_held is not null)
            _held.Add(input);
        else
            _flash.SendInput(input);
    }

    /// <summary>
    /// Sends the Mac's clipboard text, then the Paste that pastes it: Ruffle reads the clipboard during the Paste, and the Game Host can't
    /// ask for it then. Input that comes while the clipboard is read waits, so it still reaches the game after the paste.
    /// </summary>
    private async void Paste()
    {
        if (_held is not null)
        {
            // The text read for the Paste before this one is on the Game Host's clipboard by then.
            _held.Add(new GameInput.TextControl("Paste"));
            return;
        }
        _held = [];
        string text = "";
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                text = await clipboard.TryGetTextAsync() ?? "";
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Couldn't read the clipboard to paste into the game: {e.Message}");
        }
        List<GameInput> held = _held;
        _held = null;
        _flash.SendInput(new GameInput.Clipboard(text));
        _flash.SendInput(new GameInput.TextControl("Paste"));
        foreach (GameInput input in held)
            _flash.SendInput(input);
    }

    /// <summary>Puts text the game copied or cut on the Mac's clipboard.</summary>
    private void OnClipboardCopied(string text) => Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Couldn't copy the game's text to the clipboard: {e.Message}");
        }
    });

    // The latest cursor, when the UI thread gets to it.
    private void OnCursorChanged(GameCursorState _) => Dispatcher.UIThread.Post(() => ShowCursor(_flash.Cursor));

    private void ShowCursor(GameCursorState cursor)
    {
        StandardCursorType type = CursorTypeFor(cursor);
        if (type == CursorType && Cursor is not null)
            return;
        CursorType = type;
        Cursor = Cursors.TryGetValue(type, out Cursor? shown) ? shown : Cursors[type] = new Cursor(type);
    }

    /// <summary>The Mac cursor for the game's, as Ruffle's desktop player picks it; none after AS3's <c>Mouse.hide()</c>.</summary>
    public static StandardCursorType CursorTypeFor(GameCursorState cursor) => !cursor.Visible ? StandardCursorType.None : cursor.Cursor switch
    {
        GameCursor.Hand => StandardCursorType.Hand,
        GameCursor.IBeam => StandardCursorType.Ibeam,
        GameCursor.Grab => StandardCursorType.DragMove,
        _ => StandardCursorType.Arrow,
    };

    /// <summary>One of each, for every Game View: each <see cref="Cursor"/> holds a native cursor.</summary>
    private static readonly Dictionary<StandardCursorType, Cursor> Cursors = [];

    private static GameMouseButton Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => GameMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => GameMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => GameMouseButton.Middle,
        _ => GameMouseButton.Unknown,
    };
}
