using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;

namespace Skua.App.Cli;

/// <summary>
/// <c>skua --skill</c>: the agent skill for driving Engines, <c>skills/skua/SKILL.md</c> embedded at build time, so the printed copy always
/// matches this build; and the footer of <c>skua --help</c> that points agents at it.
/// </summary>
internal static class Skill
{
    public const string HelpFooter = """
        Are you an AI driving Skua Engines?
          SKIP if a Skua skill is already in your context. Otherwise run: skua --skill
        """;

    /// <summary>Adds <c>--skill</c> to the root command, and the footer to its help but not to a command's.</summary>
    public static void AddTo(RootCommand root)
    {
        root.Options.Add(new Option<bool>("--skill") { Description = "Print the agent skill for driving Engines and exit.", Action = new PrintAction() });
        HelpOption help = root.Options.OfType<HelpOption>().Single();
        help.Action = new HelpWithFooter((HelpAction)help.Action!, root);
    }

    private sealed class PrintAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            // Byte for byte: no text decoding, so the copy is the file.
            using Stream skill = typeof(Skill).Assembly.GetManifestResourceStream("SKILL.md")!;
            using Stream stdout = Console.OpenStandardOutput();
            skill.CopyTo(stdout);
            return ExitCodes.Success;
        }
    }

    private sealed class HelpWithFooter(HelpAction help, RootCommand root) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            int exitCode = help.Invoke(parseResult);
            if (parseResult.CommandResult.Command == root)
                parseResult.InvocationConfiguration.Output.WriteLine(HelpFooter);
            return exitCode;
        }
    }
}
