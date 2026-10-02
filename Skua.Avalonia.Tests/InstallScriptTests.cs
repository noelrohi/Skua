using System.Diagnostics;
using System.Reflection;

namespace Skua.Avalonia.Tests;

/// <summary>
/// install-macos.sh (#182) with stub <c>dotnet</c> and <c>cargo</c> first on PATH, into this test's own install, bin and apps folders: it
/// installs <c>skua-tui</c> next to <c>skua</c>, and fails before writing anything when cargo is missing; with <c>SKUA_NO_TUI=1</c> (#184)
/// it needs no cargo and installs everything but <c>skua-tui</c>. The real publish is the maintainer's (BUILD.md, "Install on macOS").
/// </summary>
public sealed class InstallScriptTests : IDisposable
{
    private static readonly string Repo = typeof(InstallScriptTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SkuaRepo").Value!;

    private readonly string _dir = Directory.CreateTempSubdirectory("skua-install-").FullName;
    private string Stubs => Path.Combine(_dir, "stubs");
    private string Install => Path.Combine(_dir, "install");
    private string Bin => Path.Combine(_dir, "bin");
    private string Apps => Path.Combine(_dir, "apps");
    private string CargoLog => Path.Combine(_dir, "cargo.log");
    private string DotnetLog => Path.Combine(_dir, "dotnet.log");
    private string Target => Path.Combine(_dir, "target");
    private string Checkout => Path.Combine(_dir, "checkout");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void It_builds_skua_tui_in_release_and_links_it_next_to_skua(bool app)
    {
        StubDotnet();
        StubCargo();

        (int exit, string output) = RunScript(app ? ["--app"] : []);

        Assert.True(exit == 0, output);
        Assert.Equal($"{Path.Combine(Checkout, "Skua.Tui")} build --release --locked", File.ReadAllText(CargoLog).Trim());
        string skua = new FileInfo(Path.Combine(Bin, "skua")).LinkTarget!;
        string tui = new FileInfo(Path.Combine(Bin, "skua-tui")).LinkTarget!;
        Assert.Equal(Path.Combine(Path.GetDirectoryName(skua)!, "skua-tui"), tui);
        Assert.StartsWith(Path.Combine(Install, "versions") + "/", tui);
        Assert.EndsWith(app ? "/Skua.app/Contents/Helpers/skua-tui" : "/skua-tui", tui);
        Assert.Equal((0, "usage: skua-tui\n"), Run(Path.Combine(Bin, "skua-tui"), ["--help"]));
        if (app)
            AssertSigned(tui);
    }

    [Fact]
    public void Without_cargo_it_fails_saying_what_to_install_before_writing_anything()
    {
        StubDotnet();

        (int exit, string output) = RunScript([]);

        Assert.Equal(1, exit);
        Assert.Contains("cargo was not found", output);
        Assert.Contains("https://rustup.rs", output);
        Assert.False(File.Exists(DotnetLog));
        Assert.False(Directory.Exists(Install));
        Assert.False(Directory.Exists(Bin));
        Assert.False(Directory.Exists(Apps));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_SKUA_NO_TUI_it_installs_skua_without_cargo_or_skua_tui(bool app)
    {
        StubDotnet();

        (int exit, string output) = RunScript(app ? ["--app"] : [], noTui: true);

        Assert.True(exit == 0, output);
        string skua = new FileInfo(Path.Combine(Bin, "skua")).LinkTarget!;
        Assert.StartsWith(Path.Combine(Install, "versions") + "/", skua);
        Assert.Equal((0, "skua\n"), Run(Path.Combine(Bin, "skua"), []));
        Assert.Null(new FileInfo(Path.Combine(Bin, "skua-tui")).LinkTarget);
        Assert.Empty(Directory.GetFiles(Install, "skua-tui", SearchOption.AllDirectories));
        if (app)
            AssertSigned(skua);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_SKUA_NO_TUI_it_removes_the_skua_tui_link_of_an_earlier_install(bool app)
    {
        StubDotnet();
        StubCargo();
        Assert.Equal(0, RunScript(app ? ["--app"] : []).Exit);

        (int exit, string output) = RunScript(app ? ["--app"] : [], noTui: true);

        Assert.True(exit == 0, output);
        Assert.Null(new FileInfo(Path.Combine(Bin, "skua-tui")).LinkTarget);
    }

    [Fact]
    public void With_SKUA_NO_TUI_it_keeps_a_skua_tui_link_to_another_install()
    {
        StubDotnet();
        Directory.CreateDirectory(Bin);
        string other = Path.Combine(_dir, "other-skua-tui");
        File.CreateSymbolicLink(Path.Combine(Bin, "skua-tui"), other);

        (int exit, string output) = RunScript([], noTui: true);

        Assert.True(exit == 0, output);
        Assert.Equal(other, new FileInfo(Path.Combine(Bin, "skua-tui")).LinkTarget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_full_install_adds_skua_tui_to_a_build_of_the_same_commit_made_with_SKUA_NO_TUI(bool app)
    {
        StubDotnet();
        Assert.Equal(0, RunScript(app ? ["--app"] : [], noTui: true).Exit);
        StubCargo();

        (int exit, string output) = RunScript(app ? ["--app"] : []);

        Assert.True(exit == 0, output);
        // A plain build gets skua-tui beside its files; a Skua.app is built again, as nothing runs from it.
        Assert.Equal(app ? 2 : 1, File.ReadAllLines(DotnetLog).Length);
        string skua = new FileInfo(Path.Combine(Bin, "skua")).LinkTarget!;
        string tui = new FileInfo(Path.Combine(Bin, "skua-tui")).LinkTarget!;
        Assert.Equal(Path.Combine(Path.GetDirectoryName(skua)!, "skua-tui"), tui);
        Assert.Equal((0, "usage: skua-tui\n"), Run(Path.Combine(Bin, "skua-tui"), ["--help"]));
        if (app)
            AssertSigned(tui);
    }

    [Fact]
    public void A_full_install_leaves_a_running_Skua_app_made_with_SKUA_NO_TUI_alone()
    {
        StubDotnet();
        Assert.Equal(0, RunScript(["--app"], noTui: true).Exit);
        StubCargo();
        string skua = new FileInfo(Path.Combine(Bin, "skua")).LinkTarget!;
        using Process running = Process.Start(new ProcessStartInfo("/usr/bin/tail", ["-f", skua]) { RedirectStandardOutput = true })!;
        try
        {
            (int exit, string output) = RunScript(["--app"]);

            Assert.Equal(1, exit);
            Assert.Contains("was built without skua-tui and is running", output);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(skua)!, "skua-tui")));
            Assert.Single(File.ReadAllLines(DotnetLog));
        }
        finally
        {
            running.Kill();
            running.WaitForExit();
        }
    }

    /// <summary>The Skua.app <paramref name="helper"/> is in verifies, nested code and all.</summary>
    private static void AssertSigned(string helper)
    {
        string bundle = helper[..helper.IndexOf("/Contents/", StringComparison.Ordinal)];
        (int verify, string why) = Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", bundle]);
        Assert.True(verify == 0, why);
    }

    /// <summary>A <c>dotnet publish</c> that writes a <c>skua</c> where the real one would, and for <c>--app</c> a signed bundle around it.</summary>
    private void StubDotnet() => WriteStub("dotnet", $$"""
        #!/bin/sh
        echo "$*" >> '{{DotnetLog}}'
        prev=
        for a in "$@"; do
          case "$a" in -p:SkuaAppBundle=*) app="${a#-p:SkuaAppBundle=}" ;; esac
          [ "$prev" = --output ] && out="$a"
          prev="$a"
        done
        if [ -n "$app" ]; then
          out="$app/Contents/Helpers"
          mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
          printf '<?xml version="1.0" encoding="UTF-8"?>\n<plist version="1.0"><dict><key>CFBundleIdentifier</key><string>test.skua</string><key>CFBundleExecutable</key><string>Skua</string><key>CFBundlePackageType</key><string>APPL</string></dict></plist>\n' > "$app/Contents/Info.plist"
          touch "$app/Contents/Resources/Skua.icns"
          printf '#!/bin/sh\n' > "$app/Contents/MacOS/Skua"
          chmod +x "$app/Contents/MacOS/Skua"
        fi
        mkdir -p "$out"
        printf '#!/bin/sh\necho skua\n' > "$out/skua"
        chmod +x "$out/skua"
        [ -z "$app" ] || codesign --force --deep --sign - "$app"
        """);

    /// <summary>A <c>cargo build</c> that logs where it ran and writes a <c>skua-tui</c> into this test's target folder, which its
    /// <c>cargo metadata</c> names.</summary>
    private void StubCargo() => WriteStub("cargo", $$"""
        #!/bin/sh
        if [ "$1" = metadata ]; then
          echo '{"packages":[],"target_directory":"{{Target}}","version":1}'
          exit
        fi
        echo "$PWD $*" >> '{{CargoLog}}'
        mkdir -p '{{Target}}/release'
        printf '#!/bin/sh\necho "usage: skua-tui"\n' > '{{Target}}/release/skua-tui'
        chmod +x '{{Target}}/release/skua-tui'
        """);

    private void WriteStub(string name, string script)
    {
        Directory.CreateDirectory(Stubs);
        string path = Path.Combine(Stubs, name);
        File.WriteAllText(path, script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Runs the checkout's install-macos.sh with every folder it writes in this test's own, and only system tools besides the stubs;
    /// <paramref name="noTui"/> sets <c>SKUA_NO_TUI=1</c>. It runs from a committed copy, so each run names the same build whether or not
    /// this checkout has uncommitted changes.</summary>
    private (int Exit, string Output) RunScript(string[] arguments, bool noTui = false)
    {
        if (!Directory.Exists(Checkout))
        {
            Directory.CreateDirectory(Path.Combine(Checkout, "Skua.Tui"));
            foreach (string file in new[] { "install-macos.sh", "Directory.Build.props" })
                File.Copy(Path.Combine(Repo, file), Path.Combine(Checkout, file));
            foreach (string[] git in new string[][] { ["init", "-q"], ["add", "."], ["-c", "user.name=test", "-c", "user.email=test@test", "commit", "-qm", "test"] })
                Assert.Equal(0, Run("/usr/bin/git", ["-C", Checkout, .. git]).Exit);
        }

        Dictionary<string, string> environment = new()
        {
            ["PATH"] = $"{Stubs}:/usr/bin:/bin:/usr/sbin:/sbin",
            ["HOME"] = _dir,
            ["SKUA_INSTALL_DIR"] = Install,
            ["SKUA_BIN_DIR"] = Bin,
            ["SKUA_APPS_DIR"] = Apps,
        };
        if (noTui)
            environment["SKUA_NO_TUI"] = "1";
        return Run(Path.Combine(Checkout, "install-macos.sh"), arguments, environment);
    }

    /// <summary>Runs a program to its end, with only <paramref name="environment"/> as its environment when given; returns its exit code
    /// and its output and errors.</summary>
    private static (int Exit, string Output) Run(string program, string[] arguments, Dictionary<string, string>? environment = null)
    {
        ProcessStartInfo start = new(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            start.Environment.Clear();
            foreach ((string key, string value) in environment)
                start.Environment[key] = value;
        }
        using Process process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + errors.Result);
    }
}
