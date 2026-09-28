using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Skua.Avalonia;

/// <summary>Whether a window is fully covered, from AppKit's occlusion state, which Avalonia doesn't expose.</summary>
internal static partial class WindowOcclusion
{
    /// <summary><c>NSWindowOcclusionStateVisible</c>: some of the window is on screen.</summary>
    private const nuint Visible = 1 << 1;

    private static readonly IntPtr OcclusionState = sel_registerName("occlusionState");

    /// <summary>True when AppKit reports no part of the window visible; false off macOS or without a native window (e.g. headless).</summary>
    public static bool IsOccluded(Window window) => State(window) is { } state && (state & Visible) == 0;

    /// <summary>The window's raw <c>NSWindowOcclusionState</c>, or null off macOS or without a native window.</summary>
    public static nuint? State(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle { NSWindow: var nsWindow } || nsWindow == IntPtr.Zero)
            return null;
        return objc_msgSend_nuint(nsWindow, OcclusionState);
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nuint objc_msgSend_nuint(IntPtr receiver, IntPtr selector);
}
