using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Skua.Avalonia;
using Skua.Engine.Tests;

namespace Skua.Avalonia.Tests;

/// <summary>
/// A release's updates (#148), up to what Sparkle does once it shows its window: the Info.plist the build writes, the version a tag gives,
/// the appcast and signature the release workflow makes (Skua.App.Mac/Updates/release.sh), and Sparkle itself, at its pinned version,
/// finding an update in that appcast for that Info.plist. Downloading, installing and relaunching are the live check's.
/// </summary>
/// <remarks>
/// Each test makes its own folder and throwaway EdDSA key, and deletes them, and the probe's defaults domain, in a finally. Sparkle is
/// fetched once into its cache, as the build fetches it.
/// </remarks>
public sealed class AppUpdatesTests : IDisposable
{
    private static readonly string Repo = typeof(AppUpdatesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SkuaRepo").Value!;
    private static readonly string ReleaseScript = Path.Combine(Repo, "Skua.App.Mac", "Updates", "release.sh");

    private readonly string _dir = Directory.CreateTempSubdirectory("skua-updates-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_dev_builds_Info_plist_names_no_feed_and_a_releases_carries_the_tags_version_the_run_number_and_the_update_settings()
    {
        string dev = Plist("dev");
        Assert.Equal(0, MsBuildInfoPlist(dev).Exit);
        Assert.Null(AppUpdates.ReleaseOf(BundleWith(dev, "Dev.app")));
        Assert.Equal((Props("Version"), Props("Version")), (PlistValue(dev, "CFBundleShortVersionString"), PlistValue(dev, "CFBundleVersion")));
        Assert.Null(PlistValue(dev, "SUFeedURL"));

        string publicKey = PublicKey(NewPrivateKey());
        string release = Plist("release");
        (int exit, string output) = MsBuildInfoPlist(release, "-p:SkuaReleaseVersion=1.2.3", "-p:SkuaBuildNumber=42", $"-p:SkuaUpdatePublicKey={publicKey}");
        Assert.True(exit == 0, output);
        Assert.Equal("", Run("/usr/bin/plutil", ["-lint", "-s", release]).Output);
        AppUpdates.Release? read = AppUpdates.ReleaseOf(BundleWith(release, "Release.app"));
        Assert.Equal(("1.2.3", "42", "https://github.com/noelrohi/Skua/releases/latest/download/appcast.xml", publicKey),
            (read?.Version, read?.Build, read?.Feed, read?.PublicKey));
        // Checks once a day, on its own, and asks before installing.
        Assert.Equal(("true", "86400", "false"), (PlistValue(release, "SUEnableAutomaticChecks"), PlistValue(release, "SUScheduledCheckInterval"), PlistValue(release, "SUAutomaticallyUpdate")));
    }

    [Fact]
    public void A_release_build_without_its_public_key_or_run_number_or_with_another_version_fails_saying_why()
    {
        string key = PublicKey(NewPrivateKey());
        Assert.Contains("needs -p:SkuaUpdatePublicKey", MsBuildInfoPlist(Plist("a"), "-p:SkuaReleaseVersion=1.2.3", "-p:SkuaBuildNumber=42").Output);
        Assert.Contains("needs -p:SkuaBuildNumber", MsBuildInfoPlist(Plist("b"), "-p:SkuaReleaseVersion=1.2.3", $"-p:SkuaUpdatePublicKey={key}").Output);
        Assert.Contains("must be X.Y.Z", MsBuildInfoPlist(Plist("c"), "-p:SkuaReleaseVersion=1.2", "-p:SkuaBuildNumber=42", $"-p:SkuaUpdatePublicKey={key}").Output);
        Assert.Contains("are for a release", MsBuildInfoPlist(Plist("d"), $"-p:SkuaUpdatePublicKey={key}").Output);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.plist"));
    }

    [Fact]
    public void A_release_version_comes_from_a_vX_Y_Z_tag_only()
    {
        Assert.Equal((0, "1.0.0"), Release("version", "v1.0.0"));
        Assert.Equal((0, "12.30.4"), Release("version", "v12.30.4"));
        foreach (string tag in new[] { "1.0.0", "v1.0", "v1.0.0-beta", "v01.0.0", "v1.0.0.0", "release" })
        {
            (int exit, string output) = Release("version", tag);
            Assert.Equal(1, exit);
            Assert.Contains("tags are vX.Y.Z", output);
        }
    }

    [Fact]
    public void The_appcast_carries_the_version_the_build_the_signature_and_the_newest_change_log_section_as_HTML()
    {
        string changelog = Path.Combine(_dir, "changelogs-mac.md");
        File.WriteAllText(changelog, """
            # Skua for Mac

            ---

            ## October 2, 2026

            ### New
            * Skua updates itself: **Check for Updates…** in the `Skua` menu. See [Install](./BUILD.md).
            * A <b>bold</b> claim & more.

            ---

            ## October 1, 2026

            ### Fixes
            * Older.
            """);
        (int exit, string notes) = Release("notes", changelog);
        Assert.Equal(0, exit);
        Assert.StartsWith("## October 2, 2026", notes);
        Assert.DoesNotContain("Older", notes);
        File.WriteAllText(Path.Combine(_dir, "notes.md"), notes);
        string html = Run("/bin/bash", ["-c", $"\"$0\" html < \"$1\"", ReleaseScript, Path.Combine(_dir, "notes.md")]).Output;
        Assert.Equal("""
            <h3>October 2, 2026</h3>
            <h4>New</h4>
            <ul>
            <li>Skua updates itself: <b>Check for Updates…</b> in the <code>Skua</code> menu. See <a href="./BUILD.md">Install</a>.</li>
            <li>A &lt;b&gt;bold&lt;/b&gt; claim &amp; more.</li>
            </ul>
            """, html.TrimEnd().ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(_dir, "notes.html"), html);

        const string signed = "sparkle:edSignature=\"c2lnbmF0dXJl+/A==\" length=\"1234\"";
        (exit, string xml) = Release("appcast", "1.2.3", "42", "https://github.com/noelrohi/Skua/releases/download/v1.2.3/Skua-1.2.3.zip", signed, Path.Combine(_dir, "notes.html"));
        Assert.True(exit == 0, xml);
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        XElement item = XDocument.Parse(xml).Root!.Element("channel")!.Element("item")!;
        Assert.Equal(("42", "1.2.3"), (item.Element(sparkle + "version")?.Value, item.Element(sparkle + "shortVersionString")?.Value));
        XElement enclosure = item.Element("enclosure")!;
        Assert.Equal(("https://github.com/noelrohi/Skua/releases/download/v1.2.3/Skua-1.2.3.zip", "c2lnbmF0dXJl+/A==", "1234"),
            (enclosure.Attribute("url")?.Value, enclosure.Attribute(sparkle + "edSignature")?.Value, enclosure.Attribute("length")?.Value));
        Assert.Equal(html.TrimEnd(), item.Element("description")?.Value);
        Assert.True(DateTimeOffset.TryParse(item.Element("pubDate")?.Value, out _));

        Assert.Contains("isn't sign_update's output", Release("appcast", "1.2.3", "42", "https://x/a.zip", "Error: no key", Path.Combine(_dir, "notes.html")).Output);
        Assert.Contains("isn't a positive whole number", Release("appcast", "1.2.3", "0", "https://x/a.zip", signed, Path.Combine(_dir, "notes.html")).Output);
    }

    [Fact]
    public void A_zip_sign_update_signs_verifies_against_its_public_key_and_a_tampered_zip_or_another_key_fails()
    {
        string sparkle = Sparkle();
        string key = NewPrivateKey();
        string zip = Path.Combine(_dir, "Skua.zip");
        File.WriteAllBytes(zip, RandomNumberGenerator.GetBytes(4096));
        string signature = SignUpdate(sparkle, key, zip);
        string publicKey = PublicKey(key);

        Assert.Equal((0, ""), Release("verify", publicKey, zip, signature));
        Assert.Equal(0, Run(Path.Combine(sparkle, "bin", "sign_update"), ["--verify", "--ed-key-file", key, zip, signature]).Exit);

        // A public key that isn't the private key's: the check the workflow makes before it builds.
        (int exit, string output) = Release("verify", PublicKey(NewPrivateKey()), zip, signature);
        Assert.Equal(1, exit);
        Assert.Contains("isn't signed by the key", output);

        using (FileStream stream = File.Open(zip, FileMode.Open))
        {
            stream.Position = 100;
            stream.WriteByte((byte)~stream.ReadByte());
        }
        Assert.Equal(1, Release("verify", publicKey, zip, signature).Exit);
        Assert.NotEqual(0, Run(Path.Combine(sparkle, "bin", "sign_update"), ["--verify", "--ed-key-file", key, zip, signature]).Exit);

        File.WriteAllText(Path.Combine(_dir, "not-a-key"), "c2hvcnQ=");
        Assert.Contains("isn't a 32-byte Ed25519 seed", Run("/bin/bash", ["-c", "\"$0\" public-key < \"$1\"", ReleaseScript, Path.Combine(_dir, "not-a-key")]).Output);
    }

    [Fact]
    public async Task Sparkle_finds_an_update_in_the_releases_appcast_only_for_a_higher_build_number()
    {
        string sparkle = Sparkle();
        string probe = Path.Combine(_dir, "sparkle-probe");
        (int exit, string output) = Run("/usr/bin/clang", ["-fobjc-arc", "-F", sparkle, "-framework", "Sparkle", "-framework", "Foundation",
            $"-Wl,-rpath,{sparkle}", Path.Combine(Repo, "Skua.Avalonia.Tests", "Updates", "sparkle-probe.m"), "-o", probe]);
        Assert.True(exit == 0, output);

        string key = NewPrivateKey();
        string zip = Path.Combine(_dir, "Skua-1.1.0.zip");
        File.WriteAllBytes(zip, RandomNumberGenerator.GetBytes(1024));
        File.WriteAllText(Path.Combine(_dir, "notes.html"), "<p>New.</p>");
        (exit, string appcast) = Release("appcast", "1.1.0", "10", "https://example.invalid/Skua-1.1.0.zip",
            Run(Path.Combine(sparkle, "bin", "sign_update"), ["--ed-key-file", key, zip]).Output.Trim(), Path.Combine(_dir, "notes.html"));
        Assert.True(exit == 0, appcast);

        (HttpListener listener, string baseUrl) = LoopbackHttp.Start();
        string bundleId = "io.github.noelrohi.skua.tests." + Guid.NewGuid().ToString("N")[..8];
        using CancellationTokenSource stop = new();
        Task serving = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context = await listener.GetContextAsync();
                byte[] body = System.Text.Encoding.UTF8.GetBytes(appcast);
                context.Response.ContentType = "application/xml";
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        });
        try
        {
            // The installed release's build against the appcast's 10: numbers, not text, so 9 is older.
            Assert.Equal("found 10 1.1.0", Probe(probe, bundleId, "1.0.0", "9", PublicKey(key), baseUrl));
            Assert.Equal("none", Probe(probe, bundleId, "1.1.0", "10", PublicKey(key), baseUrl));
            Assert.Equal("none", Probe(probe, bundleId, "1.2.0", "11", PublicKey(key), baseUrl));
        }
        finally
        {
            stop.Cancel();
            listener.Close();
            await Task.WhenAny(serving);
            // defaults delete empties the domain and leaves its file.
            Run("/usr/bin/defaults", ["delete", bundleId]);
            string library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library");
            File.Delete(Path.Combine(library, "Preferences", bundleId + ".plist"));
            if (Directory.Exists(Path.Combine(library, "Caches", bundleId)))
                Directory.Delete(Path.Combine(library, "Caches", bundleId), recursive: true);
        }
    }

