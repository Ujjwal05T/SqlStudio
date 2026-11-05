using System.Data.Common;
using System.Text.Json.Nodes;

namespace SqlStudio.Services.Mcp;

/// <summary>What a tool sends back. <see cref="Outcome"/> is a short line for the activity log.</summary>
public sealed record ToolResult(string Text, string Outcome);

/// <summary>A refusal or mistake the caller should see as a plain message (an unknown connection, a rejected query).</summary>
public sealed class McpToolException(string message) : Exception(message);

public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, CancellationToken, Task<ToolResult>> Run);

/// <summary>
/// The Model Context Protocol for tools, as JSON-RPC 2.0: initialize, ping, tools/list and tools/call.
/// It knows nothing about sockets, so it can be tested by passing JSON in and reading JSON out.
/// </summary>
public sealed class McpDispatcher(IReadOnlyList<McpTool> tools, string serverName, string serverVersion)
{
    static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The reply to a request, or null for a notification (which has no id and gets no reply).</summary>
    public async Task<JsonNode?> HandleAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is not JsonObject request || request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
            return Error((message as JsonObject)?["id"], -32600, "Invalid request");

        var id = request["id"];
        if (id is null)
            return null; // notifications/initialized and the like

        var args = request["params"] as JsonObject;
        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(args)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, ListTools()),
                "tools/call" => Result(id, await CallAsync(args, ct)),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (McpProtocolException ex)
        {
            return Error(id, ex.Code, ex.Message);
        }
    }

    JsonObject Initialize(JsonObject? args)
    {
        var asked = (string?)args?["protocolVersion"];
        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && SupportedVersions.Contains(asked) ? asked : SupportedVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = serverName, ["version"] = serverVersion },
        };
    }

    JsonObject ListTools() => new()
    {
        ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["inputSchema"] = t.InputSchema.DeepClone(),
            ["annotations"] = new JsonObject { ["readOnlyHint"] = true },
        }).ToArray()),
    };

    async Task<JsonObject> CallAsync(JsonObject? args, CancellationToken ct)
    {
        var name = (string?)args?["name"];
        var tool = tools.FirstOrDefault(t => t.Name == name) ?? throw new McpProtocolException(-32602, $"Unknown tool: {name}");
        try
        {
            var result = await tool.Run(args?["arguments"] as JsonObject ?? new JsonObject(), ct);
            return Content(result.Text, isError: false);
        }
        catch (Exception ex) when (ex is McpToolException or DbException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            // The tool failed in a way the caller can act on, so it is a result, not a protocol error.
            return Content(ex.Message, isError: true);
        }
    }

    static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    sealed class McpProtocolException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
