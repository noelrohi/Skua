using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace Skua.Avalonia;

/// <summary>
/// A release of Skua.app updates itself from the fork's GitHub Releases through Sparkle (#148), which it embeds: once a day, asking before
/// installing, and on Check for Updates… in the app menu. A dev build's Info.plist names no feed, so it never starts Sparkle.
/// </summary>
/// <remarks>
/// Sparkle replaces the bundle in place, and .NET loads assemblies from it long after launch, so an update waits until nothing else runs
/// from the bundle: another Skua app, the Skua Manager, or an Engine or MCP server the CLI inside it started.
/// </remarks>
public sealed unsafe partial class AppUpdates
{
    public const string CheckHeader = "Check for Updates…";

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string DelegateClassName = "SkuaUpdaterDelegate";

    /// <summary>The bundle Sparkle updates; read by its delegate, which runs on the main thread.</summary>
    private static string? s_bundle;

    private readonly IntPtr _controller;

    private AppUpdates(IntPtr controller) => _controller = controller;

    /// <summary>What a release's Info.plist says about its updates.</summary>
    public sealed record Release(string Bundle, string Version, string Build, string Feed, string PublicKey);

    /// <summary>The release the bundle at <paramref name="bundle"/> is, or null for a dev build or no bundle.</summary>
    public static Release? ReleaseOf(string? bundle)
    {
        if (bundle is null || Path.Combine(bundle, "Contents", "Info.plist") is not { } plist || !File.Exists(plist))
            return null;
        Dictionary<string, string> values = [];
        foreach (XElement name in XDocument.Load(plist).Root?.Element("dict")?.Elements("key") ?? [])
        {
            if (name.ElementsAfterSelf().FirstOrDefault() is { Name.LocalName: "string" } value)
                values[name.Value] = value.Value;
        }
        return values.GetValueOrDefault("SUFeedURL") is { Length: > 0 } feed && values.GetValueOrDefault("SUPublicEDKey") is { Length: > 0 } publicKey
            ? new Release(bundle, values.GetValueOrDefault("CFBundleShortVersionString") ?? "", values.GetValueOrDefault("CFBundleVersion") ?? "", feed, publicKey)
            : null;
    }

