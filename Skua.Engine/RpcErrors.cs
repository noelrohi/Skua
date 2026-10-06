using Skua.Control;
using StreamJsonRpc;

namespace Skua.Engine;

/// <summary>Builds the JSON-RPC errors that clients turn back into a <see cref="ControlException"/> with the same code.</summary>
internal static class RpcErrors
{
    public static LocalRpcException Of(ErrorCode code, string message, IReadOnlyList<string>? diagnostics = null) => new(message)
    {
        ErrorCode = ErrorCodes.ToWire(code),
        ErrorData = new ErrorDataDto(code, diagnostics),
    };

    /// <summary>The failure <see cref="Of"/> built, as a client sees it, for a caller that runs an operation in its own process.</summary>
    public static ControlException ToControlException(LocalRpcException e)
    {
        ErrorDataDto data = (ErrorDataDto)e.ErrorData!;
        return new ControlException(data.Code, e.Message, e, data.Diagnostics);
    }
}
