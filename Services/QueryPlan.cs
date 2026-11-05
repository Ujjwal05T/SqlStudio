using System.Globalization;
using System.Xml.Linq;

namespace SqlStudio.Services;

/// <summary>A problem SQL Server flagged on a step or a statement. <see cref="Kind"/>: convert-seek, convert-estimate, spill, nojoin or nostats.</summary>
public sealed record PlanWarning(string Kind, string Detail);

/// <summary>An index SQL Server says would make the statement cheaper. Column names keep their brackets, as in the plan.</summary>
public sealed record MissingIndex(double Impact, string Table, IReadOnlyList<string> Equality, IReadOnlyList<string> Inequality, IReadOnlyList<string> Include);

/// <summary>One step of an execution plan, with the cost it adds on top of the steps feeding it.</summary>
public sealed class PlanOperator
{
    public required int NodeId { get; init; }
    public required string PhysicalOp { get; init; }
    public required string LogicalOp { get; init; }
    public double EstimateRows { get; init; }

    /// <summary>The estimated cost of this step and everything below it.</summary>
    public double SubtreeCost { get; init; }

    /// <summary>The estimated cost of this step alone: its subtree cost minus its inputs' subtree costs.</summary>
    public double OperatorCost { get; set; }

    /// <summary>Share of the statement's total cost, 0 to 1.</summary>
    public double Percent { get; set; }

    /// <summary>Rows the step really produced, summed over all threads. Only in an actual plan.</summary>
    public long? ActualRows { get; init; }

    /// <summary>The longest any thread spent in this step. Only in an actual plan.</summary>
    public long? ActualElapsedMs { get; init; }

    /// <summary>The table or index it reads, e.g. dbo.Orders.IX_Orders_CustomerId.</summary>
    public string? Target { get; init; }

    /// <summary>The table it reads, quoted for use in a script, e.g. [dbo].[Orders].</summary>
    public string? TableName { get; init; }

    /// <summary>The condition a scan applies to every row it reads, when there is one.</summary>
    public string? Filter { get; init; }

    /// <summary>A step that fetches the remaining columns of a row found through another index (a key or RID lookup).</summary>
    public bool IsLookup { get; init; }

    public IReadOnlyList<string> OutputColumns { get; init; } = [];
    public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];

    /// <summary>How often the step ran, summed over threads. Only in an actual plan.</summary>
    public long? ActualExecutions { get; init; }

    /// <summary>How often the server expected to run it: once, plus rebinds and rewinds.</summary>
    public double EstimateExecutions { get; init; } = 1;

    public bool IsScan => !IsLookup && PhysicalOp is "Table Scan" or "Clustered Index Scan" or "Index Scan";

    public List<PlanOperator> Children { get; } = [];

    public IEnumerable<PlanOperator> SelfAndDescendants()
    {
        yield return this;
        foreach (var d in Children.SelectMany(c => c.SelfAndDescendants()))
            yield return d;
    }
}

public sealed record PlanStatement(int Index, string Text, PlanOperator Root)
{
    public IReadOnlyList<MissingIndex> MissingIndexes { get; init; } = [];

    /// <summary>Warnings that belong to the whole statement rather than to one step.</summary>
    public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];

    public IEnumerable<PlanOperator> Operators => Root.SelfAndDescendants();
    public double TotalCost => Root.SubtreeCost;
    public PlanOperator MostExpensive => Operators.MaxBy(o => o.OperatorCost)!;

    /// <summary>"1. SELECT …" for a picker: the statement on one line, cut short.</summary>
    public string Label
    {
        get
        {
            var oneLine = string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return $"{Index}. {(oneLine.Length > 70 ? oneLine[..70] + "…" : oneLine)}";
        }
    }
}

