using System.Text.RegularExpressions;

namespace Skua.Control;

/// <summary>
/// The short name that identifies one Engine on a Mac.
/// </summary>
public static partial class EngineName
{
    public const string Default = "default";

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);

    /// <exception cref="ControlException"><see cref="ErrorCode.InvalidArgument"/> when the name doesn't match <c>[a-z0-9-]{1,16}</c>.</exception>
    public static string Validate(string? name)
    {
        if (!IsValid(name))
            throw new ControlException(ErrorCode.InvalidArgument, $"Engine Name '{name}' is invalid; it must match [a-z0-9-]{{1,16}}.");
        return name!;
    }

    [GeneratedRegex("^[a-z0-9-]{1,16}$")]
    private static partial Regex Pattern();
}
