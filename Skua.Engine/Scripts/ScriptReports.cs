using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using Skua.Control;
using Skua.Core.Messaging;
using Skua.Engine.Logging;

namespace Skua.Engine.Scripts;

/// <summary>Records each Script Report, <c>Bot.Report(name, data)</c>, as a <c>script.report</c> event tagged with the run in progress.</summary>
/// <remarks>It records on the reporting thread and never waits: it takes the run's lock, then the logs', briefly.</remarks>
internal sealed class ScriptReports
{
    private readonly EngineLogs _logs;
    private readonly ScriptRuns _runs;

    public ScriptReports(EngineLogs logs, ScriptRuns runs)
    {
        _logs = logs;
        _runs = runs;
        StrongReferenceMessenger.Default.Register<ScriptReports, ScriptReportMessage, int>(
            this, (int)MessageChannels.ScriptStatus, static (r, m) => r.Record(m.Name, m.Data));
    }

    private void Record(string name, object? data)
    {
        ScriptRunDto? run = _runs.Status().Run;
        _logs.Event(EventTypes.ScriptReport, new { run = run?.Number, script = run?.Script, name, data = ToJson(data) });
    }

    /// <summary>
    /// The data as JSON, or, over the cap, its JSON text as a string, which the logs cut to the cap rather than drop; data that can't be
    /// serialized is <c>{error}</c>.
    /// </summary>
    private static JsonNode? ToJson(object? data)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(data, ControlJson.Options);
        }
        catch (Exception e)
        {
            return new JsonObject { ["error"] = $"{e.GetType().Name}: {e.Message}" };
        }
        return Encoding.UTF8.GetByteCount(json) > LogScrubber.MaxReportDataBytes ? JsonValue.Create(json) : JsonNode.Parse(json);
    }
}
