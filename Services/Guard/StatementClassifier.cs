using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlStudio.Services.Guard;

public enum SqlAction { Select, Insert, Update, Delete, Ddl, Execute }

/// <summary>One thing a statement needs. <c>Database == null</c> means the current database at that point.</summary>
public sealed record Requirement(string? Database, SqlAction Action, string Object);

public sealed record ClassifiedStatement(
    int Line,
    string Summary,
    IReadOnlyList<Requirement> Requires,
    string? UseDatabase,
    string? NeverAllowedReason,
    IReadOnlyList<ClassifiedStatement> Nested);

public sealed record ParseProblem(int Line, string Message);

public sealed record Classification(IReadOnlyList<ClassifiedStatement> Statements, ParseProblem? ParseError);

/// <summary>
/// Reads a script with Microsoft's own T-SQL parser and says what each statement needs.
/// Anything it does not recognise, and anything that fails to parse, is blocked. The script text is
/// never logged or sent anywhere from here.
/// </summary>
public static partial class StatementClassifier
{
    const string Unrecognised = "SqlStudio doesn't recognise this kind of statement";

    const int MaxDepth = 8;

    /// <summary>
    /// Procedures that are never allowed, lowercased and without schema. <c>xp_</c> and <c>sp_oa</c> are also matched
    /// by prefix (see <see cref="IsNeverAllowedProcedure"/>).
    /// </summary>
    public static IReadOnlySet<string> NeverAllowedProcedures { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "xp_cmdshell", "sp_configure", "sp_addrolemember", "sp_droprolemember", "sp_addsrvrolemember",
        "sp_dropsrvrolemember", "sp_addlogin", "sp_droplogin", "sp_adduser", "sp_dropuser", "sp_grantdbaccess",
        "sp_revokedbaccess", "sp_addrole", "sp_droprole", "sp_password", "sp_change_users_login", "sp_changedbowner",
        "sp_setapprole", "sp_addlinkedserver", "sp_droplinkedsrvlogin", "sp_addlinkedsrvlogin", "sp_dropserver",
        "sp_addserver", "sp_serveroption", "sp_addextendedproc", "sp_dropextendedproc", "sp_procoption",
        "sp_attach_db", "sp_attach_single_file_db", "sp_detach_db", "sp_dboption", "sp_makewebtask",
        "sp_msforeachdb", "sp_msforeachtable", "sp_msforeach_worker", "sp_prepexec", "sp_prepexecrpc", "sp_prepare",
        "sp_execute", "sp_unprepare", "sp_add_job", "sp_start_job", "sp_add_jobstep", "sp_add_jobserver",
    }.ToFrozenSet();

    static bool IsNeverAllowedProcedure(string name)
    {
        var n = name.ToLowerInvariant();
        return NeverAllowedProcedures.Contains(n) || n.StartsWith("xp_", StringComparison.Ordinal)
            || n.StartsWith("sp_oa", StringComparison.Ordinal) || n.StartsWith("sp_cursor", StringComparison.Ordinal)
            || n.StartsWith("sp_msforeach", StringComparison.Ordinal);
    }

    /// <summary>
    /// Harmless system procedures that may be called without a database or schema. Every other <c>sp_*</c> procedure (and
    /// anything in <c>sys</c>, <c>master</c> or <c>msdb</c>) is blocked: many take SQL text or reach other databases.
    /// </summary>
    static readonly FrozenSet<string> SafeSystemProcedures = new HashSet<string>(StringComparer.Ordinal)
    {
        "sp_help", "sp_helpindex", "sp_columns", "sp_who", "sp_who2", "sp_spaceused", "sp_helptext",
    }.ToFrozenSet();

    static readonly FrozenSet<string> LiteralOnlyProcedures = new HashSet<string>(StringComparer.Ordinal)
    {
        "sp_help", "sp_helpindex", "sp_columns", "sp_spaceused", "sp_helptext",
    }.ToFrozenSet();

