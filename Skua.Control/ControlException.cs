namespace Skua.Control;

/// <summary>
/// A Control Surface failure with a stable <see cref="ErrorCode"/>, raised by the Engine or by the client helper.
/// </summary>
public sealed class ControlException : Exception
{
    public ControlException(ErrorCode code, string message, Exception? innerException = null, IReadOnlyList<string>? diagnostics = null)
        : base(message, innerException)
    {
        Code = code;
        Diagnostics = diagnostics;
    }

    public ErrorCode Code { get; }

    /// <summary>The compiler's errors, one per line, for <see cref="ErrorCode.CompileFailed"/>; else null.</summary>
    public IReadOnlyList<string>? Diagnostics { get; }
}
