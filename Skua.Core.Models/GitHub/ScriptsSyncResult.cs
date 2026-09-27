namespace Skua.Core.Models.GitHub;

public enum ScriptsSyncMode
{
    /// <summary>The first sync from this Script Source: every missing or outdated Script was downloaded.</summary>
    Full,

    /// <summary>Only the Scripts changed since the last synced commit were downloaded.</summary>
    Incremental,

    /// <summary>The Script Source hasn't changed since the last sync.</summary>
    UpToDate,
}

/// <summary>What one sync of the Scripts with their Script Source did.</summary>
/// <param name="Commit">The Script Source commit synced to.</param>
/// <param name="Downloaded">How many Script files were downloaded.</param>
/// <param name="Failed">The paths of Scripts that failed to download; the commit isn't recorded, so the next sync retries them.</param>
public sealed record ScriptsSyncResult(ScriptSource Source, ScriptsSyncMode Mode, string Commit, int Downloaded, IReadOnlyList<string> Failed);
