using System.Text.Json.Serialization;

namespace Skua.Core.Models.GitHub;

/// <summary>
/// The repository Scripts are fetched from, <c>owner/repo@branch</c>; a setting whose default is the app's: <c>auqw/Scripts@Skua</c>, which
/// <c>new ScriptSource()</c> makes, except on macOS, where the Engine passes its own.
/// </summary>
public class ScriptSource
{
    public const string DefaultOwner = "auqw";
    public const string DefaultRepo = "Scripts";
    public const string DefaultBranch = "Skua";

    /// <summary>Environment variable that overrides <c>https://raw.githubusercontent.com/</c>, so tests never reach GitHub.</summary>
    public const string RawUrlEnvironmentVariable = "SKUA_GITHUB_RAW_URL";

    /// <summary>Environment variable that overrides <c>https://api.github.com/</c>, so tests never reach GitHub.</summary>
    public const string ApiUrlEnvironmentVariable = "SKUA_GITHUB_API_URL";

    private static readonly string _rawBaseUrl = BaseUrl(RawUrlEnvironmentVariable, "https://raw.githubusercontent.com/");
    private static readonly string _apiBaseUrl = BaseUrl(ApiUrlEnvironmentVariable, "https://api.github.com/");

    [JsonPropertyName("Owner")]
    public string Owner { get; set; } = DefaultOwner;

    [JsonPropertyName("Repo")]
    public string Repo { get; set; } = DefaultRepo;

    [JsonPropertyName("Branch")]
    public string Branch { get; set; } = DefaultBranch;

    [JsonIgnore]
    public string CommitUrl => $"{_apiBaseUrl}repos/{Owner}/{Repo}/commits/{Branch}";

    public string CompareUrl(string fromSha, string toSha) => $"{_apiBaseUrl}repos/{Owner}/{Repo}/compare/{fromSha}...{toSha}";

    /// <summary>The raw URL of a file in the Script Source, e.g. <c>scripts.json</c> or <c>Farm/Leveling.cs</c>.</summary>
    public string RawFileUrl(string path) =>
        $"{_rawBaseUrl}{Owner}/{Repo}/refs/heads/{Branch}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    public void InitializeDefaults()
    {
        if (string.IsNullOrWhiteSpace(Owner))
            Owner = DefaultOwner;
        if (string.IsNullOrWhiteSpace(Repo))
            Repo = DefaultRepo;
        if (string.IsNullOrWhiteSpace(Branch))
            Branch = DefaultBranch;
    }

    public override string ToString() => $"{Owner}/{Repo}@{Branch}";

    private static string BaseUrl(string variable, string defaultUrl) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } url ? url.TrimEnd('/') + "/" : defaultUrl;
}