    static readonly FrozenSet<string> SystemDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "master", "msdb", "model",
    }.ToFrozenSet();

    /// <summary>
    /// A call to an allow-listed system procedure that can only look at the current database: object names are plain
    /// literals of at most two parts, and <c>@table_qualifier</c> (another database) is never passed.
    /// </summary>
    static bool IsSafeSystemCall(string lower, ExecutableProcedureReference call)
    {
        if (!SafeSystemProcedures.Contains(lower)) return false;
        if (!LiteralOnlyProcedures.Contains(lower)) return true;
        foreach (var p in call.Parameters)
        {
            if (p.Variable?.Name.Equals("@table_qualifier", StringComparison.OrdinalIgnoreCase) == true) return false;
            switch (p.ParameterValue)
            {
                case StringLiteral l:
                    if (NamePartCount(l.Value) > 2) return false;
                    break;
                case IntegerLiteral or NullLiteral:
                    break;
                default:
                    return false; // a variable or expression could hold a three-part name
            }
        }
        return true;
    }

    /// <summary>Counts the dot-separated parts of an object name, ignoring dots inside [brackets] or "quotes".</summary>
    static int NamePartCount(string text)
    {
        var parts = 1;
        char? close = null;
        foreach (var c in text)
        {
            if (close is { } q) { if (c == q) close = null; }
            else if (c == '[') close = ']';
            else if (c == '"') close = '"';
            else if (c == '.') parts++;
        }
        return parts;
    }

    public static Classification Classify(string script) => Classify(script, 0);

    static Classification Classify(string script, int depth)
    {
        var statements = new List<ClassifiedStatement>();
        foreach (var batch in BatchSplitter.Split(script))
        {
            var fragment = new TSql180Parser(true).Parse(new StringReader(batch.Text), out var errors);
            if (errors.Count > 0)
            {
                var e = errors[0];
                return new Classification([], new ParseProblem(batch.StartLine + e.Line - 1, e.Message));
            }

            if (fragment is TSqlScript s)
                foreach (var b in s.Batches)
                    foreach (var stmt in b.Statements)
                        Walk(stmt, batch.StartLine, false, depth, statements);
        }

        return new Classification(statements, null);
    }

    static int LineOf(TSqlFragment f, int batchStart) => batchStart + Math.Max(f.StartLine, 1) - 1;

    static void Walk(TSqlStatement stmt, int start, bool inFlow, int depth, List<ClassifiedStatement> output)
    {
        switch (stmt)
        {
            case IfStatement s:
                Predicate(s, s.Predicate, start, output);
                Walk(s.ThenStatement, start, true, depth, output);
                if (s.ElseStatement is not null) Walk(s.ElseStatement, start, true, depth, output);
                return;
            case WhileStatement s:
                Predicate(s, s.Predicate, start, output);
                Walk(s.Statement, start, true, depth, output);
                return;
            case BeginEndBlockStatement s:
                foreach (var x in s.StatementList.Statements) Walk(x, start, true, depth, output);
                return;
            case TryCatchStatement s:
                foreach (var x in s.TryStatements.Statements) Walk(x, start, true, depth, output);
                foreach (var x in s.CatchStatements.Statements) Walk(x, start, true, depth, output);
                return;
            case UseStatement when inFlow:
                output.Add(Blocked(stmt, start, "USE inside IF/WHILE/TRY can't be checked"));
                return;
        }

        output.Add(ClassifyOne(stmt, start, depth));
    }

    static void Predicate(TSqlStatement owner, TSqlFragment predicate, int start, List<ClassifiedStatement> output)
    {
        var reads = new Collector(predicate);
        predicate.Accept(reads);
        if (reads.BlockReason is not null)
            output.Add(Blocked(owner, start, reads.BlockReason));
        else if (reads.Requires.Count > 0)
            output.Add(new ClassifiedStatement(LineOf(predicate, start), Summary(owner, null), Distinct(reads.Requires), null, null, []));
    }

    static ClassifiedStatement ClassifyOne(TSqlStatement stmt, int start, int depth)
    {
        var line = LineOf(stmt, start);
        var requires = new List<Requirement>();
        string? obj = null;

        // Everything below adds to `requires` and falls out to a single result; anything unmapped is blocked.
        switch (stmt)
        {
            case SelectStatement s:
                requires.Add(new(null, SqlAction.Select, ""));
                if (s.Into is { } into)
                {
                    obj = Name(into);
                    if (!IsTemp(into))
                    {
                        requires.Add(new(Db(into), SqlAction.Select, Name(into)));
                        requires.Add(new(Db(into), SqlAction.Ddl, Name(into)));
                    }
                }
                break;

            case InsertStatement s:
                obj = Describe(s.InsertSpecification.Target);
                Write(s, s.InsertSpecification, [SqlAction.Insert], requires);
                if (s.InsertSpecification.InsertSource is ExecuteInsertSource eis)
                {
                    // INSERT ... EXEC: the target is written and the procedure is run. Dynamic SQL here can't be
                    // classified as a whole, so only a plain procedure call is accepted.
                    var run = ClassifyExecute(eis.Execute, line, depth);
                    if (run.NeverAllowedReason is not null) return Blocked(stmt, start, run.NeverAllowedReason);
                    if (run.Nested.Count > 0) return Blocked(stmt, start, "INSERT ... EXEC of dynamic SQL can't be checked");
                    requires.AddRange(run.Requires);
                }
                break;

            case UpdateStatement s:
                obj = Describe(s.UpdateSpecification.Target);
                Write(s, s.UpdateSpecification, [SqlAction.Update], requires);
                break;

            case DeleteStatement s:
                obj = Describe(s.DeleteSpecification.Target);
                Write(s, s.DeleteSpecification, [SqlAction.Delete], requires);
                break;

            case MergeStatement s:
            {
                var spec = s.MergeSpecification;
                obj = Describe(spec.Target);
                var actions = new List<SqlAction>();
                foreach (var clause in spec.ActionClauses)
                    actions.Add(clause.Action switch
                    {
                        InsertMergeAction => SqlAction.Insert,
                        UpdateMergeAction => SqlAction.Update,
                        DeleteMergeAction => SqlAction.Delete,
                        _ => SqlAction.Ddl, // unknown action: demand the strongest permission
                    });
                Write(s, spec, actions, requires);
                break;
            }

            case TruncateTableStatement s:
                obj = Name(s.TableName);
                if (!IsTemp(s.TableName))
                {
                    requires.Add(new(Db(s.TableName), SqlAction.Delete, obj));
                    requires.Add(new(Db(s.TableName), SqlAction.Ddl, obj));
                }
                break;

            case SetIdentityInsertStatement s:
                obj = Name(s.Table);
                if (!IsTemp(s.Table))
                    requires.Add(new(Db(s.Table), SqlAction.Insert, obj));
                break;

            case ExecuteStatement s:
                return ClassifyExecute(s.ExecuteSpecification, line, depth);

            case UseStatement s:
                return new ClassifiedStatement(line, "USE " + s.DatabaseName.Value, [], s.DatabaseName.Value, null, []);

            case var s when NeverAllowedStatement(s) is { } reason:
                return Blocked(stmt, start, reason);

            case var s when IsNeutral(s):
            {
                var reads = new Collector(s);
                s.Accept(reads);
                if (reads.BlockReason is not null) return Blocked(stmt, start, reads.BlockReason);
                requires.AddRange(reads.Requires);
                break;
            }

            case var s when IsDdl(s):
                return Ddl(s, start, depth);

            default:
                return Blocked(stmt, start, Unrecognised);
        }

        // Reads inside the statement (sources, subqueries, CTE bodies, predicates) for the DML and SELECT cases.
        if (stmt is not (TruncateTableStatement or SetIdentityInsertStatement))
        {
            var exclude = WriteTargets(stmt);
            var reads = new Collector(stmt, exclude);
            stmt.Accept(reads);
            if (reads.BlockReason is not null) return Blocked(stmt, start, reads.BlockReason);
            requires.AddRange(reads.Requires);
        }

        requires.RemoveAll(r => r.Object.Length == 0 && requires.Any(o => o.Action == SqlAction.Select && o.Object.Length > 0));
        return new ClassifiedStatement(line, Summary(stmt, obj), Distinct(requires), null, null, []);
    }

    // ---- DML helpers -------------------------------------------------------------------------------

    static IEnumerable<TableReference> WriteTargets(TSqlStatement stmt)
    {
        var spec = stmt switch
        {
            InsertStatement s => (DataModificationSpecification)s.InsertSpecification,
            UpdateStatement s => s.UpdateSpecification,
            DeleteStatement s => s.DeleteSpecification,
            MergeStatement s => s.MergeSpecification,
            _ => null,
        };
        if (spec is null) yield break;
        yield return spec.Target;
        if (spec.OutputIntoClause?.IntoTable is { } t) yield return t;
    }

    static void Write(TSqlStatement stmt, DataModificationSpecification spec, IEnumerable<SqlAction> actions, List<Requirement> requires)
    {
        var acts = actions.Distinct().ToList();
        foreach (var name in ResolveTargets(stmt, spec.Target))
            foreach (var a in acts)
                requires.Add(new(Db(name), a, Name(name)));

        // OUTPUT ... INTO t writes to t.
        if (spec.OutputIntoClause?.IntoTable is NamedTableReference into && !IsTemp(into.SchemaObject))
            requires.Add(new(Db(into.SchemaObject), SqlAction.Insert, Name(into.SchemaObject)));
    }

    /// <summary>
    /// The real tables a write lands in. A temp table or table variable lands in none. An alias
    /// (<c>UPDATE t ... FROM dbo.T t</c>) resolves to the FROM table. A CTE target (<c>WITH c AS (...) DELETE c</c>)
    /// resolves to every table the CTEs read, so a write can't hide behind a CTE.
    /// </summary>
    static IEnumerable<SchemaObjectName> ResolveTargets(TSqlStatement stmt, TableReference target)
    {
        if (target is not NamedTableReference n) yield break; // variable, or something we can't write to
        var name = n.SchemaObject;

        if (name.SchemaIdentifier is null && name.DatabaseIdentifier is null)
        {
            var scan = new Collector(stmt);
            stmt.Accept(scan);
            if (scan.Ctes.Contains(name.BaseIdentifier.Value))
            {
                foreach (var t in scan.CteTables) yield return t;
                yield break;
            }

            // Every reference with that alias (or that bare name) anywhere in the statement, including derived tables,
            // subqueries and CTE bodies. Which one the target really means depends on scoping we don't model, so a write
            // needs permission on all of the real ones.
            var bare = name.BaseIdentifier.Value;
            var candidates = scan.AllNamed
                .Where(r => !ReferenceEquals(r, n) && (r.Alias is not null
                    ? r.Alias.Value.Equals(bare, StringComparison.OrdinalIgnoreCase)
                    : r.SchemaObject.SchemaIdentifier is null && r.SchemaObject.DatabaseIdentifier is null
                      && r.SchemaObject.BaseIdentifier.Value.Equals(bare, StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.SchemaObject)
                .ToList();
            if (candidates.Count > 0)
            {
                var real = candidates.Where(c => !IsTemp(c)).ToList();
                foreach (var c in real) yield return c;
                // Only temp tables carry the alias: that is fine when one sits directly in the statement's FROM, but when
                // it is only inside a subquery the target is the real table of that name, which then needs the write.
                if (real.Count == 0 && !TopLevelFromHasTemp(stmt, bare)) yield return name;
                yield break;
            }
        }

        if (!IsTemp(name)) yield return name;
    }

    static bool TopLevelFromHasTemp(TSqlStatement stmt, string bare)
    {
        var from = stmt switch
        {
            UpdateStatement s => s.UpdateSpecification.FromClause,
            DeleteStatement s => s.DeleteSpecification.FromClause,
            _ => null,
        };
        if (from is null) return false;

        bool Match(TableReference r) => r switch
        {
            NamedTableReference t => IsTemp(t.SchemaObject)
                && (t.Alias?.Value ?? t.SchemaObject.BaseIdentifier.Value).Equals(bare, StringComparison.OrdinalIgnoreCase),
            QualifiedJoin j => Match(j.FirstTableReference) || Match(j.SecondTableReference),
            UnqualifiedJoin j => Match(j.FirstTableReference) || Match(j.SecondTableReference),
            JoinParenthesisTableReference j => Match(j.Join),
            _ => false,
        };
        return from.TableReferences.Any(Match);
    }

    // ---- EXEC and dynamic SQL ---------------------------------------------------------------------

    /// <summary>
    /// <c>line</c> is the line in the script being classified. Dynamic SQL is classified recursively and returned as
    /// <see cref="ClassifiedStatement.Nested"/>; anything that can't be read exactly is never allowed.
    /// </summary>
    static ClassifiedStatement ClassifyExecute(ExecuteSpecification spec, int line, int depth)
    {
        ClassifiedStatement Stop(string reason) => new(line, "EXEC", [], null, reason, []);

        if (spec.LinkedServer is not null) return Stop("Linked servers can't be used");
        if (spec.ExecuteContext is not null) return Stop("EXECUTE ... AS can't be used");

        switch (spec.ExecutableEntity)
        {
            case ExecutableStringList list:
            {
                if (list.Parameters.Count > 0) return Stop("This dynamic SQL can't be checked");
                var text = LiteralText(list.Strings);
                return text is null ? Stop("SQL built at run time can't be checked") : Nested(text, line, depth);
            }

            case ExecutableProcedureReference { ProcedureReference: { } pr } call:
            {
                if (pr.ProcedureVariable is not null || pr.ProcedureReference?.Name is not { } name)
                    return Stop("A procedure chosen at run time can't be checked");

                if (name.ServerIdentifier is not null) return Stop("Linked servers can't be used");

                var proc = name.BaseIdentifier.Value;
                var lower = proc.ToLowerInvariant();
                if (IsNeverAllowedProcedure(proc))
                    return Stop($"{proc} can't be used");

                if (lower == "sp_executesql")
                {
                    if (name.DatabaseIdentifier is not null || name.ServerIdentifier is not null
                        || (name.SchemaIdentifier is { } sc && !sc.Value.Equals("sys", StringComparison.OrdinalIgnoreCase)))
                        return Stop("This dynamic SQL can't be checked");

                    var first = call.Parameters.FirstOrDefault(p =>
                        p.Variable is null || p.Variable.Name.Equals("@stmt", StringComparison.OrdinalIgnoreCase));
                    var text = first is null ? null : LiteralText([first.ParameterValue]);
                    return text is null ? Stop("SQL built at run time can't be checked") : Nested(text, line, depth);
                }

                if (lower == "sp_rename" && name.DatabaseIdentifier is null)
                    return new ClassifiedStatement(line, "EXEC sp_rename", [new(null, SqlAction.Ddl, "")], null, null, []);

                // System procedures: only the short allow-list, and only without a database or schema (or dbo).
                var schema = name.SchemaIdentifier?.Value;
                if (name.DatabaseIdentifier is { } dbPart && SystemDatabases.Contains(dbPart.Value))
                    return Stop($"{proc} can't be used");
                if (schema is not null && schema.Equals("sys", StringComparison.OrdinalIgnoreCase))
                    return Stop($"{proc} can't be used");
                if (lower.StartsWith("sp_", StringComparison.Ordinal)
                    && (schema is null || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                    && !(name.DatabaseIdentifier is null && IsSafeSystemCall(lower, call)))
                    return Stop($"{proc} can't be used");

                return new ClassifiedStatement(line, "EXEC " + Name(name),
                    [new(Db(name), SqlAction.Execute, Name(name))], null, null, []);
            }

            default:
                return Stop("This kind of EXEC can't be checked");
        }
    }

    /// <summary>The text of string literals joined together, or null if any part is not a plain literal.</summary>
    static string? LiteralText(IEnumerable<ScalarExpression> parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (!AppendLiteral(p, sb)) return null;
        }
        return sb.ToString();
    }

    static bool AppendLiteral(ScalarExpression e, System.Text.StringBuilder sb)
    {
        switch (e)
        {
            case StringLiteral l: sb.Append(l.Value); return true;
            case ParenthesisExpression p: return AppendLiteral(p.Expression, sb);
            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } b:
                return AppendLiteral(b.FirstExpression, sb) && AppendLiteral(b.SecondExpression, sb);
            default: return false;
        }
    }

    static ClassifiedStatement Nested(string text, int line, int depth)
    {
        if (depth + 1 > MaxDepth) return new(line, "EXEC", [], null, "Dynamic SQL is nested too deeply to check", []);
        var inner = Classify(text, depth + 1);
        if (inner.ParseError is not null)
            return new(line, "EXEC", [], null, "The SQL inside EXEC couldn't be read", []);
        if (inner.Statements.Count == 0)
            return new(line, "EXEC", [], null, null, [new(line, "EXEC", [], null, null, [])]);
        if (inner.Statements.FirstOrDefault(s => s.NeverAllowedReason is not null) is { } bad)
            return new(line, "EXEC", [], null, "The SQL inside EXEC isn't allowed: " + bad.NeverAllowedReason, []);
        return new(line, "EXEC", [], null, null, [.. inner.Statements.Select(s => OnLine(s, line))]);
    }

    /// <summary>Statements inside dynamic SQL are reported on the line of the EXEC that holds them.</summary>
    static ClassifiedStatement OnLine(ClassifiedStatement s, int line) =>
        s with { Line = line, Nested = [.. s.Nested.Select(n => OnLine(n, line))] };

    // ---- Never-allowed statements -----------------------------------------------------------------

    static string? NeverAllowedStatement(TSqlStatement s) => s switch
    {
        BulkInsertBase => "BULK INSERT can't be used",
        BackupStatement => "BACKUP can't be used",
        RestoreStatement => "RESTORE can't be used",
        DbccStatement => "DBCC can't be used",
        KillStatement or KillQueryNotificationSubscriptionStatement or KillStatsJobStatement => "KILL can't be used",
        ShutdownStatement => "SHUTDOWN can't be used",
        CheckpointStatement => "CHECKPOINT can't be used",
        ReconfigureStatement => "RECONFIGURE can't be used",
        GrantStatement or DenyStatement or RevokeStatement => "Changing permissions (GRANT, DENY, REVOKE) can't be used",
        ExecuteAsStatement => "EXECUTE AS can't be used",
        OpenSymmetricKeyStatement or CloseSymmetricKeyStatement or OpenMasterKeyStatement or CloseMasterKeyStatement
            => "Opening or closing keys can't be used",
        WaitForStatement { Statement: not null } => "WAITFOR RECEIVE can't be used",
        _ => null,
    };

    // ---- DDL ---------------------------------------------------------------------------------------

    static readonly string[] NeverAllowedKinds =
    [
        "Database", "Login", "User", "Role", "Key", "Certificate", "Credential", "Authorization", "Endpoint",
        "Audit", "Server", "External", "Availability", "Event", "Queue", "Contract", "MessageType", "Route",
        "Remote", "Resource", "Workload", "Broker",
    ];

    static bool IsDdl(TSqlStatement s)
    {
        var n = s.GetType().Name;
        return n.StartsWithAny("Create", "Alter", "Drop", "Enable", "Disable") || s is UpdateStatisticsStatement;
    }

    static ClassifiedStatement Ddl(TSqlStatement s, int start, int depth)
    {
        var typeName = s.GetType().Name;
        if (typeName.Contains("Assembly", StringComparison.Ordinal))
            return Blocked(s, start, "Assemblies (CLR code) can't be created or changed here");
        if (ServerScopedTrigger(s))
            return Blocked(s, start, "Triggers on the whole server or database can't be created or changed here");
        if (NeverAllowedKinds.Any(k => typeName.Contains(k, StringComparison.Ordinal)))
            return Blocked(s, start, "Changing logins, users, roles, databases or the server can't be done here");

        var names = DdlNames(s).ToList();
        var tableLike = typeName.Contains("Table", StringComparison.Ordinal) || typeName.Contains("Index", StringComparison.Ordinal);
        var requires = new List<Requirement>();
        foreach (var n in names)
        {
            if (tableLike && IsTemp(n)) continue; // temp tables and their indexes are the session's own
            requires.Add(new(Db(n), SqlAction.Ddl, Name(n)));
        }

        // Nothing named (CREATE SCHEMA, ...): still a DDL statement in the current database.
        if (names.Count == 0)
            requires.Add(new(null, SqlAction.Ddl, ""));

        // Every other name in the statement that points into a database (CREATE SYNONYM ... FOR db.s.t, the table a
        // trigger sits on, parameter and return types, ...). Bodies are classified below, not here.
        var others = new DefinitionNames(names);
        s.Accept(others);
        if (others.BlockReason is not null) return Blocked(s, start, others.BlockReason);
        requires.AddRange(others.Requires);

        // A stored body runs later, but it can name other databases. Require them now, like a normal reference.
        var body = new List<ClassifiedStatement>();
        foreach (var stmt in BodyStatements(s)) Walk(stmt, start, true, depth, body);
        if (body.FirstOrDefault(b => b.NeverAllowedReason is not null) is { } bad)
            return Blocked(s, start, "The body of this definition can't be used: " + bad.NeverAllowedReason);
        requires.AddRange(CrossDatabase(body));

        return new ClassifiedStatement(LineOf(s, start), Summary(s, names.FirstOrDefault() is { } f ? Name(f) : null),
            Distinct(requires), null, null, []);
    }

    /// <summary>Triggers on ALL SERVER or on DATABASE, and enabling/disabling them, change more than one database.</summary>
    static bool ServerScopedTrigger(TSqlStatement s) => s switch
    {
        TriggerStatementBody t => t.TriggerObject?.TriggerScope != TriggerScope.Normal,
        EnableDisableTriggerStatement t => t.TriggerObject?.TriggerScope != TriggerScope.Normal,
        _ => false,
    };

    /// <summary>The statements inside a view, procedure, function or trigger definition.</summary>
    static IEnumerable<TSqlStatement> BodyStatements(TSqlStatement s)
    {
        switch (s)
        {
            case ViewStatementBody v:
                if (v.SelectStatement is not null) yield return v.SelectStatement;
                break;
            case ProcedureStatementBody p:
                if (p.StatementList is not null) foreach (var x in p.StatementList.Statements) yield return x;
                break;
            case TriggerStatementBody t:
                if (t.StatementList is not null) foreach (var x in t.StatementList.Statements) yield return x;
                break;
            case CreateSchemaStatement cs:
                if (cs.StatementList is not null) foreach (var x in cs.StatementList.Statements) yield return x;
                break;
            case FunctionStatementBody f:
                if (f.StatementList is not null) foreach (var x in f.StatementList.Statements) yield return x;
                if (f.ReturnType is SelectFunctionReturnType { SelectStatement: { } sel }) yield return sel;
                break;
        }
    }

    /// <summary>Requirements of a body that name another database; the current database needs nothing extra there.</summary>
    static IEnumerable<Requirement> CrossDatabase(IEnumerable<ClassifiedStatement> statements)
    {
        foreach (var st in statements)
        {
            foreach (var r in st.Requires)
                if (r.Database is not null) yield return r;
            foreach (var r in CrossDatabase(st.Nested)) yield return r;
        }
    }

    static IEnumerable<SchemaObjectName> DdlNames(TSqlStatement s)
    {
        switch (s)
        {
            case CreateTableStatement t: return [t.SchemaObjectName];
            case AlterTableStatement t: return [t.SchemaObjectName];
            case CreateIndexStatement t: return [t.OnName];
            case AlterIndexStatement t: return [t.OnName];
            case DropObjectsStatement t: return t.Objects;
            case UpdateStatisticsStatement t: return [t.SchemaObjectName];
            case ProcedureStatementBody t: return t.ProcedureReference?.Name is { } p ? [p] : [];
            case FunctionStatementBody t: return [t.Name];
            case ViewStatementBody t: return [t.SchemaObjectName];
            case TriggerStatementBody t: return [t.Name];
            case CreateSynonymStatement t: return [t.Name];
        }

        var first = new FirstName();
        s.Accept(first);
        return first.Found is { } f ? [f] : [];
    }

    sealed class FirstName : TSqlFragmentVisitor
    {
        public SchemaObjectName? Found;
        public override void Visit(SchemaObjectName node) => Found ??= node;
    }

    // ---- Neutral statements ------------------------------------------------------------------------

    static bool IsNeutral(TSqlStatement s) => s is
        DeclareVariableStatement or DeclareTableVariableStatement or SetVariableStatement or PredicateSetStatement
        or SetRowCountStatement or SetCommandStatement or SetTransactionIsolationLevelStatement or SetTextSizeStatement
        or SetStatisticsStatement or SetOffsetsStatement or SetErrorLevelStatement
        or PrintStatement or RaiseErrorStatement or ThrowStatement
        or BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement or SaveTransactionStatement
        or ReturnStatement or BreakStatement or ContinueStatement or GoToStatement or LabelStatement
        or WaitForStatement or DeclareCursorStatement or OpenCursorStatement or FetchCursorStatement
        or CloseCursorStatement or DeallocateCursorStatement or RevertStatement;

    // ---- Collector ---------------------------------------------------------------------------------

    /// <summary>
    /// Catches database and server parts wherever they sit. Specific visitors (in <see cref="Collector"/>) record a name in
    /// <see cref="Handled"/> when they have already dealt with it; every other name that carries a database part still
    /// becomes a requirement, and a server part always blocks, so node types nobody thought of fail closed.
    /// </summary>
    abstract class NameVisitor : TSqlFragmentVisitor
    {
        protected readonly HashSet<TSqlFragment> Handled = [];
        public readonly List<Requirement> Requires = [];
        public string? BlockReason;

        protected void Linked() => BlockReason ??= "Linked servers can't be used";

        // Always required, even for the current database, so object-level checks see the name.
        protected void Read(SchemaObjectName name)
        {
            Handled.Add(name);
            if (name.ServerIdentifier is not null) Linked();
            if (!IsTemp(name)) Requires.Add(new(Db(name), SqlAction.Select, Name(name)));
        }

        public override void Visit(SchemaObjectName node)
        {
            if (Handled.Contains(node)) return;
            if (node.ServerIdentifier is not null) Linked();
            if (node.DatabaseIdentifier is not null && !IsTemp(node))
                Requires.Add(new(Db(node), SqlAction.Select, Name(node)));
        }

        public override void Visit(MultiPartIdentifier node)
        {
            if (Handled.Contains(node)) return;
            var ids = node.Identifiers;
            if (ids.Count >= 4) Linked();
            else if (ids.Count == 3)
                Requires.Add(new(ids[0].Value, SqlAction.Select, string.Join('.', ids.Select(i => i.Value))));
        }

        // A column may be written db.schema.table.column, but its table is read (and checked) in FROM.
        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier is { } m)
            {
                Handled.Add(m);
                if (m.Identifiers.Count >= 5) Linked();
            }
            base.ExplicitVisit(node);
        }

        // db.schema.fn(1) runs a function in another database; server.db.schema.fn(1) on another server.
        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier: { } m })
            {
                Handled.Add(m);
                // The function name itself is not in this list: [db, schema] or [server, db, schema].
                if (m.Identifiers.Count >= 3) Linked();
                else if (m.Identifiers.Count == 2)
                    Requires.Add(new(m.Identifiers[0].Value, SqlAction.Select, string.Join('.', m.Identifiers.Select(i => i.Value)) + "." + node.FunctionName.Value));
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FullTextTableReference node)
        {
            Read(node.TableName);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SemanticTableReference node)
        {
            Read(node.TableName);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ChangeTableChangesTableReference node)
        {
            Read(node.Target);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ChangeTableVersionTableReference node)
        {
            Read(node.Target);
            base.ExplicitVisit(node);
        }
    }

    /// <summary>The names in a definition other than the ones already handled, and not looking inside its body.</summary>
    sealed class DefinitionNames : NameVisitor
    {
        public DefinitionNames(IEnumerable<SchemaObjectName> own) { foreach (var n in own) Handled.Add(n); }

        public override void ExplicitVisit(StatementList node) { }
        public override void ExplicitVisit(SelectStatement node) { }
    }

    /// <summary>Gathers the reads under a fragment, never looking inside procedure/function/view/trigger bodies.</summary>
    sealed class Collector : NameVisitor
    {
        readonly HashSet<TableReference> _exclude;
        public readonly HashSet<string> Ctes = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<SchemaObjectName> CteTables = [];
        public readonly List<NamedTableReference> AllNamed = [];
        bool _inCte;

        public Collector(TSqlFragment root, IEnumerable<TableReference>? exclude = null)
        {
            _exclude = exclude is null ? [] : [.. exclude];
            // CTE names first, so a reference to one is not mistaken for a table.
            root.Accept(new CteNames(Ctes));
        }

        sealed class CteNames(HashSet<string> names) : TSqlFragmentVisitor
        {
            public override void Visit(CommonTableExpression node) => names.Add(node.ExpressionName.Value);
        }

        // Definitions run later; their bodies are classified by Ddl, not read now.
        public override void ExplicitVisit(CreateProcedureStatement node) { }
        public override void ExplicitVisit(AlterProcedureStatement node) { }
        public override void ExplicitVisit(CreateFunctionStatement node) { }
        public override void ExplicitVisit(AlterFunctionStatement node) { }
        public override void ExplicitVisit(CreateViewStatement node) { }
        public override void ExplicitVisit(AlterViewStatement node) { }
        public override void ExplicitVisit(CreateTriggerStatement node) { }
        public override void ExplicitVisit(AlterTriggerStatement node) { }

        // The procedure name is checked as an EXEC (Execute), not as a read.
        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is { } n)
            {
                Handled.Add(n);
                if (n.ServerIdentifier is not null) Linked();
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommonTableExpression node)
        {
            var was = _inCte;
            _inCte = true;
            base.ExplicitVisit(node);
            _inCte = was;
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            AllNamed.Add(node);
            var name = node.SchemaObject;
            Handled.Add(name);
            if (name.ServerIdentifier is not null) Linked();

            if (_exclude.Contains(node) || IsTemp(name)) return;
            var isCte = name.SchemaIdentifier is null && name.DatabaseIdentifier is null && Ctes.Contains(name.BaseIdentifier.Value);
            if (isCte) return;

            if (_inCte) CteTables.Add(name);
            Requires.Add(new(Db(name), SqlAction.Select, Name(name)));
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            Read(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NextValueForExpression node)
        {
            Handled.Add(node.SequenceName);
            if (node.SequenceName.ServerIdentifier is not null) Linked();
            Requires.Add(new(Db(node.SequenceName), SqlAction.Update, Name(node.SequenceName)));
            base.ExplicitVisit(node);
        }

        // External data sources stay blocked even before the full never-allowed list lands.
        public override void ExplicitVisit(OpenQueryTableReference node) => BlockReason ??= "OPENQUERY can't be used";
        public override void ExplicitVisit(OpenRowsetTableReference node) => BlockReason ??= "OPENROWSET can't be used";
        public override void ExplicitVisit(BulkOpenRowset node) => BlockReason ??= "OPENROWSET can't be used";
        public override void ExplicitVisit(InternalOpenRowset node) => BlockReason ??= "OPENROWSET can't be used";
        public override void ExplicitVisit(AdHocTableReference node) => BlockReason ??= "OPENDATASOURCE can't be used";
    }

    // ---- Small helpers -----------------------------------------------------------------------------

    static ClassifiedStatement Blocked(TSqlFragment f, int start, string reason) =>
        new(LineOf(f, start), f is TSqlStatement s ? Summary(s, null) : "statement", [], null, reason, []);

    static string? Db(SchemaObjectName n) => n.DatabaseIdentifier?.Value;

    static string Name(SchemaObjectName n) => string.Join('.', n.Identifiers.Select(i => i.Value));

    static bool IsTemp(SchemaObjectName n) => n.BaseIdentifier.Value.StartsWith('#');

    static string? Describe(TableReference t) => t is NamedTableReference n ? Name(n.SchemaObject) : null;

    static IReadOnlyList<Requirement> Distinct(IEnumerable<Requirement> r) => r.Distinct().ToList();

    static string Summary(TSqlStatement s, string? obj)
    {
        var words = CamelWords().Replace(s.GetType().Name.Replace("Statement", ""), " $1").Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(obj) ? words : words + " " + obj;
    }

    [GeneratedRegex("(?<!^)([A-Z])")]
    private static partial Regex CamelWords();

    static bool StartsWithAny(this string s, params string[] prefixes) =>
        prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal));
}
