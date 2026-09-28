using System.Runtime.InteropServices;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Services;

/// <summary>Core's beeps as the macOS alert sound, which has no frequency or length of its own.</summary>
public sealed partial class MacSoundService : ISoundService
{
    public void Beep() => NSBeep();

    public void Beep(int frequency, int duration) => NSBeep();

    [LibraryImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
    private static partial void NSBeep();
}
