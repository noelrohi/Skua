using Microsoft.Extensions.DependencyInjection;
using Skua.Control;

namespace Skua.Engine;

/// <summary>Who hosts an Engine: <c>skua-engine</c>, which never shows the game, or the Mac App (ADR 0006).</summary>
public enum EngineHostMode
{
    Headless,
    App,
}

/// <summary>How <see cref="HostedEngine.StartAsync"/> hosts an Engine.</summary>
public sealed class EngineHostOptions
{
    /// <summary>
    /// <see cref="EngineHostMode.Headless"/> logs to stderr as well, stops on SIGTERM and SIGINT and keeps the lag killer on.
    /// <see cref="EngineHostMode.App"/> does none of that, and gives the Game Host a Frame Buffer.
    /// </summary>
    public EngineHostMode Mode { get; init; } = EngineHostMode.Headless;

    /// <summary>Runs once the Engine holds its lock, before it writes anything; <c>skua-engine --detach</c> redirects its stdio here.</summary>
    public Action<EngineEndpoint>? LockAcquired { get; init; }

    /// <summary>Adds the host's own services after the Engine's, so they win, before <c>Ioc.Default</c> is configured.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    internal bool IsHeadless => Mode == EngineHostMode.Headless;
}

/// <summary>The Engine couldn't start; <see cref="ExitCode"/> is what <c>skua-engine</c> exits with.</summary>
public sealed class EngineStartException(int exitCode, string message) : Exception(message)
{
    /// <summary>One of <see cref="EngineExitCodes"/>.</summary>
    public int ExitCode { get; } = exitCode;
}
