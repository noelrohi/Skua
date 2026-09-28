using System.Runtime.InteropServices;

namespace Skua.Engine;

internal static partial class Umask
{
    /// <summary>Sets the process's file-creation mask and returns the previous one.</summary>
    public static int Set(int mask) => umask(mask);

    [LibraryImport("libc")]
    private static partial int umask(int mask);
}
