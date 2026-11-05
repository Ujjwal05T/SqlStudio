using System.Globalization;

namespace SqlStudio.Services;

/// <summary>
/// Turns one statement's operator tree into an ER-style model so the plan can be laid out, drawn and exported with
/// the same code as the diagrams. The colours that mark changes there mark cost here.
/// </summary>
public static class PlanDiagram
{
    public const double WarmShare = 0.15;
    public const double HotShare = 0.40;

    public static readonly IReadOnlyList<(DiffStatus Change, string Label)> Legend =
    [
        (DiffStatus.Different, "15% or more of the cost"),
        (DiffStatus.OnlyInTarget, "40% or more of the cost"),
    ];

    const string Group = "plan";

    /// <summary>The id of the box drawn for an operator (see <see cref="ErTable.Id"/>), which relationships refer to.</summary>
    public static string Id(PlanOperator op) => $"{Group}.{op.NodeId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Warm (amber) from 15% of the statement's cost, hot (red) from 40%.</summary>
    public static DiffStatus? HeatOf(double share) => share >= HotShare ? DiffStatus.OnlyInTarget : share >= WarmShare ? DiffStatus.Different : null;

    /// <summary>One box per operator; an arrow runs from each step to the step that consumes its rows, so the final step is leftmost.</summary>
    public static ErModel Build(PlanStatement statement)
    {
        var tables = new List<ErTable>();
        var relations = new List<ErRelation>();
        foreach (var op in statement.Operators)
        {
            var heat = HeatOf(op.Percent);
            var rows = new List<ErColumn> { Row("Cost", Share(op.Percent), heat) };
            rows.Add(Row("Rows", op.ActualRows is { } actual ? $"{Count(actual)} (est {Count(op.EstimateRows)})" : $"est {Count(op.EstimateRows)}"));
            if (op.ActualElapsedMs is { } ms)
                rows.Add(Row("Time", $"{ms:N0} ms"));
            if (op.Target is { Length: > 0 } target)
                rows.Add(Row("Object", target.Length > 44 ? target[..43] + "…" : target));

            tables.Add(new ErTable(Group, op.NodeId.ToString(CultureInfo.InvariantCulture), rows, Change: heat, TitleOverride: op.PhysicalOp));
            relations.AddRange(op.Children.Select(c => new ErRelation($"{c.NodeId} to {op.NodeId}", Id(c), Id(op), [""], [""])));
        }
        return new ErModel(tables, relations);
    }

    static ErColumn Row(string name, string value, DiffStatus? heat = null) => new(name, value, false, false, false, heat);

    public static string Share(double share) => share switch
    {
        <= 0 => "0%",
        < 0.005 => "<1%",
        _ => $"{share * 100:0}%",
    };

    static string Count(double value) =>
        value is < 10 && value != Math.Floor(value) ? value.ToString("0.##", CultureInfo.InvariantCulture) : value.ToString("N0", CultureInfo.InvariantCulture);
}
