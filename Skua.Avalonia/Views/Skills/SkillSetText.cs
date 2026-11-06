using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skua.Core.Models.Skills;

namespace Skua.Avalonia.Views.Skills;

/// <summary>
/// Saved skill sets as clipboard text: a JSON array with each set's class, mode, skills, timeout (in ticks, plus milliseconds when not whole ticks), use mode and reset, so a set copied from one
/// app pastes into another whole. The WPF control copies into a list of its own that only it can paste.
/// </summary>
public static class SkillSetText
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        // Text a person reads and pastes, not HTML: H<40% stays as it is.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string From(IEnumerable<AdvancedSkill> sets) =>
        JsonSerializer.Serialize(sets.Select(s => new SkillSet(s.ClassName, s.ClassUseMode, s.Skills, s.SkillTimeout, s.SkillUseMode, s.ResetComboOnTargetChange,
            s.SkillTimeoutMs % AdvancedSkill.TickMs == 0 ? null : s.SkillTimeoutMs)), s_options);

    /// <summary>The sets in <paramref name="text"/>, an array of them or one; none for anything else, or for a set with no class or skills.</summary>
    public static IReadOnlyList<AdvancedSkill> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        try
        {
            SkillSet?[]? sets = text.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<SkillSet?[]>(text, s_options)
                : [JsonSerializer.Deserialize<SkillSet>(text, s_options)];
            return sets is null
                ? []
                : sets.OfType<SkillSet>()
                    .Where(s => !string.IsNullOrWhiteSpace(s.ClassName) && !string.IsNullOrWhiteSpace(s.Skills))
                    .Select(s => new AdvancedSkill(s.ClassName.Trim(), s.Skills.Trim(), s.SkillTimeout, s.ClassUseMode, s.SkillUseMode, s.ResetComboOnTargetChange)
                    {
                        SkillTimeoutMs = s.SkillTimeoutMs ?? s.SkillTimeout * AdvancedSkill.TickMs
                    })
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record SkillSet(
        string ClassName,
        ClassUseMode ClassUseMode,
        string Skills,
        int SkillTimeout = 250,
        SkillUseMode SkillUseMode = SkillUseMode.UseIfAvailable,
        bool ResetComboOnTargetChange = false,
        // Milliseconds, as the saved skills file writes them: only when not a whole number of ticks, and winning over them.
        int? SkillTimeoutMs = null);
}
