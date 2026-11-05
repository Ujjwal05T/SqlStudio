namespace SqlStudio.Services;

/// <summary>
/// Merges the ER models of the two sides of a schema compare into one picture. A table or column only in the source is
/// green, only in the target red, present in both but different amber. Table-level status comes from the compare result,
/// so the picture and the list always agree.
/// </summary>
public static class SchemaDiffDiagram
{
    public static ErModel Build(ErModel source, ErModel target, IReadOnlyList<SchemaDifference> differences)
    {
        var status = differences.Where(d => d.Kind == "Table")
            .ToDictionary(d => d.FullName, d => d.Status, StringComparer.OrdinalIgnoreCase);
        var sourceTables = source.Tables.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var targetTables = target.Tables.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);

        var tables = new List<ErTable>();
        foreach (var s in source.Tables)
            tables.Add(targetTables.TryGetValue(s.Id, out var t) ? Merge(s, t, status) : s with { Change = DiffStatus.OnlyInSource });
        foreach (var t in target.Tables.Where(t => !sourceTables.ContainsKey(t.Id)))
            tables.Add(t with { Change = DiffStatus.OnlyInTarget });

        return new ErModel(tables, MergeRelations(source.Relations, target.Relations));
    }

    static ErTable Merge(ErTable source, ErTable target, Dictionary<string, DiffStatus> status)
    {
        var targetColumns = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var columns = new List<ErColumn>();
        foreach (var s in source.Columns)
        {
            if (!targetColumns.TryGetValue(s.Name, out var t))
                columns.Add(s with { Change = DiffStatus.OnlyInSource });
            else if (s.Type != t.Type || s.Nullable != t.Nullable)
                columns.Add(s with { Change = DiffStatus.Different, DisplayOverride = $"{s.DisplayType} (target: {t.DisplayType})" });
            else
                columns.Add(s);
        }
        var sourceNames = source.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        columns.AddRange(target.Columns.Where(c => !sourceNames.Contains(c.Name)).Select(c => c with { Change = DiffStatus.OnlyInTarget }));

        // A table can differ only in indexes or constraints, which the compare list reports and the columns do not show.
        DiffStatus? change = status.TryGetValue(source.Id, out var s2) ? s2 : columns.Any(c => c.Change is not null) ? DiffStatus.Different : null;
        return source with { Columns = columns, Change = change };
    }

    static List<ErRelation> MergeRelations(IReadOnlyList<ErRelation> source, IReadOnlyList<ErRelation> target)
    {
        static string Key(ErRelation r) => $"{r.FromId}>{r.ToId}:{string.Join(',', r.FromColumns)}>{string.Join(',', r.ToColumns)}".ToLowerInvariant();
        var targetKeys = target.Select(Key).ToHashSet();
        var sourceKeys = source.Select(Key).ToHashSet();
        return source.Select(r => targetKeys.Contains(Key(r)) ? r : r with { Change = DiffStatus.OnlyInSource })
            .Concat(target.Where(r => !sourceKeys.Contains(Key(r))).Select(r => r with { Change = DiffStatus.OnlyInTarget }))
            .ToList();
    }

    /// <summary>Keeps changed tables, plus the unchanged tables a changed relationship connects to, so nothing dangles.</summary>
    public static ErModel OnlyChanged(ErModel model)
    {
        var keep = model.Tables.Where(t => t.Change is not null).Select(t => t.Id).ToHashSet();
        foreach (var r in model.Relations.Where(r => r.Change is not null))
        {
            keep.Add(r.FromId);
            keep.Add(r.ToId);
        }
        return new ErModel(
            model.Tables.Where(t => keep.Contains(t.Id)).ToList(),
            model.Relations.Where(r => keep.Contains(r.FromId) && keep.Contains(r.ToId)).ToList());
    }
}
