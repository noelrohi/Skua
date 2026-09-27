using Skua.Control;
using StreamJsonRpc;

namespace Skua.App.Engine.Scripts;

/// <summary>Builds <see cref="ErrorCode.CompileFailed"/> from Core's compiler message, which holds one diagnostic per line.</summary>
internal static class CompileFailure
{
    /// <summary>The most diagnostics one error carries.</summary>
    private const int MaxDiagnostics = 100;

    /// <param name="subject">What didn't compile, e.g. "Farm/Leveling.cs" or "The snippet".</param>
    public static LocalRpcException Of(string subject, string compilerMessage)
    {
        List<string> diagnostics = compilerMessage.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        List<string> kept = diagnostics.Take(MaxDiagnostics).ToList();
        string more = diagnostics.Count > kept.Count ? $"\n… and {diagnostics.Count - kept.Count} more." : "";
        return RpcErrors.Of(ErrorCode.CompileFailed, $"{subject} doesn't compile:\n{string.Join("\n", kept)}{more}", kept);
    }
}
