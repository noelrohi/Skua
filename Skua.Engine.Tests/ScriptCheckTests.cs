using System.Text.Json;
using Skua.App.Cli;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>skua scripts check</c>: a Script and its includes compile as a start would compile them, with no Engine.</summary>
public class ScriptCheckTests
{
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
    public async Task An_include_that_doesnt_exist_is_an_error_at_its_line()
    {
        await using EngineSandbox sandbox = new();
        string script = TestScripts.Write(sandbox, "Farm/Lost.cs", "using Skua.Core.Interfaces;\n//cs_include Scripts/Nowhere.cs\n" + TestScripts.Main(""));

        ProcessResult result = await sandbox.RunCliAsync("scripts", "check", "Farm/Lost.cs", "--json");

        Assert.Equal(ExitCodes.For(ErrorCode.CompileFailed), result.ExitCode);
        using JsonDocument error = JsonDocument.Parse(result.Stdout);
        Assert.Equal($"{script}(2,1): error: the include Scripts/Nowhere.cs doesn't exist in {Path.Combine(sandbox.SkuaDir, "Scripts")}",
            error.RootElement.GetProperty("error").GetProperty("diagnostics").EnumerateArray().Single().GetString());
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