    [Fact]
    public void Only_a_process_running_from_the_bundle_that_isnt_the_app_or_one_it_started_holds_an_update()
    {
        const string bundle = "/Applications/Skua.app";
        (int, int, string)[] processes =
        [
            (100, 1, "/Applications/Skua.app/Contents/MacOS/Skua"),
            (101, 100, "/Applications/Skua.app/Contents/MacOS/skua-gamehost"),
            (102, 101, "/usr/bin/some-grandchild"),
            (200, 1, "/Applications/Skua.app.old/Contents/MacOS/Skua"),
            (300, 1, "/Users/me/.local/share/skua/versions/1.4.4.4+abc/Skua.app/Contents/MacOS/Skua"),
            (400, 1, "/bin/zsh"),
        ];
        Assert.Empty(AppUpdates.OthersRunningFrom(bundle, 100, processes));
        Assert.Empty(AppUpdates.OthersRunningFrom(bundle + "/", 100, processes));

        (int, int, string)[] more = [.. processes, (500, 1, "/Applications/Skua.app/Contents/MacOS/Skua"), (501, 400, "/Applications/Skua.app/Contents/Helpers/skua-engine")];
        Assert.Equal(["Skua", "skua-engine"], AppUpdates.OthersRunningFrom(bundle, 100, more));
        // Seen from another app: this one and its Game Host hold the update too.
        Assert.Equal(["Skua", "skua-gamehost", "skua-engine"], AppUpdates.OthersRunningFrom(bundle, 500, more));
        Assert.Null(AppUpdates.WhyNotNow(_dir));
    }

