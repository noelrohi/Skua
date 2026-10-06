using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>skua scripts check</c>: a Script and its includes compile as a start would compile them, with no Engine.</summary>
public class ScriptCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Lib = """
        public class Lib
        {
            public static string Greeting => "hi";
        }
        """;

    private const string UsesLib = """
        //cs_include Scripts/Lib.cs
        using Skua.Core.Interfaces;

        public class UsesLib
        {
            public void ScriptMain(IScriptInterface bot) => bot.Log(Lib.Greeting);
        }
        """;

    [Fact]
    public async Task A_good_Script_with_its_includes_passes_with_exit_code_0()
    {
        await using EngineSandbox sandbox = new();
        TestScripts.Write(sandbox, "Lib.cs", Lib);
        string script = TestScripts.Write(sandbox, "Farm/UsesLib.cs", UsesLib);

        ProcessResult human = await sandbox.RunCliAsync("scripts", "check", "Farm/UsesLib.cs");
        ProcessResult json = await sandbox.RunCliAsync("scripts", "check", "Farm/UsesLib.cs", "--json");

        Assert.True(human.ExitCode == 0, human.Stderr);
        Assert.Equal($"{script} compiles, with 1 include.", human.Stdout.Trim());
        Assert.True(json.ExitCode == 0, json.Stderr);
        using JsonDocument result = JsonDocument.Parse(json.Stdout);
        Assert.Equal(script, result.RootElement.GetProperty("script").GetString());
        Assert.Equal(Path.Combine(sandbox.SkuaDir, "Scripts"), result.RootElement.GetProperty("scriptsFolder").GetString());
        Assert.Equal([Path.Combine(sandbox.SkuaDir, "Scripts", "Lib.cs")], result.RootElement.GetProperty("includes").EnumerateArray().Select(e => e.GetString()));
        // The check compiles elsewhere, so it leaves no compiled includes behind for an Engine to trip over.
        Assert.False(Directory.Exists(Path.Combine(sandbox.SkuaDir, "Scripts", "Cached-Scripts")));
    }

    [Fact]
    public async Task A_broken_Script_fails_with_the_compile_code_and_each_error_at_its_file_and_line()
    {
        await using EngineSandbox sandbox = new();
        string script = TestScripts.Write(sandbox, "Farm/Broken.cs", """
            //cs_include Scripts/Lib.cs

            using Skua.Core.Interfaces;

            public class Broken
            {
                public void ScriptMain(IScriptInterface bot) => bot.Log(Lib.Greeting)
            }
            """);
        TestScripts.Write(sandbox, "Lib.cs", Lib);

        ProcessResult human = await sandbox.RunCliAsync("scripts", "check", "Farm/Broken.cs");
        ProcessResult json = await sandbox.RunCliAsync("scripts", "check", "Farm/Broken.cs", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), human.ExitCode);
        Assert.Contains($"{script}(7,74): error CS1002: ; expected", human.Stderr);
        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), json.ExitCode);
        using JsonDocument error = JsonDocument.Parse(json.Stdout);
        Assert.Equal("compileFailed", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal($"{script}(7,74): error CS1002: ; expected",
            error.RootElement.GetProperty("error").GetProperty("diagnostics").EnumerateArray().Single().GetString());
    }

    [Fact]
    public async Task An_error_in_an_include_is_reported_at_the_includes_own_file_and_line()
    {
        await using EngineSandbox sandbox = new();
        string lib = TestScripts.Write(sandbox, "Lib.cs", """
            //cs_include Scripts/Other.cs
            public class Lib
            {
                public static string Greeting => NoSuchType.Value;
            }
            """);
        TestScripts.Write(sandbox, "Other.cs", "public class Other { }");
        TestScripts.Write(sandbox, "Farm/UsesLib.cs", UsesLib);

        ProcessResult result = await sandbox.RunCliAsync("scripts", "check", "Farm/UsesLib.cs");

        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), result.ExitCode);
        Assert.Contains($"{lib}(4,38): error CS0103: The name 'NoSuchType' does not exist in the current context", result.Stderr);
    }

    [Fact]
    public async Task An_include_that_doesnt_exist_is_skipped_as_on_the_Engine_with_a_warning_at_its_line()
    {
        await using EngineSandbox sandbox = new();
        string unneeded = TestScripts.Write(sandbox, "Farm/Unneeded.cs", "//cs_include Scripts/Nowhere.cs\n" + TestScripts.Main(""));
        string needed = TestScripts.Write(sandbox, "Farm/Needed.cs", "//cs_include Scripts/Lib.cs\n" + TestScripts.Main("bot.Log(Lib.Greeting);"));
        string warning = $"(1,1): warning: the include Scripts/{{0}} doesn't exist in {Path.Combine(sandbox.SkuaDir, "Scripts")}, so it is skipped";
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        await game.Connection.ScriptOptionsAsync("Farm/Unneeded.cs", Ct);
        ControlException engine = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptOptionsAsync("Farm/Needed.cs", Ct));
        ProcessResult passes = await sandbox.RunCliAsync("scripts", "check", "Farm/Unneeded.cs", "--json");
        ProcessResult fails = await sandbox.RunCliAsync("scripts", "check", "Farm/Needed.cs", "--json");

        Assert.Equal(ErrorCode.CompileFailed, engine.Code);
        Assert.True(passes.ExitCode == 0, passes.Stdout + passes.Stderr);
        using (JsonDocument json = JsonDocument.Parse(passes.Stdout))
            Assert.Equal([unneeded + string.Format(warning, "Nowhere.cs")], json.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), fails.ExitCode);
        using (JsonDocument json = JsonDocument.Parse(fails.Stdout))
        {
            string?[] diagnostics = [.. json.RootElement.GetProperty("error").GetProperty("diagnostics").EnumerateArray().Select(d => d.GetString())];
            Assert.Equal(needed + string.Format(warning, "Lib.cs"), diagnostics[0]);
            Assert.Contains(diagnostics, d => d!.StartsWith($"{needed}(", StringComparison.Ordinal) && d.Contains("CS0103"));
        }
    }

    [Fact]
    public async Task The_main_Scripts_includes_count_on_any_line_and_an_includes_only_before_its_first_using_as_on_the_Engine()
    {
        await using EngineSandbox sandbox = new();
        TestScripts.Write(sandbox, "Lib.cs", Lib);
        TestScripts.Write(sandbox, "Other.cs", "public class Other { public static string Name => \"other\"; }");
        TestScripts.Write(sandbox, "LateLib.cs", """
            using System;
            //cs_include Scripts/Other.cs
            public class LateLib
            {
                public static string Name => Other.Name;
            }
            """);
        TestScripts.Write(sandbox, "Farm/Late.cs", """
            using Skua.Core.Interfaces;
            //cs_include Scripts/Lib.cs

            public class Late
            {
                public void ScriptMain(IScriptInterface bot) => bot.Log(Lib.Greeting);
            }
            """);
        TestScripts.Write(sandbox, "Farm/UsesLateLib.cs", "//cs_include Scripts/LateLib.cs\n" + TestScripts.Main("bot.Log(LateLib.Name);"));
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        await game.Connection.ScriptOptionsAsync("Farm/Late.cs", Ct);
        ControlException engine = await Assert.ThrowsAsync<ControlException>(() => game.Connection.ScriptOptionsAsync("Farm/UsesLateLib.cs", Ct));
        ProcessResult late = await sandbox.RunCliAsync("scripts", "check", "Farm/Late.cs");
        ProcessResult usesLateLib = await sandbox.RunCliAsync("scripts", "check", "Farm/UsesLateLib.cs");

        Assert.Equal(ErrorCode.CompileFailed, engine.Code);
        Assert.True(late.ExitCode == 0, late.Stderr);
        Assert.Contains("with 1 include", late.Stdout);
        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), usesLateLib.ExitCode);
        Assert.Contains($"{Path.Combine(sandbox.SkuaDir, "Scripts", "LateLib.cs")}(5,34): error CS0103: The name 'Other' does not exist", usesLateLib.Stderr);
    }

    [Fact]
    public async Task An_include_outside_the_Scripts_folder_by_its_absolute_path_compiles_as_on_the_Engine_with_errors_at_its_own_lines()
    {
        await using EngineSandbox sandbox = new();
        string elsewhere = Directory.CreateDirectory(Path.Combine(sandbox.SkuaDir, "elsewhere")).FullName;
        string good = Path.Combine(elsewhere, "Good.cs");
        File.WriteAllText(good, "public class External { public static string Name => \"external\"; }");
        string broken = Path.Combine(elsewhere, "Broken.cs");
        File.WriteAllText(broken, "//cs_include Scripts/Lib.cs\npublic class Broken\n{\n    public static string Name => NoSuchType.Value;\n}");
        TestScripts.Write(sandbox, "Lib.cs", Lib);
        TestScripts.Write(sandbox, "Farm/UsesGood.cs", $"//cs_include {good}\n" + TestScripts.Main("bot.Log(External.Name);"));
        TestScripts.Write(sandbox, "Farm/UsesBroken.cs", $"//cs_include {broken}\n" + TestScripts.Main("bot.Log(Broken.Name);"));
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        await game.Connection.ScriptOptionsAsync("Farm/UsesGood.cs", Ct);
        ProcessResult usesGood = await sandbox.RunCliAsync("scripts", "check", "Farm/UsesGood.cs", "--json");
        ProcessResult usesBroken = await sandbox.RunCliAsync("scripts", "check", "Farm/UsesBroken.cs");

        Assert.True(usesGood.ExitCode == 0, usesGood.Stdout + usesGood.Stderr);
        using (JsonDocument json = JsonDocument.Parse(usesGood.Stdout))
            Assert.Equal([good], json.RootElement.GetProperty("includes").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), usesBroken.ExitCode);
        Assert.Contains($"{broken}(4,34): error CS0103: The name 'NoSuchType' does not exist", usesBroken.Stderr);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere).Except([good, broken]));
    }

    [Fact]
    public async Task A_Script_in_a_Scripts_checkout_compiles_against_that_checkout_and_leaves_it_untouched()
    {
        await using EngineSandbox sandbox = new();
        // The data folder's Lib is broken; the checkout's, which the Script means, isn't.
        TestScripts.Write(sandbox, "Lib.cs", "public class Lib {");
        string checkout = Path.Combine(sandbox.SkuaDir, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "Farm"));
        File.WriteAllText(Path.Combine(checkout, "scripts.json"), "[]");
        File.WriteAllText(Path.Combine(checkout, "Lib.cs"), Lib);
        string script = Path.Combine(checkout, "Farm", "UsesLib.cs");
        File.WriteAllText(script, UsesLib);
        string[] before = Directory.GetFileSystemEntries(checkout, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        ProcessResult result = await sandbox.RunCliAsync("scripts", "check", script, "--json");
        ProcessResult relative = await sandbox.RunCliAsync("scripts", "check", "checkout/Farm/UsesLib.cs");

        Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
        using (JsonDocument json = JsonDocument.Parse(result.Stdout))
            Assert.Equal(checkout, json.RootElement.GetProperty("scriptsFolder").GetString());
        Assert.True(relative.ExitCode == 0, relative.Stderr);
        Assert.Equal(before, Directory.GetFileSystemEntries(checkout, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_Script_that_isnt_there_fails_with_the_not_found_code()
    {
        await using EngineSandbox sandbox = new();

        ProcessResult result = await sandbox.RunCliAsync("scripts", "check", "Farm/Nothing.cs");

        Assert.Equal(ExitCodes.For(ErrorCode.ScriptNotFound), result.ExitCode);
        Assert.Contains("Farm/Nothing.cs", result.Stderr);
    }
}
