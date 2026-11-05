using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlStudio.Services;

public enum PlanSeverity { Low, Medium, High }

/// <summary>One thing worth changing, in plain words. <see cref="Script"/> is ready to copy when the fix is a statement.</summary>
public sealed record PlanFinding(PlanSeverity Severity, string Title, string Detail, string? Step = null, string? Script = null)
{
    public bool HasScript => Script is not null;
    public bool HasStep => Step is not null;
    public string SeverityLabel => Severity.ToString();
    public bool IsHigh => Severity == PlanSeverity.High;
    public bool IsMedium => Severity == PlanSeverity.Medium;
    public bool IsLow => Severity == PlanSeverity.Low;
}

public sealed record PlanAdvice(string Verdict, IReadOnlyList<PlanFinding> Findings)
{
    public bool HasFindings => Findings.Count > 0;
}

/// <summary>
/// Reads a statement's plan for the patterns that usually mean wasted work: indexes SQL Server asks for, conversions that
/// block an index seek, scans that filter after reading everything, lookups, spills, joins without a condition and
/// estimates far from what happened. It reports only what the plan shows, and says so when there is nothing to fix.
/// </summary>
public static class PlanAdvisor
{
    /// <summary>Below this estimated cost a statement is too small for tuning to be noticeable.</summary>
    public const double CheapCost = 0.05;

    const double MinRowMiss = 100;
    const double MissFactor = 10;

    public static PlanAdvice Analyze(PlanStatement statement)
    {
        var findings = new List<(PlanFinding Finding, double Weight)>();
        void Add(PlanFinding f, double weight) => findings.Add((f, weight));

        foreach (var index in statement.MissingIndexes)
            Add(MissingIndexFinding(index), index.Impact / 100);
        foreach (var w in statement.Warnings)
            Add(WarningFinding(w, null), 0.5);

        foreach (var op in statement.Operators)
        {
            foreach (var w in op.Warnings)
                Add(WarningFinding(w, op), Math.Max(op.Percent, 0.3));
            if (ScanFinding(op) is { } scan)
                Add(scan, op.Percent);
            if (LookupFinding(statement, op) is { } lookup)
                Add(lookup, op.Percent);
            if (op.PhysicalOp == "Sort" && op.Percent >= PlanDiagram.WarmShare)
                Add(SortFinding(op), op.Percent);
            if (EstimateFinding(op) is { } miss)
                Add(miss, op.Percent);
        }

        var ordered = findings.OrderByDescending(f => f.Finding.Severity).ThenByDescending(f => f.Weight).Select(f => f.Finding).ToList();
        return new PlanAdvice(Verdict(statement, ordered), ordered);
    }

    static string Verdict(PlanStatement statement, List<PlanFinding> findings)
    {
        if (findings.Count == 0)
            return statement.TotalCost < CheapCost
                ? $"Nothing to improve. This statement is already cheap (estimated cost {statement.TotalCost.ToString("0.####", CultureInfo.InvariantCulture)})."
                : "No clear problem in this plan. The cost is spread over the steps shown, so a faster result would need a change to the query or the design, not a quick fix.";

        var impact = statement.MissingIndexes.Select(i => i.Impact).DefaultIfEmpty(0).Max();
        var count = $"{findings.Count} thing{(findings.Count == 1 ? "" : "s")} worth looking at.";
        return impact >= 5
            ? $"{count} SQL Server estimates the index below could cut this query's cost by about {impact:0}%."
            : count;
    }

    static PlanFinding MissingIndexFinding(MissingIndex index)
    {
        var keys = index.Equality.Concat(index.Inequality).ToList();
        var severity = index.Impact >= 50 ? PlanSeverity.High : index.Impact >= 20 ? PlanSeverity.Medium : PlanSeverity.Low;
        var script = keys.Count == 0 ? null
            : $"CREATE NONCLUSTERED INDEX {IndexName(index.Table, keys)}\n    ON {index.Table} ({string.Join(", ", keys)})" +
              (index.Include.Count > 0 ? $"\n    INCLUDE ({string.Join(", ", index.Include)});" : ";");
        return new PlanFinding(severity, $"Missing index on {index.Table}",
            $"SQL Server estimates this index would cut the cost of this query by about {index.Impact:0}%. " +
            "It is a suggestion from the optimizer, not a rule: check that an existing index could not be extended instead, and remember every index slows down inserts and updates.",
            Script: script);
    }

    /// <summary>IX_Table_Key1_Key2, limited to letters, digits and underscores and to a sensible length.</summary>
    public static string IndexName(string table, IEnumerable<string> keys)
    {
        var raw = "IX_" + string.Join('_', new[] { table.Split('.').Last() }.Concat(keys));
        var clean = Regex.Replace(raw.Replace("[", "").Replace("]", ""), "[^A-Za-z0-9_]+", "_").Trim('_');
        return "[" + (clean.Length > 100 ? clean[..100] : clean) + "]";
    }

