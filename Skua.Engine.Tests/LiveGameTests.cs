namespace Skua.Engine.Tests;

/// <summary>
/// The live-game tests: a real Engine, the real Game Host and the Test Account, driven through the Control Surface. They never run in CI,
/// and each logs in once. Set <c>SKUA_LIVE</c> to the one to run (<c>smoke</c>, <c>memory</c>, <c>crowded</c> or <c>hidden</c>) and
/// <c>SKUA_SCRIPTS_CHECKOUT</c> to a <c>noelrohi/Scripts@Skua</c> checkout; see BUILD.md. The others skip.
/// </summary>
public class LiveGameTests
{
    /// <summary>Login, then battleon for 5 minutes with the headless defaults, then <c>Farm/Leveling.cs</c> for 15.</summary>
    [Fact]
    public async Task Smoke()
    {
        LiveSettings.SkipUnlessSelected("smoke");
        LiveRunResult result = await LiveRun.RunAsync("smoke", LiveSettings.RealGame(), LiveSettings.Options("smoke"),
            run => LiveScenarios.SmokeAsync(run, LiveSettings.Server, LiveScenarios.Leveling));
        result.AssertPassed();
    }

    /// <summary>The 2-hour memory gate: <c>Farm/Leveling.cs</c> for 2 hours, the Game Host's footprint under 2 GB and flat.</summary>
    [Fact]
    public async Task Memory_gate()
    {
        LiveSettings.SkipUnlessSelected("memory");
        LiveRunResult result = await LiveRun.RunAsync("memory", LiveSettings.RealGame(), LiveSettings.Options("memory"),
            run => LiveScenarios.MemoryGateAsync(run, LiveSettings.Server, LiveScenarios.Leveling));
        result.AssertPassed();
    }

    /// <summary>Crowded battleon: a 2-hour idle there with the headless defaults, the Game Host's footprint under 2 GB and flat.</summary>
    [Fact]
    public async Task Crowded_battleon()
    {
        LiveSettings.SkipUnlessSelected("crowded");
        LiveRunResult result = await LiveRun.RunAsync("crowded", LiveSettings.RealGame(), LiveSettings.Options("crowded"),
            run => LiveScenarios.CrowdedBattleonAsync(run, LiveSettings.Server));
        result.AssertPassed();
    }

    /// <summary>
    /// Hidden running: <c>Farm/Leveling.cs</c> for 30 minutes with the screen locked. It waits for the developer to lock the screen after
    /// the Script starts; the Script must progress, the Game Host tick at its frame rate without throttled gaps, and screenshots be correct.
    /// </summary>
    [Fact]
    public async Task Hidden_running()
    {
        LiveSettings.SkipUnlessSelected("hidden");
        LiveRunResult result = await LiveRun.RunAsync("hidden", LiveSettings.RealGame(), LiveSettings.Options("hidden"),
            run => LiveScenarios.HiddenAsync(run, LiveSettings.Server, LiveScenarios.Leveling, lockWait: LiveSettings.LockWait));
        result.AssertPassed();
    }
}

/// <summary>The live-game scenarios; the dry runs play them against the fake Game Host with short minutes.</summary>
public static class LiveScenarios
{
    public const string Leveling = "Farm/Leveling.cs";

    public static async Task SmokeAsync(LiveRun run, string server, string script, int battleonMinutes = 5, int scriptMinutes = 15)
    {
        await run.LoginAsync(server);
        await JoinBattleonAsync(run);
        await run.CheckScreenshotAsync("battleon");
        await run.PhaseAsync(new LivePhase("battleon", battleonMinutes));
        await run.StartScriptAsync(script);
        await run.PhaseAsync(new LivePhase("leveling", scriptMinutes, Script: true));
        await run.CheckScreenshotAsync("leveling");
    }

    public static async Task MemoryGateAsync(LiveRun run, string server, string script, int minutes = 120)
    {
        await run.LoginAsync(server);
        await run.StartScriptAsync(script);
        await run.PhaseAsync(new LivePhase("leveling", minutes, Script: true, ScreenshotEvery: 30, FlatForMinutes: 60));
    }

