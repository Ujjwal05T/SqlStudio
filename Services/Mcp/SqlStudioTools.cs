using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services;

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

/// <summary>The seven tools Claude gets. Passwords never appear in anything they return.</summary>
public sealed class SqlStudioTools(IMcpDatabases databases, Action<McpActivity> log, Action<ConnectionProfile, string, string>? showQuery = null, SchemaNoteStore? notes = null)
{
    public IReadOnlyList<McpTool> All { get; } = Build(databases, log, showQuery, notes);

    static List<McpTool> Build(IMcpDatabases databases, Action<McpActivity> log, Action<ConnectionProfile, string, string>? showQuery, SchemaNoteStore? notes)
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
                var summary = name is "run_query" or "open_in_sqlstudio" ? (string?)args["sql"] ?? "" : (string?)args["table"] ?? (string?)args["text"] ?? (string?)args["database"] ?? "";
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
                    var allowed = databases.Allowed().Where(p => !p.IsManaged).ToList();
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

            Tool("schema_overview",
                "The whole database in one call: every table with its columns, types, keys and relationships, plus the notes the user wrote in SqlStudio " +
                "(what codes mean, how tables join). Read this first for an unfamiliar database instead of calling search_schema and describe_table repeatedly.",
                Schema(new JsonObject { ["connection"] = connection(), ["database"] = database() }, "connection"),
                async (args, ct) =>
                {
                    var (profile, db) = ResolveDatabase(databases, args);
                    await using var conn = await databases.OpenAsync(profile, "master", ct);
                    var result = SchemaText.Overview(await ErCatalog.LoadAsync(conn, db, ct), db, notes?.ForDatabase(profile.Id, db) ?? []);
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
                "Runs one read-only SELECT query and returns the rows to you, as a text table, so you can read and reason about them. " +
                "Anything that changes data or schema is refused. " +
                $"Returns at most {ReadOnlyQuery.DefaultRows} rows unless max_rows is set (up to {ReadOnlyQuery.MaxRows}). Use TOP in the query for large tables. " +
                "If the user wants to see the results themselves in SqlStudio, use open_in_sqlstudio instead.",
                Schema(new JsonObject
                {
                    ["connection"] = connection(),
                    ["database"] = database(),
                    ["sql"] = Text("A single T-SQL SELECT statement."),
                    ["max_rows"] = new JsonObject { ["type"] = "integer", ["description"] = $"Row limit, default {ReadOnlyQuery.DefaultRows}, at most {ReadOnlyQuery.MaxRows}." },
                }, "connection", "sql"),
                async (args, ct) =>
                {
                    var (profile, db) = ResolveDatabase(databases, args);
                    var sql = CheckedSql(args);
                    var maxRows = args["max_rows"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : ReadOnlyQuery.DefaultRows;
                    await using var conn = await databases.OpenAsync(profile, db, ct);
                    var table = await ReadOnlyQuery.RunAsync(conn, sql, maxRows, ct);
                    return new ToolResult(ReadOnlyQuery.ToText(table, maxRows), $"{table.Rows.Count:N0} row{(table.Rows.Count == 1 ? "" : "s")}{(table.Truncated ? " (cut at the limit)" : "")}");
                }),

            Tool("open_in_sqlstudio",
                "Shows a read-only SELECT query to the user: SqlStudio comes to the front, opens a query tab on that connection and runs the query there. " +
                "Use it when the user asks to see the results in SqlStudio. No rows come back to you, so just tell the user the query is open in SqlStudio " +
                "and do not repeat the SQL or the rows in chat. To read the rows yourself, use run_query.",
                Schema(new JsonObject
                {
                    ["connection"] = connection(),
                    ["database"] = database(),
                    ["sql"] = Text("A single T-SQL SELECT statement."),
                }, "connection", "sql"),
                (args, _) =>
                {
                    var (profile, db) = ResolveDatabase(databases, args);
                    var sql = CheckedSql(args);
                    if (showQuery is null)
                        throw new McpToolException("SqlStudio cannot open a query tab right now. Use run_query to get the rows instead.");
                    if (sql.Length > ReadOnlyQuery.MaxSqlLength)
                        throw new McpToolException($"The query is longer than {ReadOnlyQuery.MaxSqlLength:N0} characters.");
                    showQuery(profile, db, sql); // the app's own tab runs it, so it is not run twice
                    return Task.FromResult(new ToolResult(
                        "The query is open and running in a SqlStudio tab, which has been brought to the front. The rows are shown there, not here. Tell the user to look in SqlStudio; do not repeat the query or its rows in chat.",
                        "shown in SqlStudio"));
                }),
        ];
    }

    /// <summary>The query text, refused before anything is opened unless it is a single read-only SELECT.</summary>
    static string CheckedSql(JsonObject args)
    {
        var sql = (string?)args["sql"] ?? throw new McpToolException("Give the SELECT query in sql.");
        if (ReadOnlySql.Check(sql) is { } problem)
            throw new McpToolException(problem);
        return sql;
    }

    static ConnectionProfile Resolve(IMcpDatabases databases, JsonObject args)
    {
        var name = (string?)args["connection"];
        // Company (managed) servers are never reachable here: their per-database grants, query checks and audit live in the app's own tabs.
        var allowed = databases.Allowed().Where(p => !p.IsManaged).ToList();
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