/// <summary>A SQL Server execution plan (ShowPlan XML), estimated or actual, as a tree of operators per statement.</summary>
public sealed class QueryPlan
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public required IReadOnlyList<PlanStatement> Statements { get; init; }

    /// <summary>True when the plan carries what really happened (row counts), not only the estimates.</summary>
    public bool IsActual => Statements.Any(s => s.Operators.Any(o => o.ActualRows is not null));

    public static QueryPlan Parse(string xml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidOperationException("This is not a valid execution plan: " + ex.Message, ex);
        }

        var statements = new List<PlanStatement>();
        // A plan inside a RelOp belongs to a function called by the statement, not to a statement of its own.
        foreach (var plan in doc.Descendants(Ns + "QueryPlan").Where(q => !q.Ancestors(Ns + "RelOp").Any()))
        {
            if (plan.Element(Ns + "RelOp") is not { } rel)
                continue;
            var root = ReadOperator(rel);
            Distribute(root, root.SubtreeCost);
            statements.Add(new PlanStatement(statements.Count + 1, (string?)plan.Parent?.Attribute("StatementText") ?? "", root)
            {
                MissingIndexes = ReadMissingIndexes(plan),
                Warnings = ReadWarnings(plan.Element(Ns + "Warnings")),
            });
        }

        if (statements.Count == 0)
            throw new InvalidOperationException("This plan has no operators to show. Only statements that can be planned have one; SET and USE do not.");
        return new QueryPlan { Statements = statements };
    }

    static PlanOperator ReadOperator(XElement rel)
    {
        // Rows come from RunTimeCountersPerThread: one entry per thread, so a parallel step has several.
        var counters = rel.Element(Ns + "RunTimeInformation")?.Elements(Ns + "RunTimeCountersPerThread").ToList() ?? [];
        // A key lookup is an index seek on the clustered index marked as a lookup. Name it for what it does.
        var isLookup = rel.Elements().Any(e => IsTrue(e, "Lookup")) || ((string?)rel.Attribute("PhysicalOp"))?.Contains("Lookup") == true;
        var physical = (string?)rel.Attribute("PhysicalOp") ?? "Operator";
        if (isLookup && !physical.Contains("Lookup"))
            physical = physical.StartsWith("Clustered", StringComparison.Ordinal) ? "Key Lookup (Clustered)" : "Key Lookup";

        var op = new PlanOperator
        {
            NodeId = (int?)rel.Attribute("NodeId") ?? 0,
            PhysicalOp = physical,
            LogicalOp = (string?)rel.Attribute("LogicalOp") ?? "",
            EstimateRows = Number(rel, "EstimateRows"),
            SubtreeCost = Number(rel, "EstimatedTotalSubtreeCost"),
            ActualRows = counters.Count == 0 ? null : counters.Sum(c => (long?)Number(c, "ActualRows") ?? 0),
            ActualExecutions = counters.Count == 0 ? null : counters.Sum(c => (long)Number(c, "ActualExecutions")),
            EstimateExecutions = 1 + Number(rel, "EstimateRebinds") + Number(rel, "EstimateRewinds"),
            IsLookup = isLookup,
            TableName = TableOf(rel),
            Filter = FilterOf(rel),
            OutputColumns = rel.Element(Ns + "OutputList")?.Elements(Ns + "ColumnReference")
                .Select(c => ((string?)c.Attribute("Column") ?? "").Trim('[', ']')).Where(c => c.Length > 0).ToList() ?? [],
            Warnings = ReadWarnings(rel.Element(Ns + "Warnings")),
            ActualElapsedMs = counters.Count == 0 ? null : counters.Max(c => (long)Number(c, "ActualElapsedms")),
            Target = TargetOf(rel),
        };

        // The inputs are the nearest RelOps below this one, wherever the operator keeps them.
        foreach (var child in rel.Descendants(Ns + "RelOp").Where(d => d.Ancestors(Ns + "RelOp").First() == rel))
            op.Children.Add(ReadOperator(child));
        return op;
    }

    static string? TargetOf(XElement rel)
    {
        var target = rel.Elements().SelectMany(e => e.Elements(Ns + "Object")).FirstOrDefault();
        if (target is null)
            return null;
        var parts = new[] { "Schema", "Table", "Index" }.Select(a => ((string?)target.Attribute(a))?.Trim('[', ']')).Where(p => !string.IsNullOrEmpty(p));
        return string.Join('.', parts);
    }

    static string? TableOf(XElement rel)
    {
        var target = rel.Elements().SelectMany(e => e.Elements(Ns + "Object")).FirstOrDefault();
        var table = (string?)target?.Attribute("Table");
        if (string.IsNullOrEmpty(table))
            return null;
        var schema = (string?)target!.Attribute("Schema");
        return string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
    }

    static string? FilterOf(XElement rel) => rel.Elements().SelectMany(e => e.Elements(Ns + "Predicate"))
        .Select(p => (string?)p.Descendants(Ns + "ScalarOperator").FirstOrDefault()?.Attribute("ScalarString"))
        .FirstOrDefault(s => !string.IsNullOrEmpty(s));

    static bool IsTrue(XElement e, string attribute) => ((string?)e.Attribute(attribute)) is "1" or "true" or "True";

    static List<PlanWarning> ReadWarnings(XElement? warnings)
    {
        var list = new List<PlanWarning>();
        if (warnings is null)
            return list;
        if (IsTrue(warnings, "NoJoinPredicate"))
            list.Add(new PlanWarning("nojoin", ""));
        foreach (var convert in warnings.Elements(Ns + "PlanAffectingConvert"))
            list.Add(new PlanWarning(
                (string?)convert.Attribute("ConvertIssue") == "Seek Plan" ? "convert-seek" : "convert-estimate",
                (string?)convert.Attribute("Expression") ?? ""));
        if (IsTrue(warnings, "SpillOccurred") || warnings.Elements().Any(e => e.Name.LocalName is "SpillToTempDb" or "SortSpillDetails" or "HashSpillDetails"))
            list.Add(new PlanWarning("spill", ""));
        if (warnings.Element(Ns + "ColumnsWithNoStatistics") is { } noStats)
            list.Add(new PlanWarning("nostats", string.Join(", ", noStats.Elements(Ns + "ColumnReference")
                .Select(c => ((string?)c.Attribute("Column") ?? "").Trim('[', ']')).Where(c => c.Length > 0))));
        return list;
    }

    static List<MissingIndex> ReadMissingIndexes(XElement plan)
    {
        var list = new List<MissingIndex>();
        foreach (var group in plan.Element(Ns + "MissingIndexes")?.Elements(Ns + "MissingIndexGroup") ?? [])
            foreach (var index in group.Elements(Ns + "MissingIndex"))
            {
                List<string> Columns(string usage) => index.Elements(Ns + "ColumnGroup").Where(g => (string?)g.Attribute("Usage") == usage)
                    .SelectMany(g => g.Elements(Ns + "Column")).Select(c => (string?)c.Attribute("Name") ?? "").Where(n => n.Length > 0).ToList();
                list.Add(new MissingIndex(Number(group, "Impact"), $"{(string?)index.Attribute("Schema")}.{(string?)index.Attribute("Table")}",
                    Columns("EQUALITY"), Columns("INEQUALITY"), Columns("INCLUDE")));
            }
        return list;
    }

    static double Number(XElement e, string attribute) =>
        double.TryParse((string?)e.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // A step's own cost is what its subtree costs beyond its inputs. Estimates can leave it slightly negative; that reads as zero.
    static void Distribute(PlanOperator op, double total)
    {
        op.OperatorCost = Math.Max(0, op.SubtreeCost - op.Children.Sum(c => c.SubtreeCost));
        op.Percent = total > 0 ? op.OperatorCost / total : 0;
        foreach (var child in op.Children)
            Distribute(child, total);
    }

    /// <summary>
    /// The XML as saved to a .sqlplan file. SQL Server can declare UTF-16 while the text is written as UTF-8;
    /// a mismatched declaration stops other tools from opening the file.
    /// </summary>
    public static string ForFile(string xml)
    {
        var declarationEnd = xml.StartsWith("<?xml", StringComparison.Ordinal) ? xml.IndexOf("?>", StringComparison.Ordinal) : -1;
        if (declarationEnd < 0)
            return xml;
        var declaration = xml[..declarationEnd].Replace("utf-16", "utf-8", StringComparison.OrdinalIgnoreCase);
        return declaration + xml[declarationEnd..];
    }
}
