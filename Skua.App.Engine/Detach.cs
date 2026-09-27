using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Skua.App.Engine;

/// <summary>
/// Cuts an auto-started Engine loose from the Control Surface that started it, so it outlives the CLI or MCP session.
/// </summary>
internal static partial class Detach
{
    /// <summary>Starts a new session, so a Ctrl+C in the starting terminal never reaches the Engine.</summary>
    public static void FromTerminal() => setsid();

    /// <summary>
    /// Points stdin at /dev/null and stdout/stderr at the log file. Call it before anything touches <see cref="Console"/>.
    /// </summary>
    /// <remarks>
    /// This doesn't drop the starter's stdio: the runtime has already copied fds 0-2, so auto-start points them at /dev/null before exec.
    /// </remarks>
    public static void RedirectStdio(string logPath)
    {
        using SafeFileHandle devNull = File.OpenHandle("/dev/null", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using FileStream log = new(logPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        dup2(devNull, 0);
        dup2(log.SafeFileHandle, 1);
        dup2(log.SafeFileHandle, 2);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int dup2(SafeHandle fd, int target);
}
