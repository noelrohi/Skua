using Skua.Control;
using StreamJsonRpc;

namespace Skua.App.Engine;

/// <summary>Builds the JSON-RPC errors that clients turn back into a <see cref="ControlException"/> with the same code.</summary>
internal static class RpcErrors
{
    public static LocalRpcException Of(ErrorCode code, string message) => new(message)
    {
        ErrorCode = ErrorCodes.ToWire(code),
        ErrorData = new ErrorDataDto(code),
    };
}
