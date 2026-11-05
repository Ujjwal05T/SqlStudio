using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;

namespace SqlStudio.Services.Mcp;

/// <summary>What the tools may reach: the servers the user has allowed, and a way to open them with the app's saved credentials.</summary>
public interface IMcpDatabases
{
    IReadOnlyList<ConnectionProfile> Allowed();
    Task<SqlConnection> OpenAsync(ConnectionProfile profile, string? database, CancellationToken ct);
}

/// <summary>One call an AI made, for the activity list.</summary>
public sealed record McpActivity(DateTime Time, string Tool, string? Connection, string Summary, string Outcome, bool IsError)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>The five tools Claude gets. Passwords never appear in anything they return.</summary>
public sealed class SqlStudioTools(IMcpDatabases databases, Action<McpActivity> log, Action<ConnectionProfile, string, string>? showQuery = null)
{
    public IReadOnlyList<McpTool> All { get; } = Build(databases, log, showQuery);

    static List<McpTool> Build(IMcpDatabases databases, Action<McpActivity> log, Action<ConnectionProfile, string, string>? showQuery)
    {
        static JsonObject Schema(JsonObject properties, params string[] required) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        };
        static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };
        // A JSON node can belong to only one parent, so each schema gets its own copy.
        JsonObject connection() => Text("Name of the connection, exactly as list_connections shows it.");
        JsonObject database() => Text("Database name. Optional when the connection has a default database; list_databases shows the choices.");

        McpTool Tool(string name, string description, JsonObject schema, Func<JsonObject, CancellationToken, Task<ToolResult>> run) =>
            new(name, description, schema, async (args, ct) =>
            {
                string? connectionName = (string?)args["connection"];
                var summary = name == "run_query" ? (string?)args["sql"] ?? "" : (string?)args["table"] ?? (string?)args["text"] ?? (string?)args["database"] ?? "";
                try
                {
                    var result = await run(args, ct);
                    log(new McpActivity(DateTime.Now, name, connectionName, summary, result.Outcome, false));
                    return result;
                }
                catch (Exception ex)
                {
                    log(new McpActivity(DateTime.Now, name, connectionName, summary, ex.Message, true));
                    throw;
                }
            });

        return
        [
            Tool("list_connections",
                "Lists the SQL Server connections the user has allowed Claude to use in SqlStudio. Start here.",
                Schema(new JsonObject()),
                (_, _) =>
                {
                    var allowed = databases.Allowed();
                    var text = allowed.Count == 0
                        ? "No connection is allowed. In SqlStudio, open Connect Claude and tick the servers Claude may use."
                        : string.Join('\n', allowed.Select(p =>
                            $"{p.DisplayName}  (server {p.Server}{(string.IsNullOrEmpty(p.DefaultDatabase) ? "" : ", default database " + p.DefaultDatabase)}{(p.HasEnvironment ? ", " + p.EnvironmentLabel : "")})"));
                    return Task.FromResult(new ToolResult(text, $"{allowed.Count} connection{(allowed.Count == 1 ? "" : "s")}"));
                }),

            Tool("list_databases",
                "Lists the user databases on a connection.",
                Schema(new JsonObject { ["connection"] = connection() }, "connection"),
                async (args, ct) =>
                {
                    var profile = Resolve(databases, args);
                    await using var conn = await databases.OpenAsync(profile, "master", ct);
                    var names = (await CatalogService.ListDatabasesAsync(conn)).Where(d => d.IsOnline && !d.IsSystem).Select(d => d.Name).ToList();
                    return new ToolResult(names.Count == 0 ? "No user databases." : string.Join('\n', names), $"{names.Count} database{(names.Count == 1 ? "" : "s")}");
                }),

            Tool("search_schema",
                "Finds tables and columns whose names contain the given words, to answer questions like 'which table stores customer email?'. " +
                "Every word must appear in the table's name or its column names. Leave text empty to list all tables.",
                Schema(new JsonObject { ["connection"] = connection(), ["database"] = database(), ["text"] = Text("Words to look for in table and column names, e.g. 'customer email'.") }, "connection"),
                async (args, ct) =>
                {
                    var (profile, db) = ResolveDatabase(databases, args);
                    await using var conn = await databases.OpenAsync(profile, "master", ct);
                    var result = SchemaText.Search(await ErCatalog.LoadAsync(conn, db, ct), (string?)args["text"]);
                    return new ToolResult(result, result.Split('\n')[0]);
                }),

            Tool("describe_table",
                "Shows every column of one table (type, key, nullability) and the tables it references or is referenced by.",
                Schema(new JsonObject { ["connection"] = connection(), ["database"] = database(), ["table"] = Text("Table name, with or without schema: 'Orders' or 'dbo.Orders'.") }, "connection", "table"),
                async (args, ct) =>
                {
                    var (profile, db) = ResolveDatabase(databases, args);
                    var table = (string?)args["table"] ?? throw new McpToolException("Say which table to describe.");
                    await using var conn = await databases.OpenAsync(profile, "master", ct);
                    var result = SchemaText.DescribeTable(await ErCatalog.LoadAsync(conn, db, ct), table);
                    return new ToolResult(result, result.Split('\n')[0]);
                }),

            Tool("run_query",
                "Runs one read-only SELECT query. Anything that changes data or schema is refused. " +
                "By default (show_in 'chat') it returns the rows as a text table. When the user asks to see the results in SqlStudio, " +
                "pass show_in 'sqlstudio': the app comes to the front, opens a query tab on that connection and runs the query there, " +
                "and no rows come back to you, so just tell the user the query is open in SqlStudio and do not repeat the SQL or the rows in chat. " +
                $"In chat mode it returns at most {ReadOnlyQuery.DefaultRows} rows unless max_rows is set (up to {ReadOnlyQuery.MaxRows}). Use TOP in the query for large tables.",
                Schema(new JsonObject
                {
                    ["connection"] = connection(),
                    ["database"] = database(),
                    ["sql"] = Text("A single T-SQL SELECT statement."),
                    ["max_rows"] = new JsonObject { ["type"] = "integer", ["description"] = $"Row limit for chat mode, default {ReadOnlyQuery.DefaultRows}, at most {ReadOnlyQuery.MaxRows}." },
                    ["show_in"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("chat", "sqlstudio"),
                        ["description"] = "'chat' (default) returns the rows to you. 'sqlstudio' shows them in the SqlStudio window instead, for when the user asks to see the output there.",
                    },
                }, "connection", "sql"),
                async (args, ct) =>
                {
                    var (profile, db) = ResolveDatabase(databases, args);
                    var sql = (string?)args["sql"] ?? throw new McpToolException("Give the SELECT query in sql.");
                    var maxRows = args["max_rows"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : ReadOnlyQuery.DefaultRows;
                    var showIn = ((string?)args["show_in"])?.Trim().ToLowerInvariant();
                    if (showIn is not (null or "" or "chat" or "sqlstudio"))
                        throw new McpToolException("show_in must be \"chat\" or \"sqlstudio\".");
                    if (ReadOnlySql.Check(sql) is { } problem)
                        throw new McpToolException(problem); // refuse before opening a connection
                    if (showIn == "sqlstudio" && showQuery is not null)
                    {
                        if (sql.Length > ReadOnlyQuery.MaxSqlLength)
                            throw new McpToolException($"The query is longer than {ReadOnlyQuery.MaxSqlLength:N0} characters.");
                        showQuery(profile, db, sql); // the app's own tab runs it, so it is not run twice
                        return new ToolResult(
                            "The query is open and running in a SqlStudio tab, which has been brought to the front. The rows are shown there, not here. Tell the user to look in SqlStudio; do not repeat the query or its rows in chat.",
                            "shown in SqlStudio");
                    }
                    await using var conn = await databases.OpenAsync(profile, db, ct);
                    var table = await ReadOnlyQuery.RunAsync(conn, sql, maxRows, ct);
                    return new ToolResult(ReadOnlyQuery.ToText(table, maxRows), $"{table.Rows.Count:N0} row{(table.Rows.Count == 1 ? "" : "s")}{(table.Truncated ? " (cut at the limit)" : "")}");
                }),
        ];
    }

    static ConnectionProfile Resolve(IMcpDatabases databases, JsonObject args)
    {
        var name = (string?)args["connection"];
        var allowed = databases.Allowed();
        var matches = allowed.Where(p => p.DisplayName.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1)
            return matches[0];
        var known = allowed.Count == 0 ? "none is allowed" : string.Join(", ", allowed.Select(p => p.DisplayName));
        throw new McpToolException(matches.Count > 1
            ? $"More than one allowed connection is named \"{name}\"."
            : $"No allowed connection named \"{name}\". Allowed connections: {known}. The user can allow more in SqlStudio under Connect Claude.");
    }

    static (ConnectionProfile Profile, string Database) ResolveDatabase(IMcpDatabases databases, JsonObject args)
    {
        var profile = Resolve(databases, args);
        var database = (string?)args["database"];
        database = string.IsNullOrWhiteSpace(database) ? profile.DefaultDatabase : database.Trim();
        return string.IsNullOrEmpty(database)
            ? throw new McpToolException($"\"{profile.DisplayName}\" has no default database, so say which one to use. list_databases shows the choices.")
            : (profile, database);
    }
}