    /// <summary>Runs the probe against a bundle whose Info.plist the build wrote for a release of <paramref name="version"/> (<paramref name="build"/>).</summary>
    private string Probe(string probe, string bundleId, string version, string build, string publicKey, string feed)
    {
        string bundle = Path.Combine(_dir, $"Skua-{build}.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        string plist = Path.Combine(bundle, "Contents", "Info.plist");
        (int exit, string output) = MsBuildInfoPlist(plist, $"-p:SkuaReleaseVersion={version}", $"-p:SkuaBuildNumber={build}", $"-p:SkuaUpdatePublicKey={publicKey}");
        Assert.True(exit == 0, output);
        // Sparkle keeps its state in the host's defaults: never the real app's.
        Assert.Equal(0, Run("/usr/bin/plutil", ["-replace", "CFBundleIdentifier", "-string", bundleId, plist]).Exit);
        // Its one line, among whatever Sparkle logs.
        string lines = Run(probe, [bundle, feed + "appcast.xml"]).Output;
        return lines.Split('\n').LastOrDefault(l => l is "none" || l.StartsWith("found ", StringComparison.Ordinal) || l.StartsWith("error ", StringComparison.Ordinal)) ?? lines;
    }

    private string Plist(string name) => Path.Combine(_dir, name + ".plist");

    /// <summary>A bundle folder holding <paramref name="plist"/> as its Info.plist.</summary>
    private string BundleWith(string plist, string name)
    {
        string bundle = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
        File.Copy(plist, Path.Combine(bundle, "Contents", "Info.plist"));
        return bundle;
    }

    /// <summary>A throwaway EdDSA private key in Sparkle's format, the base64 of a 32-byte seed, in a file of its own.</summary>
    private string NewPrivateKey()
    {
        string file = Path.Combine(_dir, "key-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(file, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return file;
    }

    private static string PublicKey(string privateKeyFile)
    {
        (int exit, string output) = Run("/bin/bash", ["-c", "\"$0\" public-key < \"$1\"", ReleaseScript, privateKeyFile]);
        Assert.True(exit == 0, output);
        return output.Trim();
    }

    private static string SignUpdate(string sparkle, string privateKeyFile, string file)
    {
        (int exit, string output) = Run(Path.Combine(sparkle, "bin", "sign_update"), ["-p", "--ed-key-file", privateKeyFile, file]);
        Assert.True(exit == 0, output);
        return output.Trim();
    }

    private static string Sparkle()
    {
        (int exit, string output) = Run(Path.Combine(Repo, "Skua.App.Mac", "Updates", "sparkle.sh"), []);
        Assert.True(exit == 0, output);
        return output.Trim();
    }

    private static (int Exit, string Output) Release(params string[] arguments)
    {
        (int exit, string output) = Run(ReleaseScript, arguments);
        return (exit, output.TrimEnd());
    }

    /// <summary>Runs the build's <c>SkuaInfoPlist</c> target alone, as a publish of Skua.app does, writing <paramref name="plist"/>.</summary>
    private static (int Exit, string Output) MsBuildInfoPlist(string plist, params string[] properties) =>
        Run(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet",
            ["msbuild", Path.Combine(Repo, "Skua.App.Mac", "Skua.App.Mac.csproj"), "-t:SkuaInfoPlist", $"-p:SkuaInfoPlistFile={plist}",
             "-nologo", "-v:q", "-nodeReuse:false", .. properties]);

    private static string Props(string property) =>
        XDocument.Load(Path.Combine(Repo, "Directory.Build.props")).Descendants(property).Single().Value;

    private static string? PlistValue(string plist, string key)
    {
        XElement? name = XDocument.Load(plist).Root!.Element("dict")!.Elements("key").FirstOrDefault(k => k.Value == key);
        return name?.ElementsAfterSelf().First() is { } value ? value.Name.LocalName is "true" or "false" ? value.Name.LocalName : value.Value : null;
    }

    /// <summary>Runs a program to its end; returns its exit code and its output and errors together.</summary>
    private static (int Exit, string Output) Run(string program, string[] arguments)
    {
        ProcessStartInfo start = new(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        // The test host's MSBuild settings would point a nested build at its own SDK resolution.
        foreach (string variable in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(variable);
        using Process process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + errors.Result);
    }
}