    /// <summary>The <c>.app</c> this process runs from, e.g. <c>…/Skua.app</c> for <c>…/Skua.app/Contents/MacOS/Skua</c>; null outside one.</summary>
    public static string? CurrentBundle()
    {
        string? macOS = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string? bundle = Path.GetDirectoryName(macOS);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal) && Path.GetFileName(macOS) == "Contents" ? bundle : null;
    }

    /// <summary>The release this process is, or null for a dev build.</summary>
    public static Release? Current() => ReleaseOf(CurrentBundle());

    /// <summary>
    /// Starts Sparkle for <paramref name="release"/>, the bundle this process runs from, on the main thread: it checks once a day and shows
    /// its own windows. Null, and logged, when Sparkle can't load.
    /// </summary>
    public static AppUpdates? Start(Release release)
    {
        try
        {
            NativeLibrary.Load(Path.Combine(release.Bundle, "Contents", "Frameworks", "Sparkle.framework", "Sparkle"));
            IntPtr type = objc_getClass("SPUStandardUpdaterController");
            if (type == IntPtr.Zero)
                throw new DllNotFoundException("Sparkle.framework has no SPUStandardUpdaterController.");
            s_bundle = release.Bundle;
            // Sparkle holds its delegate weakly; this one lives as long as the process.
            IntPtr updaterDelegate = objc_msgSend(objc_msgSend(DelegateClass(), Selector("alloc")), Selector("init"));
            IntPtr controller = objc_msgSend_start(objc_msgSend(type, Selector("alloc")), Selector("initWithStartingUpdater:updaterDelegate:userDriverDelegate:"), 1, updaterDelegate, IntPtr.Zero);
            return new AppUpdates(controller);
        }
        catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"Updates are off: Sparkle didn't load: {e.Message}");
            return null;
        }
    }

    /// <summary>Check for Updates…: Sparkle's own window says what it finds.</summary>
    public void Check() => objc_msgSend_id(_controller, Selector("checkForUpdates:"), IntPtr.Zero);

    /// <summary>Why an update can't proceed now, or null when nothing else runs from <paramref name="bundle"/>.</summary>
    public static string? WhyNotNow(string bundle)
    {
        List<string> others = OthersRunningFrom(bundle, Environment.ProcessId, Processes());
        return others.Count == 0
            ? null
            : $"Quit the other Skua apps, the Skua Manager and any skua Engine or MCP server first, then choose {CheckHeader} again. Still running: {string.Join(", ", others.Distinct())}.";
    }

    /// <summary>The names of the processes running from <paramref name="bundle"/>, except <paramref name="self"/> and the processes it started.</summary>
    public static List<string> OthersRunningFrom(string bundle, int self, IEnumerable<(int Pid, int ParentPid, string Path)> processes)
    {
        List<(int Pid, int ParentPid, string Path)> all = [.. processes];
        HashSet<int> ours = [self];
        // Children are listed after their parents only by chance, so this repeats until nothing joins.
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach ((int pid, int parent, _) in all)
                grew |= ours.Contains(parent) && ours.Add(pid);
        }
        string prefix = Path.TrimEndingDirectorySeparator(bundle) + "/";
        return [.. all.Where(p => !ours.Contains(p.Pid) && p.Path.StartsWith(prefix, StringComparison.Ordinal)).Select(p => Path.GetFileName(p.Path))];
    }

    private static IEnumerable<(int Pid, int ParentPid, string Path)> Processes()
    {
        ProcessStartInfo start = new("/bin/ps") { ArgumentList = { "-axo", "pid=,ppid=,comm=" }, RedirectStandardOutput = true, UseShellExecute = false };
        using Process ps = Process.Start(start)!;
        string output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        foreach (string line in output.Split('\n'))
        {
            string[] fields = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && int.TryParse(fields[0], out int pid) && int.TryParse(fields[1], out int parent))
                yield return (pid, parent, fields[2].Trim());
        }
    }

    /// <summary>An NSObject subclass with the one <c>SPUUpdaterDelegate</c> method Skua needs; made once per process.</summary>
    private static IntPtr DelegateClass()
    {
        IntPtr cls = objc_getClass(DelegateClassName);
        if (cls != IntPtr.Zero)
            return cls;
        cls = objc_allocateClassPair(objc_getClass("NSObject"), DelegateClassName, 0);
        class_addMethod(cls, Selector("updater:shouldProceedWithUpdate:updateCheck:error:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, nint, IntPtr*, byte>)&ShouldProceed, "B@:@@q^@");
        objc_registerClassPair(cls);
        return cls;
    }

    /// <summary>
    /// <c>-updater:shouldProceedWithUpdate:updateCheck:error:</c>: no while anything else runs from the bundle. Sparkle shows the error for
    /// Check for Updates…; a daily check says nothing and finds the update again the next day.
    /// </summary>
    [UnmanagedCallersOnly]
    private static byte ShouldProceed(IntPtr self, IntPtr selector, IntPtr updater, IntPtr item, nint updateCheck, IntPtr* error)
    {
        try
        {
            if (s_bundle is null || WhyNotNow(s_bundle) is not { } why)
                return 1;
            if (error != null)
            {
                IntPtr info = objc_msgSend_id2(objc_getClass("NSDictionary"), Selector("dictionaryWithObject:forKey:"), NSString(why), NSString("NSLocalizedDescription"));
                *error = objc_msgSend_error(objc_getClass("NSError"), Selector("errorWithDomain:code:userInfo:"), NSString("io.github.noelrohi.skua"), 1, info);
            }
            return 0;
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Couldn't tell whether an update can proceed: {e}");
            return 1;
        }
    }

    private static IntPtr NSString(string value) => objc_msgSend_string(objc_getClass("NSString"), Selector("stringWithUTF8String:"), value);

    private static IntPtr Selector(string name) => sel_registerName(name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(ObjC)]
    private static partial void objc_registerClassPair(IntPtr cls);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string types);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_id(IntPtr receiver, IntPtr selector, IntPtr a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_id2(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_msgSend_string(IntPtr receiver, IntPtr selector, string value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_start(IntPtr receiver, IntPtr selector, byte startingUpdater, IntPtr updaterDelegate, IntPtr userDriverDelegate);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr objc_msgSend_error(IntPtr receiver, IntPtr selector, IntPtr domain, nint code, IntPtr userInfo);
}
