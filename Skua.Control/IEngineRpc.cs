using StreamJsonRpc;

namespace Skua.Control;

/// <summary>
/// The Control Surface contract: every method the Engine serves over JSON-RPC.
/// </summary>
/// <remarks>
/// Each method other than <c>hello</c> and <c>shutdown</c> is one snake_case MCP tool and one <c>skua</c> subcommand with the same arguments and DTOs.
/// Failures are JSON-RPC errors whose code maps to an <see cref="ErrorCode"/> through <see cref="ErrorCodes"/>.
/// </remarks>
[JsonRpcContract]
public partial interface IEngineRpc
{
    /// <summary>The first call on every connection. Frozen across protocol versions.</summary>
    [JsonRpcMethod("hello")]
    Task<HelloResult> HelloAsync(int protocol, CancellationToken cancellationToken = default);

    /// <summary>Liveness and a summary of the Engine and its game. Never fails.</summary>
    [JsonRpcMethod("status")]
    Task<StatusDto> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a cooperative shutdown and returns before it finishes. Frozen across protocol versions.</summary>
    [JsonRpcMethod("shutdown")]
    Task ShutdownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the Script Source's <c>scripts.json</c>: every word of <paramref name="query"/> must appear in a Script's name,
    /// description, tags or path, ignoring case. An empty query matches every Script.
    /// </summary>
    /// <param name="tag">When set, only Scripts with this tag (ignoring case) match.</param>
    [JsonRpcMethod("scripts_search")]
    Task<ScriptsSearchResult> ScriptsSearchAsync(string query, string? tag = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs the Scripts on disk with the Script Source: a full download the first time, then only the Scripts changed since the last sync.
    /// A second update while one runs fails with <see cref="ErrorCode.Busy"/>.
    /// </summary>
    [JsonRpcMethod("scripts_update")]
    Task<ScriptsUpdateResult> ScriptsUpdateAsync(CancellationToken cancellationToken = default);
}
