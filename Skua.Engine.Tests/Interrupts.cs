using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Skua.Engine.Tests;

/// <summary>
/// Lets the Engines and CLIs these tests start take SIGINT, as they do when run from a terminal. A shell starts a background job with SIGINT
/// ignored, and a process started with it ignored keeps ignoring it, .NET's included: without this, a test run started in the background finds
/// Ctrl-C and <c>kill -INT</c> doing nothing, and a test waiting for either to stop a process waits forever.
/// </summary>
internal static unsafe partial class Interrupts
{
    private const int SIGINT = 2;
    private const nint SIG_IGN = 1;

    /// <summary>Children inherit an ignored signal, but get the default for one this process handles; so only an ignored SIGINT changes.</summary>
    [ModuleInitializer]
    internal static void RestoreDefault()
    {
        SigAction current;
        if (sigaction(SIGINT, null, &current) == 0 && current.Handler == SIG_IGN)
        {
            SigAction byDefault = default;
            sigaction(SIGINT, &byDefault, null);
        }
    }

    /// <summary>
    /// A <c>struct sigaction</c>, whose handler comes first: macOS's is 16 bytes, glibc's on 64-bit Linux 152 (a 128-byte <c>sigset_t</c>), so
    /// it is sized for the larger and zeroed past the handler.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 152)]
    private struct SigAction
    {
        public nint Handler;
    }

    [LibraryImport("libc")]
    private static partial int sigaction(int signal, SigAction* action, SigAction* old);
}
