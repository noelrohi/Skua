using System.Runtime.InteropServices;

namespace Skua.Avalonia.Manager;

/// <summary>AppKit's <c>NSRunningApplication</c>, for bringing another app process to the front.</summary>
internal static partial class MacApps
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>NSApplicationActivateAllWindows | NSApplicationActivateIgnoringOtherApps; the latter is a no-op since macOS 14.</summary>
    private const nuint ActivationOptions = 1 | 2;

    /// <summary>Activates the app process <paramref name="pid"/>; false when it isn't an app, or macOS declined.</summary>
    public static bool Activate(int pid)
    {
        IntPtr type = objc_getClass("NSRunningApplication");
        if (type == IntPtr.Zero)
            return false;
        IntPtr app = SendInt(type, sel_registerName("runningApplicationWithProcessIdentifier:"), pid);
        return app != IntPtr.Zero && SendUInt(app, sel_registerName("activateWithOptions:"), ActivationOptions) != 0;
    }

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendInt(IntPtr receiver, IntPtr selector, int argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendUInt(IntPtr receiver, IntPtr selector, nuint argument);
}
