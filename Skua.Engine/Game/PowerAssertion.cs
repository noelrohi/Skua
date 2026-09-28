using System.Runtime.InteropServices;

namespace Skua.Engine.Game;

/// <summary>
/// An IOKit assertion that keeps the Mac from idle-sleeping, as <c>caffeinate -i</c> does; <c>pmset -g assertions</c> lists it by name.
/// </summary>
internal sealed partial class PowerAssertion : IDisposable
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint AssertionLevelOn = 255;
    private const uint Utf8 = 0x08000100;

    private readonly object _lock = new();
    private readonly string _name;
    private uint? _id;
    private bool _disposed;

    public PowerAssertion(string name)
    {
        _name = name;
    }

    /// <summary>Takes or releases the assertion; taking it again while held, or at all once disposed, does nothing.</summary>
    public void Hold(bool hold)
    {
        lock (_lock)
        {
            if (hold && _id is null && !_disposed)
                _id = Create(_name);
            else if (!hold && _id is { } id)
            {
                IOPMAssertionRelease(id);
                _id = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Hold(false);
            _disposed = true;
        }
    }

    private static uint? Create(string name)
    {
        IntPtr type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", Utf8);
        IntPtr reason = CFStringCreateWithCString(IntPtr.Zero, name, Utf8);
        try
        {
            int result = IOPMAssertionCreateWithName(type, AssertionLevelOn, reason, out uint id);
            if (result == 0)
                return id;
            EngineLog.Write($"Couldn't hold off idle sleep: IOPMAssertionCreateWithName returned 0x{result:x8}.");
            return null;
        }
        finally
        {
            CFRelease(type);
            CFRelease(reason);
        }
    }

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionRelease(uint id);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);
}
