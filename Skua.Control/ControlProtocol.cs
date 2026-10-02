using System.Reflection;

namespace Skua.Control;

/// <summary>
/// The version of the Control Surface contract. Bump it on every breaking change to <see cref="IEngineRpc"/> or its DTOs.
/// </summary>
/// <remarks>
/// <c>hello</c>, <c>shutdown</c> and <c>shutdown_if_idle</c> are frozen across versions, so a client can always recognise an Engine of
/// another version and stop it.
/// </remarks>
public static class ControlProtocol
{
    public const int Version = 12;

    /// <summary>This build: the version and the commit it was built from, which the Engine and its CLI share when built together.</summary>
    public static string Build { get; } =
        typeof(ControlProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
