// PROTOTYPE (#6): headless Engine host (Skua.Core on net10.0) driving the Rust Game Host.
//
//   bridge-console smoke                         load to the login screen, no login
//   bridge-console live [--server NAME]          login, maps, latency benchmark, event ordering
//   bridge-console script <file.cs> <minutes>    login, run a Script, sample Game Host RSS every minute
//   bridge-console idle <minutes>                login, stay in battleon, sample RSS (hidden-running check)
//
// Env: SKUA_GAMEHOST (binary), SKUA_SWF (skua.swf), SKUA_OUT (output dir), SKUA_SHOW_GAME=1.
// The Test Account password is read from Keychain and never printed; all output is redacted.
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Skua.Core.AppStartup;
using Skua.Core.Interfaces;
using Skua.Core.Messaging;
using Skua.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BridgeConsole;

public static class Program
{
    static string OutDir = "";
    static StreamWriter? LogFile;
    static string? Secret;
    static readonly object LogLock = new();
    static IScriptInterface Bot = null!;
    static MacFlashUtil Flash = null!;
    static GameHost Host => Flash.Host!;

    public static void Log(string s)
    {
        if (Secret != null && Secret.Length > 0) s = s.Replace(Secret, "***");
        s = Regex.Replace(s, @"<pword>.*?</pword>", "<pword>***</pword>"); // game's own login-token trace
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            LogFile?.WriteLine(line);
            LogFile?.Flush();
        }
    }

    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "smoke";
        OutDir = Environment.GetEnvironmentVariable("SKUA_OUT") ?? Path.Combine(Environment.CurrentDirectory, "out");
        Directory.CreateDirectory(OutDir);
        LogFile = new StreamWriter(Path.Combine(OutDir, $"{mode}-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
        MacFlashUtil.GameHostExe = Environment.GetEnvironmentVariable("SKUA_GAMEHOST")!;
        MacFlashUtil.SwfPath = Environment.GetEnvironmentVariable("SKUA_SWF")!;
        MacFlashUtil.ShowGame = Environment.GetEnvironmentVariable("SKUA_SHOW_GAME") == "1";

        StartEngine();
        if (mode == "compile")
        {
            var mgr = Ioc.Default.GetRequiredService<IScriptManager>();
            foreach (var f in args.Skip(1))
            {
                var sw = Stopwatch.StartNew();
                try { var o = mgr.Compile(File.ReadAllText(f)); Log($"compile {f}: {(o != null ? "OK" : "null")} in {sw.ElapsedMilliseconds} ms"); }
                catch (Exception e) { Log($"compile {f}: FAILED {e.Message[..Math.Min(e.Message.Length, 2000)]}"); }
            }
            Flash.Dispose();
            return 0;
        }
        if (!WaitLoaded()) return 2;
        switch (mode)
        {
            case "smoke": Smoke(); break;
            case "loginscreen": LoginScreen(double.Parse(args[1])); break;
            case "hold": Log("holding"); Thread.Sleep(Timeout.Infinite); break;
            case "live": Live(Arg(args, "--server")); break;
            case "full": Live(Arg(args, "--server")); RunScript(Path.GetFullPath(args[1]), double.Parse(args[2])); break;
            case "script": Login(Arg(args, "--server")); RunScript(args[1], double.Parse(args[2])); break;
            case "idle": Login(Arg(args, "--server")); Idle(double.Parse(args[1])); break;
        }
        Log("done; closing Game Host");
        Flash.Dispose();
        return 0;
    }

    static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    // ------------------------------------------------------------------ Engine composition

    static readonly ManualResetEventSlim Loaded = new();
    static readonly List<(long ticks, string name, string arg)> EventLog = new();
    static readonly ConcurrentQueue<string> FlashLines = new();
    static int MaxQueue;

    static void StartEngine()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISettingsService, HeadlessSettings>();
        services.AddSingleton<IFlashUtil, MacFlashUtil>();
        services.AddSingleton<IDispatcherService, HeadlessDispatcher>();
        services.AddSingleton<IDialogService, HeadlessDialogs>();
        services.AddSingleton(_ => Noop<IClipboardService>.Create());
        services.AddSingleton(_ => Noop<IWindowService>.Create());
        services.AddSingleton(_ => Noop<IFileDialogService>.Create());
        services.AddSingleton(_ => Noop<IHotKeyService>.Create());
        services.AddSingleton(_ => Noop<IThemeService>.Create());
        services.AddSingleton(_ => Noop<ISoundService>.Create());
        services.AddCommonServices();
        services.AddScriptableObjects();
        services.AddCompiler();
        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        HeadlessDialogs.Log = Log;
        provider.GetRequiredService<IClientFilesService>().CreateDirectories();
        provider.GetRequiredService<IClientFilesService>().CreateFiles();
        Bot = provider.GetRequiredService<IScriptInterface>();
        var logs = provider.GetRequiredService<ILogService>();
        StrongReferenceMessenger.Default.Register<object, FlashErrorMessage>(new object(), (_, m) => Log($"[bridge.error] {m.Function}: {m.Exception.Message}"));

        Flash = (MacFlashUtil)provider.GetRequiredService<IFlashUtil>();
        Flash.FlashCall += (name, a) =>
        {
            string arg = a is { Length: > 0 } ? a[0]?.ToString() ?? "" : "";
            lock (EventLog) EventLog.Add((Stopwatch.GetTimestamp(), name, arg));
            switch (name)
            {
                case "requestLoadGame": Flash.Call("loadClient"); break; // SkuaStartupHandler.LoadGame
                case "loaded": Loaded.Set(); break;
                case "debug": Log($"[as3 debug] {arg}"); break;
            }
        };
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));
        Flash.InitializeFlash();
        Host.FlashLog += l => { FlashLines.Enqueue(l); Log("[flash] " + l); };
        Host.DebugLog += l => Log(l);
        Log($"Game Host pid {Host.Pid}");
    }

    static bool WaitLoaded()
    {
        var sw = Stopwatch.StartNew();
        if (!Loaded.Wait(TimeSpan.FromSeconds(60)))
        {
            Log("FAIL: 'loaded' never arrived");
            return false;
        }
        Log($"'loaded' after {sw.ElapsedMilliseconds} ms; callbacks registered: {Host.Callbacks.Count}");
        Thread.Sleep(1500);
        return true;
    }

    // ------------------------------------------------------------------ checks

    static void Smoke()
    {
        CheckCallbacks();
        Shot("login-screen");
        Bench("idle-prelogin", () => Bot.Flash.GetGameObject("mcLogin.currentLabel"), 2000);
        Log("stats " + Host.Stats());
        MemSample("smoke", Stopwatch.StartNew());
    }

    // #13: no login; sit at the login screen and sample memory every 30 s.
    static void LoginScreen(double minutes)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMinutes < minutes) { MemSample("loginscreen", sw); Thread.Sleep(30000); }
        MemSample("loginscreen-after-full-gc", sw, gc: true);
    }

    static void CheckCallbacks()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Externalizer.as.txt"));
        var expected = Regex.Matches(src, @"addCallback\(\s*""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        List<string> got;
        lock (Host.Callbacks) got = Host.Callbacks.ToList();
        var missing = expected.Except(got).ToList();
        Log($"callbacks: expected {expected.Count} (Externalizer.as), registered {got.Count}, missing [{string.Join(",", missing)}]");
        // Reachability: call every callback once with no args where safe; count bridge errors (none should throw on transport).
        int ok = 0, err = 0;
        var unsafeToCall = new HashSet<string> { "loadClient", "injectScript", "sendClientPacket", "catchPackets", "callGameFunction", "callGameFunction0", "setGameObject", "setGameObjectKey", "fcCallGameFunction" };
        foreach (var cb in got)
        {
            if (unsafeToCall.Contains(cb)) continue;
            try { Host.Call($"<invoke name=\"{cb}\" returntype=\"xml\"></invoke>"); ok++; }
            catch (Exception e) { err++; Log($"callback {cb}: {e.Message}"); }
        }
        Log($"callbacks called with no args: {ok} replied, {err} transport errors, {unsafeToCall.Count} skipped as state-changing");
    }

    static void Shot(string name, int maxWidth = 0)
    {
        var sw = Stopwatch.StartNew();
        var s = Host.Screenshot(maxWidth);
        if (s.Png.Length == 0) { Log($"screenshot {name}: FAILED (Game Host returned no image)"); return; }
        var path = Path.Combine(OutDir, $"{name}.png");
        File.WriteAllBytes(path, s.Png);
        Log($"screenshot {name}: {s.Width}x{s.Height} frames~{s.Frames} {s.Png.Length} B in {sw.ElapsedMilliseconds} ms -> {path}");
    }

    static void Bench(string label, Func<string?> getter, int n)
    {
        getter();
        var t = new double[n];
        string? last = null;
        for (int i = 0; i < n; i++)
        {
            long a = Stopwatch.GetTimestamp();
            last = getter();
            t[i] = Stopwatch.GetElapsedTime(a).TotalMilliseconds;
        }
        Array.Sort(t);
        Log($"BENCH {label}: n={n} p50={t[n / 2]:F3} p90={t[(int)(n * .9)]:F3} p99={t[(int)(n * .99)]:F3} max={t[n - 1]:F3} ms (last value '{last}')");
    }

    // ------------------------------------------------------------------ login and maps

    static (string user, string pass) ReadKeychain()
    {
        string Run(params string[] a)
        {
            var psi = new ProcessStartInfo("security") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var x in a) psi.ArgumentList.Add(x);
            // HOME may point at a scratch SkuaDIR; name the real login keychain explicitly.
            psi.ArgumentList.Add(Path.Combine(Environment.GetEnvironmentVariable("REAL_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Keychains/login.keychain-db"));
            var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o;
        }
        string attrs = Run("find-generic-password", "-s", "skua-test-account");
        string user = Regex.Match(attrs, "\"acct\"<blob>=\"([^\"]*)\"").Groups[1].Value;
        string pass = Run("find-generic-password", "-s", "skua-test-account", "-w").TrimEnd('\n');
        Secret = pass;
        return (user, pass);
    }

    static void Login(string? server)
    {
        WatchConnDetail(CancellationToken.None);
        var (user, pass) = ReadKeychain();
        if (user.Length == 0 || pass.Length == 0) throw new Exception("Test Account not found in Keychain");
        Log($"login as {user} (password from Keychain, redacted) server={server ?? "(auto)"}");
        Bot.Servers.SetLoginInfo(user, pass);
        var sw = Stopwatch.StartNew();
        bool relog = server != null ? Bot.Servers.Relogin(server) : Bot.Servers.Relogin();
        Log($"Core Relogin returned {relog} after {sw.ElapsedMilliseconds} ms (its own wait is 30 x 100 ms); waiting up to 90 s for Playing && IsWorldLoaded");
        bool ok = SpinWait.SpinUntil(() => { Thread.Sleep(250); return Bot.Player.Playing && Bot.Flash.IsWorldLoaded; }, TimeSpan.FromSeconds(90));
        Log($"login {(ok ? "OK" : "FAILED")} in {sw.ElapsedMilliseconds} ms: Playing={Bot.Player.Playing} IsWorldLoaded={Bot.Flash.IsWorldLoaded} map={Bot.Map.Name} server={Bot.Servers.LastIP}");
        Log("stats after login (before any render) " + Host.Stats());
        MemSample("after-login-before-render", Stopwatch.StartNew());
        Log("RENDERBENCH after-login " + Host.RenderBench(1));
        MemSample("after-login-after-render", Stopwatch.StartNew());
        Shot("after-login");
        if (!ok) throw new Exception("login failed");
    }

    static bool Join(string map, string cell = "Enter", string pad = "Spawn")
    {
        var sw = Stopwatch.StartNew();
        Bot.Map.Join(map, cell, pad);
        Bot.Wait.ForMapLoad(map, 30);
        bool ok = SpinWait.SpinUntil(() => { Thread.Sleep(250); return string.Equals(Bot.Map.Name, map, StringComparison.OrdinalIgnoreCase) && Bot.Player.Playing && Bot.Flash.IsWorldLoaded; }, TimeSpan.FromSeconds(60));
        Thread.Sleep(1500);
        Log($"join {map}: {(ok ? "OK" : "FAILED")} in {sw.ElapsedMilliseconds} ms; map={Bot.Map.Name} cell={Bot.Player.Cell} players={Bot.Map.PlayerCount} IsWorldLoaded={Bot.Flash.IsWorldLoaded}");
        Log($"RENDERBENCH {map} " + Host.RenderBench(30));
        Log($"stats {map} " + Host.Stats());
        Shot("map-" + map);
        return ok;
    }

    static void WatchConnDetail(CancellationToken ct)
    {
        new Thread(() =>
        {
            string last = "";
            while (!ct.IsCancellationRequested)
            {
                string cd = Bot.Flash.IsNull("mcConnDetail.stage") ? "null" : Bot.Flash.GetGameObject("mcConnDetail.txtDetail.text", "null")!;
                if (cd != last) { Log($"[mcConnDetail] '{last}' -> '{cd}'"); last = cd; }
                Thread.Sleep(100);
            }
        }) { IsBackground = true }.Start();
    }

    static void Live(string? server)
    {
        CheckCallbacks();
        Login(server);
        Join("battleon");
        Bench("idle Player.Cell (battleon)", () => Bot.Player.Cell, 10000);
        Bench("transport ping", () => { Host.Ping(); return ""; }, 10000);
        Join("yulgar");
        Bench("idle Player.Cell (yulgar)", () => Bot.Player.Cell, 10000);
        Join("battleontown");
        Combat();
        Join("battleon");
        EventOrdering();
        Log("stats " + Host.Stats());
    }

    static void Combat()
    {
        // Go where monsters are, attack continuously on a background thread while benchmarking.
        Join("battleontown");
        var mon = Bot.Monsters.MapMonsters.FirstOrDefault();
        if (mon != null)
        {
            Bot.Map.Jump(mon.Cell, "Left");
            Thread.Sleep(1500);
        }
        Log($"combat cell {Bot.Player.Cell}, monsters here: {Bot.Monsters.CurrentMonsters.Count}");
        int kills0 = Bot.Stats.Kills;
        using var cts = new CancellationTokenSource();
        var attacker = new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    Bot.Combat.Attack("*");
                    for (int s = 1; s <= 4; s++) Bot.Skills.UseSkill(s);
                }
                catch { }
                Thread.Sleep(250);
            }
        }) { IsBackground = true };
        attacker.Start();
        Thread.Sleep(3000);
        Bench("combat Player.Cell", () => Bot.Player.Cell, 10000);
        Log($"inCombat={Bot.Player.InCombat} kills during combat: {Bot.Stats.Kills - kills0}");
        Shot("combat");
        cts.Cancel();
        attacker.Join();
    }

    static void EventOrdering()
    {
        List<(long, string, string)> ev;
        lock (EventLog) ev = EventLog.ToList();
        var counts = ev.GroupBy(e => e.Item2).ToDictionary(g => g.Key, g => g.Count());
        Log("event counts: " + string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}")));
        bool mono = ev.Zip(ev.Skip(1)).All(p => p.First.Item1 <= p.Second.Item1);
        // Server json messages appear in packetFromServer (raw debug message) and as pext (parsed). The cmd
        // sequence of pext must be a subsequence, in order, of the json cmds seen in packetFromServer.
        static string? Cmd(string json)
        {
            try { var j = JObject.Parse(json); return (string?)(j["params"]?["dataObj"]?["cmd"] ?? j["b"]?["o"]?["cmd"]); }
            catch { return null; }
        }
        var pext = ev.Where(e => e.Item2 == "pext").Select(e => Cmd(e.Item3)).Where(c => c != null).ToList();
        var raw = ev.Where(e => e.Item2 == "packetFromServer").Select(e => { var m = Regex.Match(e.Item3, "\"cmd\":\"([^\"]+)\""); return m.Success ? m.Groups[1].Value : null; }).Where(c => c != null).ToList();
        int i = 0, matched = 0;
        foreach (var c in pext)
        {
            int j = raw.IndexOf(c!, i);
            if (j >= 0) { matched++; i = j + 1; }
        }
        Log($"event ordering: dispatch timestamps monotonic={mono}; pext json cmds={pext.Count}, in-order matches in packetFromServer={matched}; max dispatch queue depth={GameHostQueueNote}");
    }

    static string GameHostQueueNote => Host.MaxQueueDepth.ToString();

    // ------------------------------------------------------------------ long runs

    static long RssKb(int pid)
    {
        var p = Process.Start(new ProcessStartInfo("ps", $"-o rss= -p {pid}") { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return long.TryParse(o, out var v) ? v : -1;
    }

    // #13: `footprint` categories of the Game Host (phys_footprint, what Activity Monitor calls Memory).
    static string Footprint(int pid)
    {
        var p = Process.Start(new ProcessStartInfo("footprint", pid.ToString()) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        static double Mb(string v) { var a = v.Split(' '); var n = double.Parse(a[0]); return a[1] switch { "GB" => n * 1000, "MB" => n, "KB" => n / 1000, _ => n / 1e6 }; }
        var tot = Regex.Match(o, @"Footprint: ([\d.]+ \w+)");
        var cats = new Dictionary<string, double>();
        foreach (Match m in Regex.Matches(o, @"^\s*([\d.]+ \w+)\s+[\d.]+ \w+\s+[\d.]+ \w+\s+\d+\s+(.+)$", RegexOptions.Multiline))
            cats[m.Groups[2].Value.Trim()] = Mb(m.Groups[1].Value);
        double C(string k) => cats.TryGetValue(k, out var v) ? v : 0;
        return $"fp={(tot.Success ? Mb(tot.Groups[1].Value) : -1):F0} gfx={C("IOAccelerator (graphics)"):F0} unmappedGfx={C("Owned physical footprint (unmapped) (graphics)"):F0} malloc={C("Malloc Small") + C("Malloc Large") + C("Malloc Tiny") + C("Malloc Nano"):F0}";
    }

    static void MemSample(string label, Stopwatch sw, bool gc = false)
    {
        if (gc) Host.FullGc();
        Log($"MEM {label} t={sw.Elapsed.TotalMinutes:F1}min {Footprint(Host.Pid)} {Host.Mem()}");
        // #13: live GC objects by Rust type after a full GC (census build of the Game Host only).
        if (gc && Environment.GetEnvironmentVariable("SKUA_CENSUS") == "1")
            foreach (var line in Host.Census().Split('\n')) Log($"CENSUS {label} t={sw.Elapsed.TotalMinutes:F1}min {line}");
    }

    static void Sample(string label, Stopwatch sw)
    {
        MemSample(label, sw);
        var st = JObject.Parse(Host.Stats());
        Log($"SAMPLE {label} t={sw.Elapsed.TotalMinutes:F1}min rss={RssKb(Host.Pid) / 1024} MB framesRun={st["framesRun"]} tickBusyMs={st["tickBusyMs"]} maxTickMs={st["maxTickMs"]} maxTickGapMs={st["maxTickGapMs"]} peakCmdBufs={st["maxOutstandingCmdBufs"]} framesEst={st["framesEst"]} ticks={st["ticks"]} calls={st["calls"]} events={st["events"]} playing={Bot.Player.Playing} map={Bot.Map.Name} kills={Bot.Stats.Kills} deaths={Bot.Stats.Deaths} drops={Bot.Stats.Drops} relogins={Bot.Stats.Relogins}");
    }

    static void RunScript(string path, double minutes)
    {
        var mgr = Ioc.Default.GetRequiredService<IScriptManager>();
        mgr.SetLoadedScript(path);
        var sw = Stopwatch.StartNew();
        Log($"starting Script {path} for {minutes} min");
        var start = mgr.StartScript().GetAwaiter().GetResult();
        if (start != null) { Log("Script failed to start: " + start.Message); return; }
        int lastMinute = -1;
        while (sw.Elapsed.TotalMinutes < minutes)
        {
            int m = (int)sw.Elapsed.TotalMinutes;
            if (m != lastMinute)
            {
                lastMinute = m; Sample("script", sw);
                if (m % 15 == 0) { Shot($"script-{m:D3}min"); if (Environment.GetEnvironmentVariable("SKUA_NO_RENDERBENCH") == null) Log($"RENDERBENCH script-{m}min " + Host.RenderBench(30)); }
                if (m % 30 == 0 && m > 0) MemSample("script-after-full-gc", sw, gc: true);
            }
            if (!mgr.ScriptRunning) { Log("Script ended on its own"); break; }
            Thread.Sleep(1000);
        }
        Sample("script-end", sw);
        MemSample("script-end-after-full-gc", sw, gc: true);
        Shot("script-end");
        mgr.StopScript().GetAwaiter().GetResult();
        Log("Script stopped");
    }

    static void Idle(double minutes)
    {
        Join("battleon");
        var sw = Stopwatch.StartNew();
        int lastMinute = -1;
        while (sw.Elapsed.TotalMinutes < minutes)
        {
            int m = (int)sw.Elapsed.TotalMinutes;
            if (m != lastMinute) { lastMinute = m; Sample("idle", sw); }
            Thread.Sleep(1000);
        }
        Sample("idle-end", sw);
        Shot("idle-end");
    }
}
