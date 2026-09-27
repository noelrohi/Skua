using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>
/// The compile check: every Script in a Scripts checkout compiles through <c>script_options</c>, with the Engine's real compiler setup.
/// </summary>
public class CompileCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_check_compiles_every_Script_in_the_checkout_and_lists_each_failure_with_its_file_and_diagnostics()
    {
        await using EngineSandbox sandbox = new();
        string checkout = FakeCheckout(sandbox, new()
        {
            ["Lib.cs"] = """
                public class Lib
                {
                    public static string Greeting => "hi";
                }
                """,
            ["Farm/Good.cs"] = """
                //cs_include Scripts/Lib.cs
                using Skua.Core.Interfaces;

                public class Good
                {
                    public void ScriptMain(IScriptInterface bot) => bot.Log(Lib.Greeting);
                }
                """,
            ["Farm/Broken.cs"] = """
                using Skua.Core.Interfaces;

                public class Broken
                {
                    public void ScriptMain(IScriptInterface bot) => bot.Log("missing semicolon")
                }
                """,
            ["Other/WindowsOnly.cs"] = """
                using Skua.Core.Interfaces;
                #if !MACOS
                using System.Windows.Forms;
                #endif

                public class WindowsOnly
                {
                    public void ScriptMain(IScriptInterface bot) { }
                }
                """,
            ["Story/Classless.cs"] = "// Nothing here.",
        });
        await using GameFixture game = await CompileCheck.StartAsync(sandbox, checkout);

        CompileCheckResult result = await CompileCheck.RunAsync(game.Connection, CompileCheck.Scripts(checkout), CompileCheck.ScriptTimeout, Ct);

        Assert.Equal(["Lib.cs", "Farm/Good.cs", "Farm/Broken.cs", "Other/WindowsOnly.cs", "Story/Classless.cs"], result.Checked);
        Assert.Equal(["Farm/Broken.cs", "Story/Classless.cs"], result.Failures.Select(f => f.Script));
        Assert.Contains(result.Failures[0].Diagnostics, d => d.Contains("CS1002", StringComparison.Ordinal));
        Assert.Contains(result.Failures[1].Diagnostics, d => d.Contains("no class definitions", StringComparison.Ordinal));
        Assert.Null(result.TimedOut);
        string report = result.Report(new HashSet<string>());
        Assert.Contains("2 of 5 Scripts don't compile", report);
        Assert.Contains("Farm/Broken.cs", report);
        Assert.Contains("CS1002", report);
    }

    [Fact]
    public async Task A_Script_that_takes_too_long_to_compile_stops_the_check_there_and_fails_it()
    {
        await using EngineSandbox sandbox = new();
        string checkout = FakeCheckout(sandbox, new()
        {
            ["Farm/Broken.cs"] = TestScripts.Main("bot.Log(1)"),
            ["Farm/Stuck.cs"] = TestScripts.Main("", "public TestScript() => System.Threading.Thread.Sleep(10_000);"),
            ["Farm/After.cs"] = TestScripts.Main(""),
        });
        await using GameFixture game = await CompileCheck.StartAsync(sandbox, checkout);

        CompileCheckResult result = await CompileCheck.RunAsync(game.Connection, CompileCheck.Scripts(checkout), TimeSpan.FromSeconds(3), Ct);

        Assert.Equal("Farm/Stuck.cs", result.TimedOut);
        Assert.Equal(["Farm/Broken.cs", "Farm/Stuck.cs"], result.Checked);
        Assert.Equal(["Farm/Broken.cs"], result.Failures.Select(f => f.Script));
        HashSet<string> known = ["Farm/Broken.cs", "Farm/After.cs"];
        Assert.False(result.Passes(known));
        Assert.Empty(result.NoLongerFailing(known));
        Assert.Contains("stopped at Farm/Stuck.cs", result.Report(known));
    }

    [Fact]
    public void Against_the_known_failures_only_a_new_failure_or_a_known_failure_that_no_longer_fails_breaks_the_check()
    {
        CompileCheckResult result = new(["A.cs", "B.cs", "C.cs", "D.cs"],
            [new("A.cs", ErrorCode.CompileFailed, ["(1,1): error CS1002: ; expected"]), new("B.cs", ErrorCode.CompileFailed, ["(2,2): error CS0246: CoreFarms"])]);
        HashSet<string> known = ["A.cs", "C.cs", "Gone.cs"];

        Assert.False(result.Passes(known));
        Assert.True(new CompileCheckResult(["A.cs", "C.cs"], [result.Failures[0], result.Failures[0] with { Script = "C.cs" }]).Passes(new HashSet<string> { "A.cs", "C.cs" }));
        Assert.Equal(["B.cs"], result.NewFailures(known).Select(f => f.Script));
        Assert.Equal(["C.cs", "Gone.cs"], result.NoLongerFailing(known));
        string report = result.Report(known);
        Assert.StartsWith("2 of 4 Scripts don't compile: 1 new, 1 known.", report);
        Assert.Contains("Known failures that no longer fail", report);
        Assert.True(report.IndexOf("New failures", StringComparison.Ordinal) < report.IndexOf("CS0246", StringComparison.Ordinal));
        Assert.True(report.IndexOf("Known failures:", StringComparison.Ordinal) < report.IndexOf("CS1002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_known_failures_file_lists_one_Script_per_line_with_comments_and_blank_lines_ignored()
    {
        await using EngineSandbox sandbox = new();
        string file = Path.Combine(sandbox.SkuaDir, CompileCheck.KnownFailuresFile);
        File.WriteAllText(file, """
            # Broken upstream, not by macOS.

            Templates/MergeTemplate.cs   # placeholders
            Other/Issue#12.cs
            Core/TaskExtensions.cs
            """);

        IReadOnlySet<string> known = CompileCheck.KnownFailures(file);

        Assert.Equivalent(new[] { "Templates/MergeTemplate.cs", "Other/Issue#12.cs", "Core/TaskExtensions.cs" }, known, strict: true);
    }

    /// <summary>
    /// Compiles every Script in the Scripts checkout named by <c>SKUA_SCRIPTS_CHECKOUT</c>, such as <c>noelrohi/Scripts@Skua</c>;
    /// skipped when it is unset. Only a Script failing that isn't in the known failures, or a known failure that no longer fails, breaks it.
    /// Run it after each upstream merge into the fork; the Scripts compile check workflow runs it too.
    /// </summary>
    [Fact]
    public async Task Every_Script_in_the_Scripts_checkout_compiles()
    {
        string? checkout = Environment.GetEnvironmentVariable(CompileCheck.CheckoutVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(checkout), $"Set {CompileCheck.CheckoutVariable} to a Scripts checkout to compile every Script in it.");
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await CompileCheck.StartAsync(sandbox, checkout!);

        CompileCheckResult result = await CompileCheck.RunAsync(game.Connection, CompileCheck.Scripts(checkout!), CompileCheck.ScriptTimeout, Ct);

        IReadOnlySet<string> known = CompileCheck.KnownFailures(Path.Combine(AppContext.BaseDirectory, CompileCheck.KnownFailuresFile));
        string report = result.Report(known);
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        Assert.True(result.Passes(known), report);
    }

    /// <summary>A Scripts checkout in the sandbox with these files and a <c>scripts.json</c> listing them, in order, as the Script Source's generator writes it.</summary>
    private static string FakeCheckout(EngineSandbox sandbox, Dictionary<string, string> files)
    {
        string checkout = Path.Combine(sandbox.SkuaDir, "checkout");
        foreach ((string path, string source) in files)
        {
            string file = Path.Combine(checkout, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, source);
        }
        File.WriteAllText(Path.Combine(checkout, "scripts.json"),
            JsonSerializer.Serialize(files.Keys.Select(p => new { name = "null", path = p, fileName = Path.GetFileName(p) })));
        return checkout;
    }
}
