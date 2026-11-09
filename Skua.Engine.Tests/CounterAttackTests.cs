using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary>Counter attacks read from the Game Client's <c>ct</c> packets, against the fake game.</summary>
public class CounterAttackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_counter_starts_on_its_warning_and_aura_ends_when_the_aura_goes_and_a_broken_packet_stops_nothing()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);
        TestScripts.Write(sandbox, "Tests/Counter.cs", """
            using Skua.Core.Interfaces;

            public class Script
            {
                public void ScriptMain(IScriptInterface bot)
                {
                    bot.Events.CounterAttack += faded => bot.Log($"counter faded={faded}");
                    bot.Events.ExtensionPacketReceived += _ => bot.Log("pext seen");
                    bot.Log("ready");
                    while (!bot.ShouldExit)
                        bot.Sleep(50);
                }
            }
            """);
        ScriptStartResult start = await game.Connection.ScriptStartAsync("Tests/Counter.cs", cancellationToken: Ct);
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Run == start.Run && e.Text == "ready");

        // A packet Core can't read reaches the Script's handlers anyway.
        await game.GameHost.DoAsync(Pext("""{"cmd":"addItems","items":"not an item list"}"""));
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Run == start.Run && e.Text == "pext seen");

        await game.GameHost.DoAsync(Pext("""
            {"cmd":"ct","anims":[{"cInf":"m:1","msg":"The boss prepares a counter attack!","animStr":"Reflect"}],"a":[{"cmd":"aura+","cInf":"m:1","tInf":"m:1","auras":[{"nam":"Deflect","dur":5}]}]}
            """));
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Run == start.Run && e.Text == "counter faded=False");

        await game.GameHost.DoAsync(Pext("""{"cmd":"ct","a":[{"cmd":"aura--","tInf":"m:1","aura":{"nam":"Deflect"}}]}"""));
        await game.Connection.WaitForLogsAsync(LogKind.Script, 1, e => e.Run == start.Run && e.Text == "counter faded=True");

        await game.Connection.ScriptStopAsync(Ct);
    }

    private static string Pext(string data) =>
        $$$"""send E <invoke name="pext" returntype="xml"><arguments><string>{"params":{"type":"json","dataObj":{{{data.Trim()}}}}}</string></arguments></invoke>""";
}
