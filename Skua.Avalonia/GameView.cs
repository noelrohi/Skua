using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Skua.MacOS.GameHost;

namespace Skua.Avalonia;

/// <summary>
/// The Game View (ADR 0006): the live picture of the Game Client, scaled to fit with letterboxing, read from the Game Host's Frame Buffer
/// on each display refresh. Clicks, keys and text in it go to the game.
/// </summary>
/// <remarks>
/// It is live (the Game Host renders every 33 ms and writes each frame) while its window is on screen, and not while the window is
/// minimised, hidden or fully covered, or once the view leaves the window.
/// </remarks>
public sealed class GameView : Control
{
    private static readonly TimeSpan OcclusionPoll = TimeSpan.FromMilliseconds(500);

    private readonly BridgeFlashUtil _flash;
    private readonly DispatcherTimer _occlusionTimer;
    private WriteableBitmap? _bitmap;
    private PixelSize _frameSize = new(GameHostLaunch.StageWidth, GameHostLaunch.StageHeight);
    private long _lastNumber;
    private bool _live;
    private bool _framePending;
    private bool _occluded;
    private Window? _window;
    private bool _pointerInside;
    private bool _gameFocused;

    public GameView(BridgeFlashUtil flash)
    {
        _flash = flash;
        Focusable = true;
        ClipToBounds = true;
        _occlusionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = OcclusionPoll };
        _occlusionTimer.Tick += (_, _) => UpdateLive();
    }

    /// <summary>Frames shown and their ages; the host logs them.</summary>
    public GameViewStats Stats { get; } = new();

    /// <summary>The number of the frame on screen, or 0 before the first.</summary>
    public long FrameNumber => _lastNumber;

    /// <summary>The size of the frame on screen, in Game Host viewport pixels; the stage size before the first.</summary>
    public PixelSize FrameSize => _frameSize;

    /// <summary>Whether the view is live, as it last told the Game Host.</summary>
    public bool IsLive => _live;

    /// <summary>Where the frame is drawn in this control: scaled to fit, keeping its aspect ratio, centred.</summary>
    public Rect ImageRect => Letterbox(Bounds.Size, FrameSize);

    /// <summary>Maps a point in this control to Game Host viewport pixels.</summary>
    public Point ToViewport(Point point)
    {
        Rect image = ImageRect;
        if (image.Width <= 0 || image.Height <= 0)
            return default;
        double scale = image.Width / FrameSize.Width;
        return new Point((point.X - image.X) / scale, (point.Y - image.Y) / scale);
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
        }
        _occlusionTimer.Start();
        UpdateLive();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _occlusionTimer.Stop();
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.Activated -= OnWindowActivated;
            _window.Deactivated -= OnWindowDeactivated;
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

    /// <summary>Decides again whether the view is live: in a window that is visible, not minimised and not fully covered.</summary>
    public void UpdateLive()
    {
        if (_window is not null)
            _occluded = WindowOcclusion.IsOccluded(_window);
        SetLive(_window is { IsVisible: true, WindowState: not WindowState.Minimized } && !_occluded);
    }

    private void SetLive(bool live)
    {
        if (live == _live)
            return;
        _live = live;
        _flash.SetLive(live);
        if (live)
            RequestFrame();
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
        PixelSize size = new(frameBuffer.MaxWidth, frameBuffer.MaxHeight);
        if (_bitmap is null || _bitmap.PixelSize != size)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormats.Rgba8888, AlphaFormat.Opaque);
        }

        FrameInfo? frame;
        using (ILockedFramebuffer locked = _bitmap.Lock())
            frame = frameBuffer.TryRead(_lastNumber, (byte*)locked.Address, locked.RowBytes, locked.Size.Height);
        if (frame is not { } shown)
            return false;

        _lastNumber = shown.Number;
        _frameSize = new PixelSize(shown.Width, shown.Height);
        Stats.Shown(FrameBuffer.Now() - shown.Stamp);
        InvalidateVisual();
        return true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointerInside = true;
        Point p = ToViewport(e.GetPosition(this));
        _flash.SendInput(new GameInput.MouseMove((float)p.X, (float)p.Y));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus(NavigationMethod.Pointer);
        PointerPoint point = e.GetCurrentPoint(this);
        Point p = ToViewport(point.Position);
        _flash.SendInput(new GameInput.MouseDown((float)p.X, (float)p.Y, Button(point.Properties.PointerUpdateKind)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        PointerPoint point = e.GetCurrentPoint(this);
        Point p = ToViewport(point.Position);
        _flash.SendInput(new GameInput.MouseUp((float)p.X, (float)p.Y, Button(point.Properties.PointerUpdateKind)));
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_pointerInside)
            return;
        _pointerInside = false;
        _flash.SendInput(new GameInput.MouseLeave());
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y != 0)
            _flash.SendInput(new GameInput.Wheel((float)e.Delta.Y, Pixels: false));
        e.Handled = true;
    }

    /// <summary>Sends the key, then the text-editing command it stands for; typed text arrives separately, as text input.</summary>
    /// <remarks>Handled, so Tab and the arrows move focus in the game, not between Avalonia controls.</remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        _flash.SendInput(new GameInput.KeyDown(GameKeys.Map(e.PhysicalKey, e.Key, e.KeySymbol)));
        if (GameKeys.TextControl(e.Key, e.KeyModifiers) is { } code)
            _flash.SendInput(new GameInput.TextControl(code));
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _flash.SendInput(new GameInput.KeyUp(GameKeys.Map(e.PhysicalKey, e.Key, e.KeySymbol)));
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
                    _flash.SendInput(new GameInput.Text(codePoint));
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

    /// <summary>The game has focus while this view has it in the active window, as a Flash player's stage has it while its window does.</summary>
    private void UpdateGameFocus()
    {
        bool focused = IsFocused && _window is { IsActive: true };
        if (focused == _gameFocused)
            return;
        _gameFocused = focused;
        _flash.SendInput(focused ? new GameInput.FocusGained() : new GameInput.FocusLost());
    }

    private static GameMouseButton Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => GameMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => GameMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => GameMouseButton.Middle,
        _ => GameMouseButton.Unknown,
    };
}
