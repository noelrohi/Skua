using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Compiles Scripts from a Scripts checkout through <c>script_options</c> and collects every one that fails.</summary>
public static class CompileCheck
{
    /// <summary>Names the Scripts checkout the compile check runs over.</summary>
    public const string CheckoutVariable = "SKUA_SCRIPTS_CHECKOUT";

    /// <summary>The Scripts known not to compile, broken upstream rather than by macOS; it sits next to the tests.</summary>
    public const string KnownFailuresFile = "compile-check-known-failures.txt";

    /// <summary>How long one Script may take to compile before the check stops.</summary>
    public static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(2);

    /// <summary>The Scripts in a known-failures file: one per line, where a <c>#</c> at the start of a line or after a space starts a comment.</summary>
    public static IReadOnlySet<string> KnownFailures(string file) =>
        File.ReadLines(file).Select(l => Regex.Replace(l, @"(^|\s)#.*", "").Trim()).Where(l => l.Length > 0).ToHashSet();

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
            if (relative.Split(Path.DirectorySeparatorChar)[0] == ".git")
                continue;
            string target = Path.Combine(scripts, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>
    /// Asks for each Script's options, one at a time, which compiles it with its includes as a start would.
    /// A Script that takes longer than <paramref name="scriptTimeout"/> stops the check there, since the Engine is still busy with it.
    /// </summary>
    public static async Task<CompileCheckResult> RunAsync(
        EngineConnection connection, IReadOnlyList<string> scripts, TimeSpan scriptTimeout, CancellationToken cancellationToken)
    {
        List<CompileCheckFailure> failures = [];
        for (int i = 0; i < scripts.Count; i++)
        {
            try
            {
                // The Engine can't cancel a compile, so this stops waiting instead of cancelling the call.
                await connection.ScriptOptionsAsync(scripts[i], cancellationToken).WaitAsync(scriptTimeout, cancellationToken);
            }
            catch (ControlException e)
            {
                failures.Add(new CompileCheckFailure(scripts[i], e.Code, e.Diagnostics is { Count: > 0 } diagnostics ? diagnostics : [e.Message]));
            }
            catch (TimeoutException)
            {
                return new CompileCheckResult(scripts.Take(i + 1).ToList(), failures, TimedOut: scripts[i]);
            }
        }
        return new CompileCheckResult(scripts, failures);
    }
}

/// <param name="Diagnostics">The compiler's errors, or the error's message when it isn't a compile error.</param>
public sealed record CompileCheckFailure(string Script, ErrorCode Code, IReadOnlyList<string> Diagnostics);

/// <param name="TimedOut">The Script the check stopped at because it took too long to compile; the Scripts after it weren't checked.</param>
public sealed record CompileCheckResult(IReadOnlyList<string> Checked, IReadOnlyList<CompileCheckFailure> Failures, string? TimedOut = null)
{
    public bool Passes(IReadOnlySet<string> known) => TimedOut is null && NewFailures(known).Count == 0 && NoLongerFailing(known).Count == 0;

    public IReadOnlyList<CompileCheckFailure> NewFailures(IReadOnlySet<string> known) => Failures.Where(f => !known.Contains(f.Script)).ToList();

    /// <summary>The known failures that compile now or are gone from the checkout, so the known-failures file is out of date.</summary>
    public IReadOnlyList<string> NoLongerFailing(IReadOnlySet<string> known) =>
        known.Except(Failures.Select(f => f.Script)).Where(s => TimedOut is null || Checked.Contains(s)).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every failing Script with its diagnostics, new failures first. Scripts that fail the same way share one entry,
    /// so a broken include shows its diagnostics once rather than once for every Script that includes it.
    /// </summary>
    public string Report(IReadOnlySet<string> known)
    {
        List<CompileCheckFailure> newFailures = [.. NewFailures(known)];
        List<string> noLongerFailing = [.. NoLongerFailing(known)];
        StringBuilder report = new(Failures.Count == 0
            ? $"All {Checked.Count} Scripts compile.\n"
            : $"{Failures.Count} of {Checked.Count} Scripts don't compile: {newFailures.Count} new, {Failures.Count - newFailures.Count} known.\n");
        if (TimedOut is not null)
            report.AppendLine($"\nThe check stopped at {TimedOut}, which took too long to compile; the Scripts after it weren't checked.");
        if (noLongerFailing.Count > 0)
        {
            report.AppendLine($"\nKnown failures that no longer fail; take them off {CompileCheck.KnownFailuresFile}:");
            foreach (string script in noLongerFailing)
                report.AppendLine($"  {script}");
        }
        AppendFailures(report, "New failures:", newFailures);
        AppendFailures(report, "Known failures:", Failures.Where(f => known.Contains(f.Script)).ToList());
        return report.ToString();
    }

    private static void AppendFailures(StringBuilder report, string heading, List<CompileCheckFailure> failures)
    {
        if (failures.Count == 0)
            return;
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
