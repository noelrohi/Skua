namespace Skua.Control;

/// <summary>
/// The stable code carried by every Control Surface failure. The numbers are part of the contract; never reuse one.
/// </summary>
public enum ErrorCode
{
    NotLoggedIn = 1,
    ScriptRunning = 2,
    ScriptNotFound = 3,
    CompileFailed = 4,
    Timeout = 5,
    GameHostDown = 6,
    Busy = 7,
    DialogNotPending = 8,
    InvalidArgument = 9,
    LoginFailed = 10,

    /// <summary>The running Engine speaks another protocol version and wasn't replaced; the user must run <c>skua engine stop</c>.</summary>
    ProtocolMismatch = 11,

    /// <summary>No Engine could be reached: it failed to start, or it is starting or hung.</summary>
    EngineUnavailable = 12,

    /// <summary>The Script Source couldn't be reached or returned something unusable.</summary>
    ScriptSourceUnavailable = 13,

    /// <summary>The game's servers API couldn't be reached or returned no servers.</summary>
    ServersUnavailable = 14,

    /// <summary>No account is in Keychain under the name or service asked for.</summary>
    AccountNotFound = 15,

    /// <summary>macOS's <c>security</c> tool failed to read or change Keychain, e.g. because access was denied.</summary>
    KeychainFailed = 16,
}

/// <summary>
/// Maps <see cref="ErrorCode"/> to and from JSON-RPC error codes, which sit above the range JSON-RPC reserves.
/// </summary>
public static class ErrorCodes
{
    private const int WireBase = 1000;

    public static int ToWire(ErrorCode code) => WireBase + (int)code;

    public static ErrorCode? FromWire(int wire)
    {
        ErrorCode code = (ErrorCode)(wire - WireBase);
        return Enum.IsDefined(code) ? code : null;
    }
}
