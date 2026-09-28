using System.Runtime.InteropServices;
using Skua.MacOS.GameHost;

namespace Skua.Avalonia;

/// <summary>
/// The scroll wheel event AppKit is dispatching, which tells what Avalonia's wheel delta doesn't: whether the device scrolls by lines (a
/// mouse wheel) or by pixels (a trackpad or Magic Mouse). Avalonia divides both by a speed and passes no flag.
/// </summary>
public static partial class MacScroll
{
    private const nuint NSEventTypeScrollWheel = 22;
    private static readonly IntPtr RtldDefault = -2;

    private static readonly IntPtr CurrentEvent = sel_registerName("currentEvent");
    private static readonly IntPtr Type = sel_registerName("type");
    private static readonly IntPtr HasPreciseScrollingDeltas = sel_registerName("hasPreciseScrollingDeltas");
    private static readonly IntPtr ScrollingDeltaY = sel_registerName("scrollingDeltaY");

    /// <summary>The wheel event for the scroll AppKit is dispatching, or null when it isn't dispatching one (off macOS, or headless).</summary>
    public static GameInput.Wheel? Current(double scaling)
    {
        // NSApp, read rather than [NSApplication sharedApplication], which would make one in a process without; absent without AppKit.
        IntPtr nsAppVariable = OperatingSystem.IsMacOS() ? dlsym(RtldDefault, "NSApp") : IntPtr.Zero;
        if (nsAppVariable == IntPtr.Zero)
            return null;
        IntPtr app = Marshal.ReadIntPtr(nsAppVariable);
        IntPtr e = app == IntPtr.Zero ? IntPtr.Zero : objc_msgSend(app, CurrentEvent);
        if (e == IntPtr.Zero || objc_msgSend_nuint(e, Type) != NSEventTypeScrollWheel)
            return null;
        return Wheel(objc_msgSend_bool(e, HasPreciseScrollingDeltas) != 0, objc_msgSend_double(e, ScrollingDeltaY), scaling);
    }

    /// <summary>
    /// As winit gives it to Ruffle's desktop player: a precise device's delta in device pixels, else the delta in lines; positive scrolls up.
    /// </summary>
    public static GameInput.Wheel Wheel(bool precise, double scrollingDeltaY, double scaling) =>
        precise ? new GameInput.Wheel((float)(scrollingDeltaY * scaling), Pixels: true) : new GameInput.Wheel((float)scrollingDeltaY, Pixels: false);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dlsym(IntPtr handle, string symbol);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nuint objc_msgSend_nuint(IntPtr receiver, IntPtr selector);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial byte objc_msgSend_bool(IntPtr receiver, IntPtr selector);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial double objc_msgSend_double(IntPtr receiver, IntPtr selector);
}
