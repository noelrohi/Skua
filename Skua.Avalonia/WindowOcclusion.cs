using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Skua.Avalonia;

/// <summary>Whether a window is fully covered, from AppKit's occlusion state, which Avalonia doesn't expose, and when that changes.</summary>
internal static unsafe partial class WindowOcclusion
{
    /// <summary><c>NSWindowOcclusionStateVisible</c>: some of the window is on screen.</summary>
    private const nuint Visible = 1 << 1;

    private const string NotificationName = "NSWindowDidChangeOcclusionStateNotification";
    private const string ObserverClassName = "SkuaWindowOcclusionObserver";

    private static readonly IntPtr OcclusionState = sel_registerName("occlusionState");
    private static readonly IntPtr Changed = sel_registerName("skuaOcclusionChanged:");

    /// <summary>The handler of each observer object, by its address.</summary>
    private static readonly Dictionary<IntPtr, Action> s_observers = [];

    /// <summary>True when AppKit reports no part of the window visible; false off macOS or without a native window (e.g. headless).</summary>
    public static bool IsOccluded(Window window) => State(window) is { } state && (state & Visible) == 0;

    /// <summary>The window's raw <c>NSWindowOcclusionState</c>, or null off macOS or without a native window.</summary>
    public static nuint? State(Window window) => NativeWindow(window) is { } nsWindow ? objc_msgSend_nuint(nsWindow, OcclusionState) : null;

    /// <summary>
    /// Calls <paramref name="changed"/> on the UI thread each time AppKit posts the window's <c>NSWindowDidChangeOcclusionStateNotification</c>:
    /// when it becomes fully covered or visible again, including a move to another Space or the display sleeping. Null off macOS or
    /// without a native window. Dispose it to stop.
    /// </summary>
    public static IDisposable? Observe(Window window, Action changed)
    {
        if (NativeWindow(window) is not { } nsWindow)
            return null;
        IntPtr observer = objc_msgSend(objc_msgSend(ObserverClass(), sel_registerName("alloc")), sel_registerName("init"));
        lock (s_observers)
            s_observers[observer] = changed;
        IntPtr center = objc_msgSend(objc_getClass("NSNotificationCenter"), sel_registerName("defaultCenter"));
        IntPtr name = Name();
        objc_msgSend_observe(center, sel_registerName("addObserver:selector:name:object:"), observer, Changed, name, nsWindow);
        return new Observation(() =>
        {
            objc_msgSend_remove(center, sel_registerName("removeObserver:name:object:"), observer, name, nsWindow);
            lock (s_observers)
                s_observers.Remove(observer);
            objc_msgSend(observer, sel_registerName("release"));
        });
    }

    private static IntPtr? NativeWindow(Window window) =>
        OperatingSystem.IsMacOS() && window.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle { NSWindow: var nsWindow } && nsWindow != IntPtr.Zero
            ? nsWindow
            : null;

    /// <summary>AppKit's own constant, or an equal string: notification names match by value.</summary>
    private static IntPtr Name()
    {
        IntPtr constant = dlsym(-2, NotificationName);
        return constant != IntPtr.Zero && Marshal.ReadIntPtr(constant) is var name && name != IntPtr.Zero
            ? name
            : objc_msgSend_string(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), NotificationName);
    }

    /// <summary>An NSObject subclass whose one method hands the notification to the observer's handler; made once per process.</summary>
    private static IntPtr ObserverClass()
    {
        lock (s_observers)
        {
            IntPtr cls = objc_getClass(ObserverClassName);
            if (cls != IntPtr.Zero)
                return cls;
            cls = objc_allocateClassPair(objc_getClass("NSObject"), ObserverClassName, 0);
            class_addMethod(cls, Changed, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnChanged, "v@:@");
            objc_registerClassPair(cls);
            return cls;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnChanged(IntPtr self, IntPtr selector, IntPtr notification)
    {
        Action? changed;
        lock (s_observers)
            changed = s_observers.GetValueOrDefault(self);
        if (changed is not null)
            Dispatcher.UIThread.Post(changed);
    }

    private sealed class Observation(Action stop) : IDisposable
    {
        private Action? _stop = stop;

        public void Dispose()
        {
            _stop?.Invoke();
            _stop = null;
        }
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dlsym(IntPtr handle, string symbol);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport("/usr/lib/libobjc.A.dylib")]
    private static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_msgSend_string(IntPtr receiver, IntPtr selector, string value);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_observe(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c, IntPtr d);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_remove(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial nuint objc_msgSend_nuint(IntPtr receiver, IntPtr selector);
}