    public static async Task CrowdedBattleonAsync(LiveRun run, string server, int minutes = 120)
    {
        await run.LoginAsync(server);
        await JoinBattleonAsync(run);
        await run.PhaseAsync(new LivePhase("battleon", minutes, ScreenshotEvery: 30, FlatForMinutes: 60));
    }

    public static async Task HiddenAsync(LiveRun run, string server, string script, TimeSpan lockWait, int minutes = 30)
    {
        await run.LoginAsync(server);
        await run.StartScriptAsync(script);
        await run.WaitForScreenLockAsync(lockWait);
        await run.PhaseAsync(new LivePhase("hidden", minutes, Script: true, Hidden: true, ScreenshotEvery: 10));
    }

    /// <summary>Joins battleon with a timed, real join: through yulgar when the login already landed in battleon.</summary>
    private static async Task JoinBattleonAsync(LiveRun run)
    {
        if (await run.JoinAsync("battleon"))
            return;
        await run.JoinAsync("yulgar");
        await run.JoinAsync("battleon");
    }
}

/// <summary>What selects and configures a live-game test, from the environment.</summary>
public static class LiveSettings
{
    /// <summary>Names the one live-game test to run: <c>smoke</c>, <c>memory</c>, <c>crowded</c> or <c>hidden</c>.</summary>
    public const string SelectVariable = "SKUA_LIVE";

    /// <summary>The server to log in on; Galanoth unless set.</summary>
    public const string ServerVariable = "SKUA_LIVE_SERVER";

    /// <summary>Where each run's folder goes; <c>live-results</c> next to the tests unless set.</summary>
    public const string OutVariable = "SKUA_LIVE_OUT";

    /// <summary>How many minutes the hidden-running check waits for the screen to be locked; 10 unless set.</summary>
    public const string LockWaitVariable = "SKUA_LIVE_LOCK_WAIT_MIN";

    /// <summary>The Scripts checkout the runs copy into their data folder (shared with the compile check).</summary>
    public const string ScriptsCheckoutVariable = "SKUA_SCRIPTS_CHECKOUT";

    public static string Server => Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } server ? server : "Galanoth";

    public static TimeSpan LockWait =>
        TimeSpan.FromMinutes(int.TryParse(Environment.GetEnvironmentVariable(LockWaitVariable), out int minutes) ? minutes : 10);

    /// <summary>Skips the test unless <c>SKUA_LIVE</c> names it, and always in CI.</summary>
    public static void SkipUnlessSelected(string name)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CI") is { Length: > 0 }, "The live-game tests never run in CI.");
        Assert.SkipUnless(string.Equals(Environment.GetEnvironmentVariable(SelectVariable), name, StringComparison.OrdinalIgnoreCase),
            $"A live-game test; set {SelectVariable}={name} to run it (it logs the Test Account in once).");
    }

    /// <summary>Options for a live run on a quiet Mac, with the Scripts checkout; it fails at once without one.</summary>
    public static LiveRunOptions Options(string name)
    {
        string? checkout = Environment.GetEnvironmentVariable(ScriptsCheckoutVariable);
        Assert.False(string.IsNullOrEmpty(checkout) || !Directory.Exists(checkout),
            $"Set {ScriptsCheckoutVariable} to a noelrohi/Scripts@Skua checkout; the live runs play Scripts from it.");
        string root = Environment.GetEnvironmentVariable(OutVariable) is { Length: > 0 } dir ? dir : Path.Combine(EngineSandbox.BinDir, "live-results");
        return new LiveRunOptions
        {
            OutDir = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}"),
            RequireQuietMac = true,
            ScriptsCheckout = checkout,
        };
    }

    /// <summary>
    /// Points the Engine at the real Game Host and <c>skua.swf</c> next to the tests, the real Keychain and the real servers API,
    /// instead of the fakes every other test uses.
    /// </summary>
    public static Dictionary<string, string> RealGame() => new()
    {
        ["SKUA_GAMEHOST"] = Path.Combine(EngineSandbox.BinDir, "skua-gamehost"),
        ["SKUA_SWF"] = Path.Combine(EngineSandbox.BinDir, "skua.swf"),
        // Empty means the default: /usr/bin/security and content.aq.com.
        ["SKUA_SECURITY_TOOL"] = "",
        ["SKUA_AQ_SERVERS_URL"] = "",
    };
}
