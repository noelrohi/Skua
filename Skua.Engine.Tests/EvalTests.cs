using System.Text.Json;
using Skua.Control;

namespace Skua.Engine.Tests;

/// <summary><c>eval</c> against the fake Game Host.</summary>
public class EvalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_expression_returns_its_value_as_JSON()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        EvalResult number = await game.Connection.EvalAsync("6 * 7", cancellationToken: Ct);
        EvalResult list = await game.Connection.EvalAsync("new List<string> { \"a\", \"b\" }", cancellationToken: Ct);
        EvalResult obj = await game.Connection.EvalAsync("new { Name = \"Artix\", Level = 100 }", cancellationToken: Ct);

        Assert.Equal(42, number.Value!.Value.GetInt32());
        Assert.Equal(["a", "b"], list.Value!.Value.EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("""{"name":"Artix","level":100}""", obj.Value!.Value.GetRawText());
        Assert.Empty(number.Logs);
        Assert.Null(number.Error);
    }

    [Fact]
    public async Task Statements_return_what_they_return_with_the_log_lines_they_wrote()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        EvalResult statements = await game.Connection.EvalAsync("""Bot.Log("one"); Bot.Log("two"); return "done";""", cancellationToken: Ct);
        EvalResult voidCall = await game.Connection.EvalAsync("""Bot.Log("three")""", cancellationToken: Ct);
        EvalResult nothing = await game.Connection.EvalAsync("""var x = 1;""", cancellationToken: Ct);

        Assert.Equal("done", statements.Value!.Value.GetString());
        Assert.Equal(["one", "two"], statements.Logs);
        Assert.Null(voidCall.Value);
        Assert.Equal(["three"], voidCall.Logs);
        Assert.Null(nothing.Value);
    }

    [Fact]
    public async Task A_snippet_that_throws_returns_the_exception_with_its_stack_and_the_lines_before_it()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        EvalResult result = await game.Connection.EvalAsync("""Bot.Log("before"); throw new InvalidOperationException("boom");""", cancellationToken: Ct);

        Assert.Null(result.Value);
        Assert.Equal(["before"], result.Logs);
        Assert.StartsWith("InvalidOperationException: boom\n", result.Error);
        Assert.Contains("EvalSnippet.Eval", result.Error);
    }

    [Fact]
    public async Task A_snippet_that_doesnt_compile_fails_with_CompileFailed_and_its_diagnostics()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => game.Connection.EvalAsync("Bot.NoSuchThing", cancellationToken: Ct));

        Assert.Equal(ErrorCode.CompileFailed, e.Code);
        Assert.Contains(e.Diagnostics!, d => d.Contains("CS1061", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_snippet_that_runs_too_long_fails_with_Timeout()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);

        ControlException e = await Assert.ThrowsAsync<ControlException>(() => game.Connection.EvalAsync("Thread.Sleep(10_000);", 1, Ct));

        Assert.Equal(ErrorCode.Timeout, e.Code);
    }

    [Fact]
    public async Task The_Test_Account_password_never_comes_back_from_eval()
    {
        await using EngineSandbox sandbox = new();
        await using GameFixture game = await GameFixture.StartAsync(sandbox);
        await game.Connection.LoginAsync("Galanoth", cancellationToken: Ct);

        EvalResult value = await game.Connection.EvalAsync("""Bot.Flash.GetGameObjectStatic("loginInfo.strPassword")""", cancellationToken: Ct);
        EvalResult logged = await game.Connection.EvalAsync("""Bot.Log(Bot.Flash.GetGameObjectStatic("loginInfo.strPassword"))""", cancellationToken: Ct);
        EvalResult thrown = await game.Connection.EvalAsync("""throw new Exception(Bot.Flash.GetGameObjectStatic("loginInfo.strPassword"));""", cancellationToken: Ct);

        string all = JsonSerializer.Serialize(new[] { value, logged, thrown }, ControlJson.Options);
        Assert.DoesNotContain(game.Keychain.Password, all);
        Assert.Contains("[redacted]", value.Value!.Value.GetString());
        Assert.Contains("[redacted]", Assert.Single(logged.Logs));
        Assert.Contains("[redacted]", thrown.Error);
    }
}
