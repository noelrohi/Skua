using System.Text;

namespace Skua.Control;

/// <summary>
/// Where one Engine listens and locks. Both the Engine and its clients compute it with the same rules.
/// </summary>
/// <remarks>
/// The socket is <c>&lt;SkuaDIR&gt;/engines/&lt;name&gt;.sock</c> unless <c>SKUA_ENGINE_SOCKET</c> overrides it.
/// The lock and the log always sit in <c>&lt;SkuaDIR&gt;/engines</c>, so one data folder never runs two Engines of the same name.
/// </remarks>
public sealed record EngineEndpoint
{
    /// <summary>Overrides the Skua data folder, as in Skua.Core.</summary>
    public const string SkuaDirVariable = "SKUA_DIR";

    /// <summary>Overrides the Engine's socket path.</summary>
    public const string SocketVariable = "SKUA_ENGINE_SOCKET";

    /// <summary>macOS's <c>sun_path</c> holds 104 bytes, including the terminating NUL.</summary>
    public const int MaxSocketPathBytes = 103;

    private EngineEndpoint(string name, string skuaDir, string socketPath)
    {
        Name = name;
        SkuaDir = skuaDir;
        SocketPath = socketPath;
        EnginesDir = Path.Combine(skuaDir, "engines");
        LockPath = Path.Combine(EnginesDir, name + ".lock");
        LogPath = Path.Combine(EnginesDir, name + ".log");
    }

    public string Name { get; }

    public string SkuaDir { get; }

    public string SocketPath { get; }

    /// <summary>Where the lock, the log and (unless overridden) the socket live; only the user may enter it.</summary>
    public string EnginesDir { get; }

    public string LockPath { get; }

    /// <summary>The Engine's own diagnostics when it runs detached.</summary>
    public string LogPath { get; }

    /// <summary>The default Skua data folder, honouring <c>SKUA_DIR</c>.</summary>
    public static string DefaultSkuaDir() =>
        Environment.GetEnvironmentVariable(SkuaDirVariable) is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Skua");

    /// <summary>Resolves the endpoint from this process's environment.</summary>
    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> for a bad name or an over-long socket path.</exception>
    public static EngineEndpoint FromEnvironment(string name = EngineName.Default) =>
        Resolve(name, DefaultSkuaDir(), Environment.GetEnvironmentVariable(SocketVariable));

    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> for a bad name or an over-long socket path.</exception>
    public static EngineEndpoint Resolve(string name, string skuaDir, string? socketOverride = null)
    {
        EngineName.Validate(name);
        skuaDir = Path.GetFullPath(skuaDir);
        string socketPath = socketOverride is { Length: > 0 }
            ? Path.GetFullPath(socketOverride)
            : Path.Combine(skuaDir, "engines", name + ".sock");

        int bytes = Encoding.UTF8.GetByteCount(socketPath);
        if (bytes > MaxSocketPathBytes)
            throw new ControlException(ErrorCode.InvalidArgument,
                $"The Engine socket path '{socketPath}' is {bytes} bytes; macOS allows {MaxSocketPathBytes}. Set {SocketVariable} to a shorter path.");

        return new EngineEndpoint(name, skuaDir, socketPath);
    }

    /// <summary>
    /// The environment an Engine for this endpoint must be started with, so that it computes the same paths.
    /// </summary>
    public IReadOnlyDictionary<string, string> EngineEnvironment() => new Dictionary<string, string>
    {
        [SkuaDirVariable] = SkuaDir,
        [SocketVariable] = SocketPath,
    };
}
