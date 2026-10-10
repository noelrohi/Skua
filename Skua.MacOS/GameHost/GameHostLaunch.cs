namespace Skua.MacOS.GameHost;

/// <summary>What the Engine runs as its Game Host: the <c>skua-gamehost</c> executable and the <c>skua.swf</c> it plays.</summary>
public sealed record GameHostLaunch(string Executable, string Swf)
{
    public const string ExecutableVariable = "SKUA_GAMEHOST";
    public const string SwfVariable = "SKUA_SWF";

    public const string ExecutableName = "skua-gamehost";
    public const string SwfName = "skua.swf";

    /// <summary>The Game Client's stage size, which the Game Host renders at while headless, and every screenshot's size.</summary>
    public const int StageWidth = 958;
    public const int StageHeight = 550;

    /// <summary>
    /// A live Game View renders at its size in device pixels (twice the stage's on Retina at the native size), up to this many times the
    /// stage's, which sizes the Frame Buffer's slots. Pages of a slot are only touched as frames grow into them.
    /// </summary>
    public const int MaxViewScale = 3;
    public const int MaxViewWidth = StageWidth * MaxViewScale;
    public const int MaxViewHeight = StageHeight * MaxViewScale;

    /// <summary>Whether the Game Host gets a Frame Buffer for the Game View: in the Mac App, never in <c>skua-engine</c>.</summary>
    public bool WantsFrameBuffer { get; init; }

    /// <summary>
    /// The folder where the Game Host keeps the game's local SharedObjects (its Favorites and options) across restarts: the Engine Name's
    /// own (ADR 0008). Without one they live in memory and are lost when the Game Host exits.
    /// </summary>
    public string? StorageDir { get; init; }

    /// <summary>The Game Host's arguments, with the storage folder and the Frame Buffer's name when it has them.</summary>
    public IReadOnlyList<string> Arguments(string? frameBufferName = null) =>
    [
        .. StorageDir is null ? [] : new[] { $"--storage={StorageDir}" },
        .. frameBufferName is null ? [] : new[] { $"--frame-buffer={frameBufferName}" },
        Swf,
    ];

    /// <summary>
    /// Resolves both files next to <paramref name="baseDirectory"/> (the flat output layout), unless <c>SKUA_GAMEHOST</c>
    /// or <c>SKUA_SWF</c> names another path.
    /// </summary>
    /// <exception cref="FileNotFoundException">A file doesn't exist; the message names its path.</exception>
    public static GameHostLaunch Resolve(string baseDirectory)
    {
        GameHostLaunch launch = new(
            ResolveFile(ExecutableVariable, baseDirectory, ExecutableName),
            ResolveFile(SwfVariable, baseDirectory, SwfName));

        if (!File.Exists(launch.Executable))
            throw new FileNotFoundException($"The Game Host '{launch.Executable}' doesn't exist; build Skua.App.Engine on macOS, or set {ExecutableVariable}.", launch.Executable);
        if (!File.Exists(launch.Swf))
            throw new FileNotFoundException($"The Game Client's SWF '{launch.Swf}' doesn't exist; build Skua.App.Engine on macOS, or set {SwfVariable}.", launch.Swf);

        return launch;
    }

    private static string ResolveFile(string variable, string baseDirectory, string name) =>
        Path.GetFullPath(Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path ? path : Path.Combine(baseDirectory, name));
}
