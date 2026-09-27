using Skua.Control;

namespace Skua.App.Cli;

/// <summary>
/// The CLI's exit codes: 0 on success, 1 for an unexpected failure or bad usage, and 10 + the <see cref="ErrorCode"/> number for each error code.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>An unexpected failure or bad usage; also a run that <c>skua script start --follow</c> followed and that failed.</summary>
    public const int Failure = 1;

    private const int ErrorCodeBase = 10;

    public static int For(ErrorCode code) => ErrorCodeBase + (int)code;
}
