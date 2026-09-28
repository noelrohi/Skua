using System.Diagnostics;
using Skua.Core.Interfaces;
using Skua.Core.Models;

namespace Skua.Avalonia.Services;

/// <summary>
/// Core's links and editors on macOS, as <c>ProcessStartService</c> opens them on Windows: a link or a folder opens as Finder would open it,
/// and the Scripts folder and a Script open in VS Code.
/// </summary>
/// <remarks>
/// VS Code is found by its <c>code</c> command, where its "Install 'code' command in PATH" puts it or inside its app bundle, since an app's
/// PATH is launchd's and not the shell's; else by its bundle id. Without it, the Scripts folder says so, as on Windows, and a Script opens in
/// the default text editor, as in Notepad on Windows. Each request runs off the calling thread, so the UI never waits for a process.
/// </remarks>
public sealed class MacProcessService : IProcessService
{
    private const string Open = "/usr/bin/open";
    private const string VSCodeBundleId = "com.microsoft.VSCode";

    private static readonly string[] s_codeCommands =
    [
        "/usr/local/bin/code",
        "/opt/homebrew/bin/code",
        "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"),
    ];

    private readonly IDialogService _dialogs;
    private readonly Func<string, IReadOnlyList<string>, bool> _run;
    private readonly Func<string?> _codeCommand;

    public MacProcessService(IDialogService dialogs)
        : this(dialogs, Run, () => s_codeCommands.FirstOrDefault(File.Exists))
    {
    }

    /// <param name="run">Runs a program with its arguments and says whether it succeeded; the tests record instead.</param>
    /// <param name="codeCommand">VS Code's <c>code</c> command, or null when it isn't installed.</param>
    public MacProcessService(IDialogService dialogs, Func<string, IReadOnlyList<string>, bool> run, Func<string?> codeCommand)
    {
        _dialogs = dialogs;
        _run = run;
        _codeCommand = codeCommand;
    }

    /// <summary>Opens a URL in the default browser, or a folder in Finder.</summary>
    public void OpenLink(string link) => InBackground(() =>
    {
        // Never an option of open's.
        if (!link.StartsWith('-'))
            _run(Open, [link]);
    });

    public void OpenVSC() => InBackground(() =>
    {
        if (!OpenInVSCode([ClientFileSources.SkuaScriptsDIR]))
            _dialogs.ShowMessageBox("VS Code was not found. Install it, then run \"Shell Command: Install 'code' command in PATH\" from its Command Palette.", "VS Code not found");
    });

    public void OpenVSC(string path) => InBackground(() =>
    {
        if (!OpenInVSCode([ClientFileSources.SkuaScriptsDIR, path]) && !path.StartsWith('-'))
            _run(Open, ["-t", path]);
    });

    /// <summary>Opens the files or folders in one VS Code window, as <c>code</c> does.</summary>
    private bool OpenInVSCode(IReadOnlyList<string> paths)
    {
        if (_codeCommand() is { } code)
            return _run(code, paths);
        return !paths.Any(p => p.StartsWith('-')) && _run(Open, ["-b", VSCodeBundleId, .. paths]);
    }

    private static void InBackground(Action action) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Couldn't open: {e.Message}");
        }
    });

    /// <summary>Starts the program and waits briefly for it: <c>open</c> and <c>code</c> hand over to the app and exit at once.</summary>
    private static bool Run(string program, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(program) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
                return false;
            return !process.WaitForExit(TimeSpan.FromSeconds(10)) || process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
