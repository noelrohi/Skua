using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Skua.Core.Interfaces;

namespace Skua.Avalonia.Manager;

/// <summary>
/// The Updates tab on macOS, in place of Windows' Client Updates, which downloads Skua release zips: whether the build <c>install-macos.sh</c>
/// installed matches the checkout, and how to update. It downloads nothing; a release says it updates itself (<see cref="AppUpdates"/>).
/// </summary>
/// <remarks>
/// A build is <c>&lt;Version&gt;+&lt;commit&gt;</c>, plus <c>.dirty.&lt;time&gt;</c> for uncommitted changes, as <c>install-macos.sh</c> names it.
/// </remarks>
public sealed partial class UpdatesViewModel : ObservableObject
{
    /// <summary>Overrides the checkout the app was built from, as the tests do.</summary>
    public const string CheckoutVariable = "SKUA_CHECKOUT";

    /// <summary>The checkout path the Mac App's build records in its assembly metadata.</summary>
    public const string CheckoutMetadata = "SkuaCheckout";

    private readonly IClipboardService _clipboard;
    private readonly AppUpdates.Release? _release;

    public UpdatesViewModel(IClipboardService clipboard)
        : this(clipboard, AppUpdates.Current())
    {
    }

    /// <param name="release">The release this app is, or null for a dev build.</param>
    public UpdatesViewModel(IClipboardService clipboard, AppUpdates.Release? release)
    {
        _clipboard = clipboard;
        _release = release;
        Refresh();
    }

    /// <summary>A release, which updates itself: the tab shows no command.</summary>
    public bool IsRelease => _release is not null;

    /// <summary>This app's own build.</summary>
    public static string AppBuild =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    [ObservableProperty]
    private string _installed = "";

    [ObservableProperty]
    private string _checkout = "";

    [ObservableProperty]
    private string _verdict = "";

    [ObservableProperty]
    private bool _upToDate;

    /// <summary>The command that installs the checkout's build.</summary>
    [ObservableProperty]
    private string _updateCommand = "";

    public string App => AppBuild;

    [RelayCommand]
    public void Refresh()
    {
        if (_release is { } release)
        {
            Installed = InstalledBuild(out string skua) ?? $"{skua} doesn't link to a build";
            Checkout = "None: a release isn't built from a checkout";
            UpdateCommand = "";
            UpToDate = true;
            Verdict = $"Skua {release.Version} is a release: it checks for updates by itself and asks before installing one. To check now, choose {AppUpdates.CheckHeader} in a Skua app's Skua menu.";
            return;
        }
        string? repo = CheckoutPath();
        string? installed = InstalledBuild(out string link);
        string? checkout = repo is null ? null : CheckoutBuild(repo, out bool dirty);
        Installed = installed ?? $"Not installed ({link} doesn't link to a build)";
        Checkout = repo is null ? "Unknown: this app doesn't know the checkout it was built from" : checkout is null ? $"{repo} (not a git checkout)" : $"{checkout} in {repo}";
        UpdateCommand = repo is null ? "./install-macos.sh" : $"cd {Quote(repo)} && ./install-macos.sh";

        bool dirtyCheckout = checkout?.EndsWith(".dirty", StringComparison.Ordinal) == true;
        UpToDate = installed is not null && checkout is not null && !dirtyCheckout && installed == checkout;
        Verdict = (installed, checkout) switch
        {
            (_, null) => "Can't compare: the checkout is unknown.",
            (null, _) => "Skua isn't installed from this checkout yet. Run the command below.",
            _ when UpToDate => "The installed build matches the checkout.",
            _ when dirtyCheckout => "The checkout has uncommitted changes; the command below installs them as a build of their own.",
            _ => "The installed build doesn't match the checkout. Run the command below to install the checkout's build, then quit and reopen the Skua apps.",
        };
    }

    [RelayCommand]
    private void CopyCommand() => _clipboard.SetText(UpdateCommand);

    /// <summary>
    /// The build <c>skua</c> links to: <c>$SKUA_BIN_DIR/skua</c> (default <c>~/.local/bin/skua</c>) → <c>…/versions/&lt;build&gt;/skua</c>, or
    /// <c>…/versions/&lt;build&gt;/Skua.app/Contents/Helpers/skua</c> when the app is installed too.
    /// </summary>
    public static string? InstalledBuild(out string link)
    {
        string binDir = Environment.GetEnvironmentVariable("SKUA_BIN_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
        link = Path.Combine(binDir, "skua");
        try
        {
            if (new FileInfo(link).LinkTarget is not { } target)
                return null;
            for (string? folder = Path.GetDirectoryName(target); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                if (Path.GetFileName(Path.GetDirectoryName(folder)) == "versions")
                    return Path.GetFileName(folder);
            }
            return Path.GetFileName(Path.GetDirectoryName(target)) is { Length: > 0 } build ? build : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The checkout's build as <c>install-macos.sh</c> would name it, with <c>.dirty</c> (and no time) for uncommitted changes; null when git can't tell.</summary>
    public static string? CheckoutBuild(string repo, out bool dirty)
    {
        dirty = false;
        string props = Path.Combine(repo, "Directory.Build.props");
        if (!File.Exists(props) || Regex.Match(File.ReadAllText(props), "<Version>(.*)</Version>") is not { Success: true } version)
            return null;
        if (Git(repo, "rev-parse", "HEAD") is not { Length: > 0 } commit)
            return null;
        dirty = Git(repo, "status", "--porcelain") is { Length: > 0 };
        return $"{version.Groups[1].Value}+{commit}{(dirty ? ".dirty" : "")}";
    }

    private static string? CheckoutPath()
    {
        string? path = Environment.GetEnvironmentVariable(CheckoutVariable) is { Length: > 0 } variable
            ? variable
            : Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == CheckoutMetadata)?.Value;
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    private static string? Git(string repo, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repo);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        try
        {
            using Process git = Process.Start(startInfo)!;
            string output = git.StandardOutput.ReadToEnd();
            git.StandardError.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string Quote(string path) => path.All(c => char.IsLetterOrDigit(c) || "/._-".Contains(c)) ? path : $"'{path.Replace("'", "'\\''")}'";
}
