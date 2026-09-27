using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using JsonProperty = Newtonsoft.Json.Serialization.JsonProperty;
using JsonSerializer = System.Text.Json.JsonSerializer;
using Skua.App.Engine.Logging;
using Skua.Control;
using Skua.Core.Interfaces;
using Skua.Core.Models;
using StreamJsonRpc;

namespace Skua.App.Engine.Scripts;

/// <summary><c>eval</c>: a C# snippet compiled against <c>IScriptInterface Bot</c> as the console compiles one, run on a thread of its own.</summary>
internal sealed class EvalOperations
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A larger value comes back as a JSON string of its start.</summary>
    private const int MaxValueChars = 256 * 1024;

    private const int MaxErrorChars = 4 * 1024;

    /// <summary>The console's usings, so a snippet reads as a Script body does.</summary>
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using Skua.Core;
        using Skua.Core.Interfaces;
        using Skua.Core.Utils;
        using Skua.Core.Models;
        using Skua.Core.Models.Auras;
        using Skua.Core.Models.Items;
        using Skua.Core.Models.Monsters;
        using Skua.Core.Models.Players;
        using Skua.Core.Models.Quests;
        using Skua.Core.Models.Servers;
        using Skua.Core.Models.Shops;
        using Skua.Core.Models.Skills;
        using Newtonsoft.Json;
        """;

    private static readonly JsonSerializerSettings ValueSettings = new()
    {
        ContractResolver = new MemberNames(),
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        MaxDepth = 32,
        Converters = { new StringEnumConverter() },
        // Core's models have getters that throw before login; such a member is left out.
        Error = (_, e) => e.ErrorContext.Handled = true,
    };

    private readonly IScriptManager _manager;
    private readonly IScriptInterface _bot;
    private readonly EngineLogs _logs;
    private readonly SemaphoreSlim _compiling;

    public EvalOperations(IScriptManager manager, IScriptInterface bot, EngineLogs logs, SemaphoreSlim compiling)
    {
        _manager = manager;
        _bot = bot;
        _logs = logs;
        _compiling = compiling;
    }

    public async Task<EvalResult> EvalAsync(string code, int? timeoutSec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw RpcErrors.Of(ErrorCode.InvalidArgument, "Pass a snippet, e.g. Bot.Player.Level.");
        if (timeoutSec < 1)
            throw RpcErrors.Of(ErrorCode.InvalidArgument, $"timeoutSec must be at least 1, not {timeoutSec}.");
        TimeSpan timeout = timeoutSec is { } seconds ? TimeSpan.FromSeconds(seconds) : DefaultTimeout;

        object snippet = await CompileAsync(code, timeout, cancellationToken);
        TaskCompletionSource<(object? Value, Exception? Thrown)> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> lines = [];
        Thread thread = new(() =>
        {
            using IDisposable capture = EngineLogService.CaptureScriptLines(lines);
            try
            {
                done.SetResult((snippet.GetType().GetMethod("Eval")!.Invoke(snippet, [_bot]), null));
            }
            catch (TargetInvocationException e)
            {
                done.SetResult((null, e.InnerException ?? e));
            }
            catch (Exception e)
            {
                done.SetResult((null, e));
            }
        })
        {
            IsBackground = true,
            Name = "Eval",
        };
        thread.Start();

        (object? value, Exception? thrown) result;
        try
        {
            result = await done.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw RpcErrors.Of(ErrorCode.Timeout, $"The snippet was still running after {timeout.TotalSeconds:0} s; it goes on in the background until it returns.");
        }

        List<string> logged;
        lock (lines)
            logged = [.. lines];
        return new EvalResult(
            result.thrown is null ? ToJson(result.value) : null,
            logged,
            result.thrown is { } e ? Cap(_logs.Scrub($"{e.GetType().Name}: {e.Message}\n{e.StackTrace}"), MaxErrorChars) : null);
    }

    /// <summary>
    /// Compiles through Core's Script manager, as the console does. An expression is returned; if it has no value (a void call),
    /// it is compiled again as a statement.
    /// </summary>
    /// <param name="timeout">How long to wait for another compile (a Script's start, say) to finish first.</param>
    private async Task<object> CompileAsync(string code, TimeSpan timeout, CancellationToken cancellationToken)
    {
        bool expression = !SyntaxFactory.ParseExpression(code).ContainsDiagnostics;
        if (!await _compiling.WaitAsync(timeout, cancellationToken))
            throw RpcErrors.Of(ErrorCode.Timeout, $"Another compile, such as a Script's start, still ran after {timeout.TotalSeconds:0} s; try again.");
        try
        {
            if (expression)
            {
                try
                {
                    return await CompileBodyAsync($"return (object)(\n{code}\n);");
                }
                catch (ScriptCompileException e) when (e.Message.Contains("CS0030", StringComparison.Ordinal))
                {
                    // Cannot convert type 'void' to 'object'.
                }
            }
            return await CompileBodyAsync($"{code}{(expression ? ";" : "")}\nreturn null;");
        }
        catch (ScriptCompileException e)
        {
            throw CompileFailure.Of("The snippet", e.Message);
        }
        catch (Exception e) when (e is not LocalRpcException and not OperationCanceledException)
        {
            string message = $"{e.GetType().Name}: {e.Message}";
            throw RpcErrors.Of(ErrorCode.CompileFailed, $"The snippet couldn't be compiled: {message}", [message]);
        }
        finally
        {
            _compiling.Release();
        }
    }

    private async Task<object> CompileBodyAsync(string body)
    {
        string source = $$"""
            {{Usings}}
            public class EvalSnippet
            {
                public object Eval(IScriptInterface Bot)
                {
            {{body}}
                }
            }
            """;
        return await Task.Run(() => _manager.Compile(source)) ?? throw new ScriptCompileException("The snippet compiled to nothing.", source);
    }

    /// <summary>The value as JSON, redacted, on a best-effort basis: members that throw are left out, and anything unserializable becomes its text.</summary>
    private JsonElement? ToJson(object? value)
    {
        if (value is null)
            return null;
        string json;
        try
        {
            json = JsonConvert.SerializeObject(value, ValueSettings);
        }
        catch (Exception e) when (e is Newtonsoft.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            json = JsonConvert.SerializeObject(value.ToString());
        }
        if (json.Length > MaxValueChars)
            json = JsonConvert.SerializeObject(json[..MaxValueChars] + "… (cut; return less)");

        string redacted = _logs.Scrub(json);
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(redacted);
        }
        catch (System.Text.Json.JsonException)
        {
            // A redaction inside an escape sequence; the text is still useful.
            return JsonSerializer.SerializeToElement(redacted);
        }
    }

    private static string Cap(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];

    /// <summary>Names members as .NET does, in camelCase, instead of the AS3 wire names Core's models serialize with.</summary>
    private sealed class MemberNames : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            property.PropertyName = JsonNamingPolicy.CamelCase.ConvertName(member.Name);
            return property;
        }
    }
}
