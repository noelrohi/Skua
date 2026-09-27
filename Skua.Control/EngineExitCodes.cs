namespace Skua.Control;

/// <summary>The exit codes of the <c>skua-engine</c> process, which auto-start reads.</summary>
public static class EngineExitCodes
{
    public const int Success = 0;
    public const int Usage = 2;

    /// <summary>Another Engine holds the lock for this Engine Name.</summary>
    public const int AlreadyRunning = 3;

    public const int GameHostMissing = 4;
}
