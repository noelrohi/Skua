namespace Skua.Control;

/// <summary>
/// A Control Surface failure with a stable <see cref="ErrorCode"/>, raised by the Engine or by the client helper.
/// </summary>
public sealed class ControlException : Exception
{
    public ControlException(ErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}