    static PlanFinding WarningFinding(PlanWarning w, PlanOperator? op)
    {
        var step = op is null ? null : StepOf(op);
        return w.Kind switch
        {
            "convert-seek" => new PlanFinding(PlanSeverity.High, "Implicit conversion blocks an index seek",
                $"SQL Server has to convert a value to compare it ({w.Detail}), so it cannot seek on the index and reads far more rows. " +
                "Make the two sides the same type: give the parameter or literal the column's type (for a varchar column use a varchar value, for nvarchar use N'…').", step),
            "convert-estimate" => new PlanFinding(PlanSeverity.Medium, "Implicit conversion hurts row estimates",
                $"A conversion ({w.Detail}) makes SQL Server's guess about how many rows match unreliable, which can lead to a poor plan. Match the types on both sides.", step),
            "spill" => new PlanFinding(op is { Percent: >= PlanDiagram.WarmShare } ? PlanSeverity.High : PlanSeverity.Medium, "Spilled to tempdb",
                "This step did not get enough memory and wrote data to disk, which is much slower. It usually follows an underestimate of the rows: update the statistics on the tables involved, or reduce the rows before this step.", step),
            "nojoin" => new PlanFinding(PlanSeverity.High, "Join without a condition",
                "Two inputs are combined with no join condition, so every row meets every row. This is usually a missing or misspelt ON clause.", step),
            "nostats" => new PlanFinding(PlanSeverity.Medium, "Missing statistics",
                $"There are no statistics on {w.Detail}, so SQL Server is guessing row counts. Turn on AUTO_CREATE_STATISTICS for the database, or create statistics on those columns.", step),
            _ => new PlanFinding(PlanSeverity.Low, "Warning in the plan", w.Detail, step),
        };
    }

    static PlanFinding? ScanFinding(PlanOperator op)
    {
        // A scan with no condition reads everything on purpose (SELECT * with no WHERE); only a filtered scan can be avoided.
        if (!op.IsScan || op.Filter is null || op.Percent < PlanDiagram.WarmShare)
            return null;
        var what = op.TableName ?? op.Target ?? "the table";
        return new PlanFinding(op.Percent >= PlanDiagram.HotShare ? PlanSeverity.High : PlanSeverity.Medium,
            $"Scans all of {what}, then filters",
            $"This step reads every row and applies the condition afterwards ({op.Filter}). If the condition keeps only a few rows, " +
            "an index on the columns it filters on lets SQL Server seek straight to them.", StepOf(op));
    }

    static PlanFinding? LookupFinding(PlanStatement statement, PlanOperator op)
    {
        if (!op.IsLookup || op.Percent < PlanDiagram.WarmShare)
            return null;
        var parent = statement.Operators.FirstOrDefault(o => o.Children.Contains(op));
        var index = parent?.Children.FirstOrDefault(c => c != op && !c.IsLookup)?.Target;
        var runs = op.ActualExecutions ?? (long)op.EstimateExecutions;
        var columns = op.OutputColumns.Count > 0 ? string.Join(", ", op.OutputColumns) : "the other columns the query needs";
        return new PlanFinding(op.Percent >= PlanDiagram.HotShare ? PlanSeverity.High : PlanSeverity.Medium,
            "Key lookup for every row found",
            $"After finding rows in {index ?? "an index"}, SQL Server goes back to the table for each one ({runs:N0} time{(runs == 1 ? "" : "s")}) to fetch {columns}. " +
            "Adding those columns to the index as INCLUDE columns lets it answer the query alone. Only worth it when the query runs often and the columns are small.",
            StepOf(op));
    }

    static PlanFinding SortFinding(PlanOperator op) => new(PlanSeverity.Low, "Sorting costs a large share",
        $"The sort takes {PlanDiagram.Share(op.Percent)} of the statement's cost. An index that already keeps the rows in the ORDER BY order, or filtering rows before the sort, avoids it. " +
        "If the sort is needed, nothing is wrong.", StepOf(op));

    static PlanFinding? EstimateFinding(PlanOperator op)
    {
        if (op.ActualRows is not { } actual || op.Percent < PlanDiagram.WarmShare)
            return null;
        var perRun = actual / Math.Max(1.0, op.ActualExecutions ?? 1);
        var (low, high) = (Math.Min(perRun, op.EstimateRows), Math.Max(perRun, op.EstimateRows));
        if (high - low < MinRowMiss || high / Math.Max(low, 1) < MissFactor)
            return null;
        return new PlanFinding(PlanSeverity.Medium, "SQL Server guessed the row count badly",
            $"It expected about {op.EstimateRows.ToString("N0", CultureInfo.InvariantCulture)} rows but each run produced about {perRun.ToString("N0", CultureInfo.InvariantCulture)}. " +
            "A wrong guess leads to the wrong join type or too little memory. Out-of-date statistics are the usual cause; if it only happens for some parameter values, it is parameter sniffing.",
            StepOf(op), op.TableName is { } table ? $"UPDATE STATISTICS {table};" : null);
    }

    static string StepOf(PlanOperator op) => $"{op.PhysicalOp} · step {op.NodeId}";
}
