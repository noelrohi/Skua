using System.Collections.Concurrent;
using System.Diagnostics;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>The compile cache in <c>Scripts/Cached-Scripts</c>, which every Engine of a data folder shares.</summary>
public class ScriptCacheTests
{
    private const int Includes = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A party's <c>script start</c> on each Engine at once: every one updates the Scripts, which downloads what changed, then compiles a Script
    /// whose includes the others are compiling, loading or replacing in the cache at the same time.
    /// </summary>
    [Fact]
    public async Task Engines_of_one_data_folder_updating_and_compiling_at_once_all_compile()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        github.Commit("noelrohi", "Scripts", "Skua", Version(0));
        EngineEndpoint[] endpoints = [.. Enumerable.Range(1, 4).Select(i => EngineEndpoint.Resolve($"alt{i}", sandbox.SkuaDir))];
        List<EngineConnection> connections = [];
        try
        {
            foreach (EngineEndpoint endpoint in endpoints)
                connections.Add(await StartEngineAsync(sandbox, github, endpoint));

            ConcurrentBag<string> failures = [];
            for (int round = 1; round <= 4; round++)
            {
                // Each round changes every include, so each update downloads them and each compile misses the cache.
                github.Commit("noelrohi", "Scripts", "Skua", Version(round));
                await Task.WhenAll(connections.Select(connection => Task.Run(async () =>
                {
                    for (int start = 0; start < 3; start++)
                    {
                        try
                        {
                            await connection.ScriptsUpdateAsync(Ct);
                            await connection.ScriptOptionsAsync("Party/Main.cs", Ct);
                        }
                        catch (ControlException e)
                        {
                            failures.Add($"round {round}: {e.Message} {string.Join(" ", e.Diagnostics ?? [])}");
                        }
                    }
                }, Ct)));
            }

            Assert.True(failures.IsEmpty, string.Join("\n", failures));
        }
        finally
        {
            foreach (EngineConnection connection in connections)
                connection.Dispose();
            await Task.WhenAll(endpoints.Select(endpoint => EngineClient.StopAsync(endpoint, EngineSandbox.StopTimeout)));
        }
    }

    /// <summary>The cache isn't cleared on update, so an unchanged include's entry must not outlive a change to the include it compiled against.</summary>
    [Fact]
    public async Task A_changed_include_recompiles_the_unchanged_includes_over_it()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        // A constant is compiled into the include that reads it, so a stale entry would still say 1.
        FakeScript step = new("Party/Step.cs", """
            //cs_include Scripts/Party/Core.cs
            public class Step
            {
                public int Run() => PartyCore.Count;
            }
            """);
        FakeScript main = new("Party/Main.cs", """
            //cs_include Scripts/Party/Step.cs
            using System.Collections.Generic;
            using Skua.Core.Interfaces;
            using Skua.Core.Options;

            public class PartyScript
            {
                public List<IOption> Options = new() { new Option<int>("count", "Count", "", new Step().Run()) };

                public void ScriptMain(IScriptInterface bot) { }
            }
            """, "Party", "A party Script.");
        github.Commit("noelrohi", "Scripts", "Skua", Core(1), step, main);
        EngineEndpoint endpoint = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        using EngineConnection connection = await StartEngineAsync(sandbox, github, endpoint);
        try
        {
            await connection.ScriptsUpdateAsync(Ct);
            ScriptOptionsResult before = await connection.ScriptOptionsAsync("Party/Main.cs", Ct);
            github.Commit("noelrohi", "Scripts", "Skua", Core(2));
            ScriptsUpdateResult update = await connection.ScriptsUpdateAsync(Ct);
            ScriptOptionsResult after = await connection.ScriptOptionsAsync("Party/Main.cs", Ct);

            Assert.Equal("1", Assert.Single(before.Options).Default);
            Assert.Equal(["Party/Core.cs"], update.Changed);
            Assert.Equal("2", Assert.Single(after.Options).Default);
        }
        finally
        {
            await EngineClient.StopAsync(endpoint, EngineSandbox.StopTimeout);
        }

        static FakeScript Core(int count) => new("Party/Core.cs", $$"""
            public class PartyCore
            {
                public const int Count = {{count}};
            }
            """);
    }

    /// <summary>
    /// The Script's entry is keyed on the includes it compiled against, not on the include files as they are by then: another Engine's update
    /// can replace one between the includes' compile and the Script's key, and a key read from the files would then name a Script compiled
    /// against the old include. Here the replaced include keeps its old date, so only its contents say it changed.
    /// </summary>
    [Fact]
    public async Task The_Script_recompiles_when_an_include_it_compiled_against_changes_whatever_the_files_dates()
    {
        await using EngineSandbox sandbox = new();
        await using FakeGitHub github = new();
        string core = TestScripts.Write(sandbox, "Party/Core.cs", Core(1));
        TestScripts.Write(sandbox, "Party/Main.cs", """
            //cs_include Scripts/Party/Core.cs
            using System.Collections.Generic;
            using Skua.Core.Interfaces;
            using Skua.Core.Options;

            public class PartyScript
            {
                // A constant is compiled into the Script that reads it, so a stale Script entry would still say 1.
                public List<IOption> Options = new() { new Option<int>("count", "Count", "", PartyCore.Count) };

                public void ScriptMain(IScriptInterface bot) { }
            }
            """);
        EngineEndpoint endpoint = EngineEndpoint.Resolve("alt1", sandbox.SkuaDir);
        using EngineConnection connection = await StartEngineAsync(sandbox, github, endpoint);
        try
        {
            ScriptOptionsResult before = await connection.ScriptOptionsAsync("Party/Main.cs", Ct);
            DateTime written = File.GetLastWriteTimeUtc(core);
            TestScripts.Write(sandbox, "Party/Core.cs", Core(2));
            File.SetLastWriteTimeUtc(core, written);
            ScriptOptionsResult after = await connection.ScriptOptionsAsync("Party/Main.cs", Ct);

            Assert.Equal("1", Assert.Single(before.Options).Default);
            Assert.Equal("2", Assert.Single(after.Options).Default);
        }
        finally
        {
            await EngineClient.StopAsync(endpoint, EngineSandbox.StopTimeout);
        }

        static string Core(int count) => $$"""
            public class PartyCore
            {
                public const int Count = {{count}};
            }
            """;
    }

    /// <summary>A Script with a chain of includes over a shared core, like a party Script over CoreBots, each file marked with its version.</summary>
    private static FakeScript[] Version(int version)
    {
        List<FakeScript> scripts =
        [
            new("Party/Core.cs", $$"""
                // v{{version}}
                public class PartyCore
                {
                    public int Value(int i) => i + {{version}};
                }
                """),
        ];
        for (int i = 1; i <= Includes; i++)
        {
            string previous = i == 1 ? "" : $"//cs_include Scripts/Party/Step{i - 1}.cs\n";
            scripts.Add(new($"Party/Step{i}.cs", $$"""
                //cs_include Scripts/Party/Core.cs
                {{previous}}// v{{version}}
                public class Step{{i}}
                {
                    public int Run() => new PartyCore().Value({{i}});
                }
                """));
        }
        scripts.Add(new("Party/Main.cs", $$"""
            //cs_include Scripts/Party/Step{{Includes}}.cs
            using Skua.Core.Interfaces;

            // v{{version}}
            public class PartyScript
            {
                public void ScriptMain(IScriptInterface bot) => new Step{{Includes}}().Run();
            }
            """, "Party", "A party Script."));
        return [.. scripts];
    }

    private static async Task<EngineConnection> StartEngineAsync(EngineSandbox sandbox, FakeGitHub github, EngineEndpoint endpoint)
    {
        Dictionary<string, string> environment = new(github.Environment());
        foreach ((string key, string value) in endpoint.EngineEnvironment())
            environment[key] = value;
        Process engine = sandbox.StartEngineProcess(environment, "--name", endpoint.Name);
        // The Engine logs every compile on stderr; unread, the pipe fills and the Engine blocks.
        Task<string> stderr = engine.StandardError.ReadToEndAsync(Ct);
        _ = engine.StandardOutput.BaseStream.CopyToAsync(Stream.Null, Ct);
        Stopwatch waited = Stopwatch.StartNew();
        while (waited.Elapsed < EngineSandbox.StopTimeout)
        {
            if (await EngineClient.TryConnectAsync(endpoint, Ct) is { } connection)
            {
                connection.EnsureCompatible();
                return connection;
            }
            if (engine.HasExited)
                throw new InvalidOperationException($"skua-engine '{endpoint.Name}' exited with {engine.ExitCode}: {await stderr}");
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"skua-engine '{endpoint.Name}' didn't answer.");
    }
}
