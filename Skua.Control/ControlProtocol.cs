namespace Skua.Control;

/// <summary>
/// The version of the Control Surface contract. Bump it on every breaking change to <see cref="IEngineRpc"/> or its DTOs.
/// </summary>
/// <remarks>
/// <c>hello</c> and <c>shutdown</c> are frozen across versions, so a client can always recognise an Engine of another version and stop it.
/// </remarks>
public static class ControlProtocol
{
    public const int Version = 6;
}
