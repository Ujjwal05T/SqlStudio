using SqlStudio.Models;
using SqlStudio.Services.Company.Api;

namespace SqlStudio.Services.Guard;

public sealed record GuardVerdict(bool Allowed, string? Message, int? Line)
{
    public static GuardVerdict Ok { get; } = new(true, null, null);
}

/// <summary>
/// Applies a company server's per-database permissions to a classified script. Nothing is run, logged or sent from
/// here, and messages never quote the script.
/// </summary>
public static class QueryGuard
{
    const string NothingRun = "Nothing in this script was run.";

    public static Permission PermissionFor(SqlAction action) => action switch
    {
        SqlAction.Select => Permission.Read,
        SqlAction.Insert or SqlAction.Update or SqlAction.Delete => Permission.Write,
        SqlAction.Ddl => Permission.Ddl,
        SqlAction.Execute => Permission.Execute,
        _ => Permission.None,
    };

    public static Task<GuardVerdict> CheckAsync(string script, string? currentDatabase, ManagedServerAccess access, string serverName) =>
        Task.Run(() => Check(StatementClassifier.Classify(script), currentDatabase, access, serverName));

    public static GuardVerdict Check(Classification c, string? currentDatabase, ManagedServerAccess access, string serverName)
    {
        if (access.IsRevoked)
            return Block(access.RevokedReason ?? $"Not run. You're signed out of company connections. Sign in again to use {serverName}.", null);

        if (c.ParseError is { } pe)
            return Block($"Not run. SqlStudio couldn't read line {pe.Line} ({pe.Message}), so it can't check this script against your company access. Fix it and run again.", pe.Line);

        var current = currentDatabase;
        return CheckStatements(c.Statements, ref current, access, serverName, null);
    }

    static GuardVerdict CheckStatements(IReadOnlyList<ClassifiedStatement> statements, ref string? current,
        ManagedServerAccess access, string serverName, int? outerLine)
    {
        foreach (var s in statements)
        {
            var line = outerLine ?? s.Line;

            if (s.NeverAllowedReason is { } reason)
                return Block($"Not run. Line {line}: {reason}. This is never allowed on company connections. {NothingRun}", line);

            if (s.UseDatabase is { } use)
            {
                if (!access.CanUse(use))
                    return NoDatabase(line, use, serverName);
                current = use;
            }

            foreach (var r in s.Requires)
            {
                var db = r.Database ?? current;
                if (db is null)
                    return Block($"Not run. Line {line}: no database is selected, so SqlStudio can't check this statement against your company access. {NothingRun}", line);
                if (!access.CanUse(db))
                    return NoDatabase(line, db, serverName);

                var needed = PermissionFor(r.Action);
                if (!access.Has(db, needed))
                    return Block($"Not run. Line {line}: {Kind(s, r.Action)} on {db} isn't allowed for you. Your access to {db}: {Labels(access, db)}. {NothingRun}", line);
            }

            if (s.Nested.Count > 0)
            {
                // A USE inside dynamic SQL lasts only for that EXEC, so work on a copy.
                var inner = current;
                var v = CheckStatements(s.Nested, ref inner, access, serverName, line);
                if (!v.Allowed)
                    return v;
            }
        }
        return GuardVerdict.Ok;
    }

    public static GuardVerdict CheckDatabaseSwitch(string database, ManagedServerAccess access, string serverName) =>
        !access.IsRevoked && access.CanUse(database)
            ? GuardVerdict.Ok
            : new(false, $"You don't have access to {database} on {serverName}.", null);

    public static GuardVerdict CheckChanges(string database, ManagedServerAccess access, string serverName) =>
        access.Has(database, Permission.Write)
            ? GuardVerdict.Ok
            : new(false, $"Not saved. Changes to {database} aren't allowed for you. Your access to {database}: {Labels(access, database)}.", null);

    static GuardVerdict Block(string message, int? line) => new(false, message, line);

    static GuardVerdict NoDatabase(int line, string db, string server) =>
        Block($"Not run. Line {line}: you don't have access to database {db} on {server}. {NothingRun}", line);

    static string Labels(ManagedServerAccess access, string db)
    {
        var p = access.PermissionsFor(db);
        var labels = new[] { Permission.Read, Permission.Write, Permission.Ddl, Permission.Execute }
            .Where(f => (p & f) == f)
            .Select(f => PermissionNames.Label(f).ToLowerInvariant())
            .ToList();
        return labels.Count == 0 ? "none" : string.Join(", ", labels);
    }

    static string Kind(ClassifiedStatement s, SqlAction action)
    {
        if (action == SqlAction.Ddl) return "a structure change";
        if (action == SqlAction.Execute) return "EXEC";
        var verb = s.Summary.Split(' ', 2)[0];
        return action switch
        {
            SqlAction.Select => "SELECT",
            _ when verb is "INSERT" or "UPDATE" or "DELETE" or "MERGE" => verb,
            SqlAction.Insert => "INSERT",
            SqlAction.Update => "UPDATE",
            _ => "DELETE",
        };
    }
}
