using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Agent.Server;

/// <summary>Response envelope (shared/schemas/response.v1.schema.json).</summary>
internal static class Responses
{
    public static byte[] Ok(Guid? id, JsonObject? payload = null) => Write(id, ok: true, payload ?? [], error: null);

    public static byte[] Error(Guid? id, string code, string message) =>
        Write(id, ok: false, payload: null, error: new JsonObject { ["code"] = code, ["message"] = message });

    private static byte[] Write(Guid? id, bool ok, JsonObject? payload, JsonObject? error)
    {
        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["id"] = id?.ToString("D"),
            ["kind"] = "response",
            ["ok"] = ok,
        };
        if (payload is not null)
        {
            envelope["payload"] = payload;
        }
        if (error is not null)
        {
            envelope["error"] = error;
        }
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }
}

/// <summary>A handler's answer, plus optional work to run only after the answer was sent.</summary>
internal sealed record HandlerResult(byte[] Response, bool Ok, string? ErrorCode, Func<Task>? AfterSend = null);
