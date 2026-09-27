using Skua.Control;
using Skua.Core.Models.GitHub;

namespace Skua.App.Engine;

/// <summary>Between Core's <see cref="ScriptSource"/> and the Control Surface's <see cref="ScriptSourceDto"/>.</summary>
internal static class ScriptSourceMapping
{
    public static ScriptSourceDto ToDto(this ScriptSource source) => new(source.Owner, source.Repo, source.Branch);

    public static ScriptSource ToCore(this ScriptSourceDto source) => new() { Owner = source.Owner, Repo = source.Repo, Branch = source.Branch };
}
