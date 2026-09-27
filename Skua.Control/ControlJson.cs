using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace Skua.Control;

/// <summary>
/// The wire format shared by the Engine and its clients: JSON-RPC 2.0 with camelCase names and string enums.
/// </summary>
public static class ControlJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Wraps a connected socket in a JSON-RPC channel. Call <see cref="JsonRpc.StartListening"/> after adding targets.</summary>
    public static JsonRpc CreateRpc(Socket socket)
    {
        SystemTextJsonFormatter formatter = new() { JsonSerializerOptions = CreateOptions() };
        NetworkStream stream = new(socket, ownsSocket: true);
        return new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, formatter));
    }

    // Relaxed escaping keeps the JSON that the CLI and MCP show readable, e.g. a '+' in a build string.
    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
