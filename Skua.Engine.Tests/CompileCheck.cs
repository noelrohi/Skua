using System.Text;
using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Compiles Scripts from a Scripts checkout through <c>script_options</c> and collects every one that fails.</summary>
public static class CompileCheck
{
    /// <summary>Names the Scripts checkout the compile check runs over.</summary>
    public const string CheckoutVariable = "SKUA_SCRIPTS_CHECKOUT";

    /// <summary>The Scripts known not to compile on any platform, next to the tests.</summary>
    public const string KnownFailuresFile = "compile-check-known-failures.txt";

    /// <summary>The Scripts in a known-failures file: one per line, where <c>#</c> starts a comment.</summary>
    public static IReadOnlySet<string> KnownFailures(string file) =>
        File.ReadLines(file).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToHashSet();

    /// <summary>The Scripts the checkout's <c>scripts.json</c> lists, which are the ones a Script Source offers, in its order.</summary>
    public static IReadOnlyList<string> Scripts(string checkout)
    {
        using FileStream json = File.OpenRead(Path.Combine(checkout, "scripts.json"));
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()!).ToList();
    }

    /// <summary>Starts an Engine against the fake Game Host whose Scripts folder is a copy of the checkout.</summary>
    public static async Task<GameFixture> StartAsync(EngineSandbox sandbox, string checkout)
    {
        UseCheckout(sandbox, checkout);
        GameFixture game = await GameFixture.StartAsync(sandbox);
        // The Engine logs every compile on stderr; unread, the pipe fills after about a thousand and the Engine blocks.
        _ = game.Engine.StandardError.BaseStream.CopyToAsync(Stream.Null);
        _ = game.Engine.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        return game;
    }

    /// <summary>
    /// Makes the checkout the sandbox's Scripts folder, so the Engine finds it through the data-folder override.
    /// It is copied, because compiling writes Core's include cache into the Scripts folder.
    /// </summary>
    private static void UseCheckout(EngineSandbox sandbox, string checkout)
    {
        string scripts = Path.Combine(sandbox.SkuaDir, "Scripts");
        foreach (string file in Directory.EnumerateFiles(checkout, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(checkout, file);
            if (relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            string target = Path.Combine(scripts, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Asks for each Script's options, one at a time, which compiles it with its includes as a start would.</summary>
    public static async Task<CompileCheckResult> RunAsync(EngineConnection connection, IEnumerable<string> scripts, CancellationToken cancellationToken)
    {
        List<string> checkedScripts = [];
        List<CompileCheckFailure> failures = [];
        foreach (string script in scripts)
        {
            checkedScripts.Add(script);
            try
            {
                await connection.ScriptOptionsAsync(script, cancellationToken);
            }
            catch (ControlException e)
            {
                failures.Add(new CompileCheckFailure(script, e.Code, e.Diagnostics is { Count: > 0 } diagnostics ? diagnostics : [e.Message]));
            }
        }
        return new CompileCheckResult(checkedScripts, failures);
    }
}

/// <param name="Diagnostics">The compiler's errors, or the error's message when it isn't a compile error.</param>
public sealed record CompileCheckFailure(string Script, ErrorCode Code, IReadOnlyList<string> Diagnostics);

public sealed record CompileCheckResult(IReadOnlyList<string> Checked, IReadOnlyList<CompileCheckFailure> Failures)
{
    public IReadOnlyList<CompileCheckFailure> NewFailures(IReadOnlySet<string> known) => Failures.Where(f => !known.Contains(f.Script)).ToList();

    /// <summary>The known failures that compile now or are gone from the checkout, so the known-failures file is out of date.</summary>
    public IReadOnlyList<string> NoLongerFailing(IReadOnlySet<string> known) =>
        known.Except(Failures.Select(f => f.Script)).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every failing Script with its diagnostics, new failures first. Scripts that fail the same way share one entry,
    /// so a broken include shows its diagnostics once rather than once for every Script that includes it.
    /// </summary>
    public string Report(IReadOnlySet<string>? known = null)
    {
        known ??= new HashSet<string>();
        List<CompileCheckFailure> newFailures = [.. NewFailures(known)];
        List<string> noLongerFailing = [.. NoLongerFailing(known)];
        StringBuilder report = new(Failures.Count == 0
            ? $"All {Checked.Count} Scripts compile.\n"
            : known.Count == 0
                ? $"{Failures.Count} of {Checked.Count} Scripts don't compile.\n"
                : $"{Failures.Count} of {Checked.Count} Scripts don't compile: {newFailures.Count} new, {Failures.Count - newFailures.Count} known.\n");
        if (noLongerFailing.Count > 0)
        {
            report.AppendLine($"\nKnown failures that no longer fail; take them off {CompileCheck.KnownFailuresFile}:");
            foreach (string script in noLongerFailing)
                report.AppendLine($"  {script}");
        }
        AppendFailures(report, known.Count == 0 ? null : "New failures:", newFailures);
        AppendFailures(report, "Known failures:", Failures.Where(f => known.Contains(f.Script)).ToList());
        return report.ToString();
    }

    private static void AppendFailures(StringBuilder report, string? heading, List<CompileCheckFailure> failures)
    {
        if (failures.Count == 0)
            return;
        if (heading is not null)
            report.AppendLine().AppendLine(heading);
        foreach (IGrouping<string, CompileCheckFailure> group in failures.GroupBy(f => $"{f.Code}\n{string.Join("\n", f.Diagnostics)}"))
        {
            report.AppendLine();
            foreach (CompileCheckFailure failure in group)
                report.AppendLine(failure.Script);
            report.AppendLine($"  {group.First().Code}:");
            foreach (string diagnostic in group.First().Diagnostics)
                report.AppendLine($"    {diagnostic}");
        }
    }
}
