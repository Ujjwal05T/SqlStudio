namespace SqlStudio.Services;

public readonly record struct ErPoint(double X, double Y);

public sealed record ErNode(ErTable Table, double X, double Y, double Width, double Height, double TypeOffset)
{
    public double Right => X + Width;

    /// <summary>The vertical centre of a column's row, where relationship lines attach.</summary>
    public double RowY(string column)
    {
        var index = Math.Max(0, Table.Columns.ToList().FindIndex(c => c.Name == column));
        return Y + ErLayout.HeaderHeight + ErLayout.RowHeight * (index + 0.5);
    }
}

/// <summary>A relationship line drawn as one cubic curve from the child's key column to the parent's, with an arrow at the parent.</summary>
public sealed record ErEdge(ErRelation Relation, ErPoint Start, ErPoint C1, ErPoint C2, ErPoint End, IReadOnlyList<ErPoint> Arrow);

/// <summary>A colour key entry under the diagram, placed by the layout so the screen, SVG and PNG agree.</summary>
public sealed record ErLegendItem(DiffStatus Change, string Label, double X, double Y);

public sealed record ErLayoutResult(IReadOnlyList<ErNode> Nodes, IReadOnlyList<ErEdge> Edges, double Width, double Height, IReadOnlyList<ErLegendItem>? LegendItems = null)
{
    public static ErLayoutResult Empty { get; } = new([], [], 0, 0);
    public IReadOnlyList<ErLegendItem> Legend => LegendItems ?? [];
}

/// <summary>
/// Places tables left to right so that referenced (parent) tables sit left of the tables that reference them, orders each
/// column of tables to keep related tables close, and puts tables without relationships in a grid underneath.
/// Text is monospace, so box widths follow from character counts and the same numbers serve the screen, SVG and PNG.
/// </summary>
public static class ErLayout
{
    public const double CharWidth = 7.2;
    public const double FontSize = 12;
    public const double HeaderHeight = 28;
    public const double RowHeight = 20;
    public const double Padding = 10;
    public const double BadgeWidth = 34;
    public const double Margin = 30;
    const double LayerGap = 90;
    const double RowGap = 28;
    const double TypeGap = 18;
    const double MinWidth = 160;
    const double GridWidth = 1200;
    const int Sweeps = 4;

    public static ErLayoutResult Compute(ErModel model, IReadOnlyList<(DiffStatus Change, string Label)>? legend = null)
    {
        var tables = model.Tables.OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (tables.Count == 0)
            return ErLayoutResult.Empty;

        var byId = tables.ToDictionary(t => t.Id);
        var relations = model.Relations.Where(r => byId.ContainsKey(r.FromId) && byId.ContainsKey(r.ToId)).ToList();

        var parents = tables.ToDictionary(t => t.Id, _ => new List<string>());
        var neighbours = tables.ToDictionary(t => t.Id, _ => new List<string>());
        foreach (var r in relations)
        {
            if (r.FromId == r.ToId)
                continue; // a table referencing itself needs a loop line, but does not change where it sits
            parents[r.FromId].Add(r.ToId);
            neighbours[r.FromId].Add(r.ToId);
            neighbours[r.ToId].Add(r.FromId);
        }
        var related = relations.SelectMany(r => new[] { r.FromId, r.ToId }).ToHashSet();

        var layers = BuildLayers(tables.Where(t => related.Contains(t.Id)).ToList(), parents);
        OrderLayers(layers, neighbours);

        var sizes = tables.ToDictionary(t => t.Id, Measure);
        var nodes = new Dictionary<string, ErNode>();
        double x = Margin, connectedHeight = 0;
        foreach (var layer in layers)
        {
            double y = Margin, widest = 0;
            foreach (var t in layer)
            {
                var (w, h, typeOffset) = sizes[t.Id];
                nodes[t.Id] = new ErNode(t, x, y, w, h, typeOffset);
                y += h + RowGap;
                widest = Math.Max(widest, w);
            }
            connectedHeight = Math.Max(connectedHeight, y - RowGap);
            x += widest + LayerGap;
        }
        var connectedWidth = layers.Count == 0 ? 0 : x - LayerGap;

        PlaceUnrelated(tables.Where(t => !related.Contains(t.Id)).ToList(), sizes, nodes,
            top: layers.Count == 0 ? Margin : connectedHeight + RowGap * 2, limit: Math.Max(connectedWidth, GridWidth));

        var edges = relations.Select(r => Route(r, nodes[r.FromId], nodes[r.ToId])).ToList();
        var width = Math.Max(nodes.Values.Max(n => n.Right), edges.Select(e => Math.Max(e.C1.X, e.C2.X)).DefaultIfEmpty(0).Max()) + Margin;
        var height = nodes.Values.Max(n => n.Y + n.Height) + Margin;

        var items = new List<ErLegendItem>();
        if (legend is { Count: > 0 })
        {
            double legendX = Margin;
            foreach (var (change, label) in legend)
            {
                items.Add(new ErLegendItem(change, label, legendX, height));
                legendX += 22 + label.Length * CharWidth + 28;
            }
            width = Math.Max(width, legendX - 28 + Margin);
            height += 14 + Margin;
        }
        return new ErLayoutResult(tables.Select(t => nodes[t.Id]).ToList(), edges, width, height, items);
    }

    // A table sits one column to the right of the furthest of its parents. A cycle is cut where it comes back to a table
    // that is still being placed, so every table gets a column and the walk ends.
    static List<List<ErTable>> BuildLayers(List<ErTable> related, Dictionary<string, List<string>> parents)
    {
        var byId = related.ToDictionary(t => t.Id);
        var layerOf = new Dictionary<string, int>();
        var inProgress = new HashSet<string>();

        int Place(string id)
        {
            if (layerOf.TryGetValue(id, out var known))
                return known;
            inProgress.Add(id);
            var layer = 0;
            foreach (var parent in parents[id].Where(p => !inProgress.Contains(p)))
                layer = Math.Max(layer, Place(parent) + 1);
            inProgress.Remove(id);
            return layerOf[id] = layer;
        }

        foreach (var t in related)
            Place(t.Id);

        var count = related.Count == 0 ? 0 : layerOf.Values.Max() + 1;
        var layers = Enumerable.Range(0, count).Select(_ => new List<ErTable>()).ToList();
        foreach (var t in related)
            layers[layerOf[t.Id]].Add(t);
        return layers;
    }

    // Barycentre ordering: a table moves towards the average height of the tables it is related to, sweeping both ways.
    static void OrderLayers(List<List<ErTable>> layers, Dictionary<string, List<string>> neighbours)
    {
        var position = new Dictionary<string, double>();
        void Reindex(List<ErTable> layer)
        {
            for (var i = 0; i < layer.Count; i++)
                position[layer[i].Id] = (i + 0.5) / layer.Count;
        }
        foreach (var layer in layers)
            Reindex(layer);

        for (var sweep = 0; sweep < Sweeps; sweep++)
        {
            var order = sweep % 2 == 0 ? Enumerable.Range(0, layers.Count) : Enumerable.Range(0, layers.Count).Reverse();
            foreach (var i in order)
            {
                var sorted = layers[i].OrderBy(t => neighbours[t.Id] is { Count: > 0 } n ? n.Average(id => position[id]) : position[t.Id]).ToList();
                layers[i] = sorted;
                Reindex(sorted);
            }
        }
    }

    static void PlaceUnrelated(List<ErTable> tables, Dictionary<string, (double W, double H, double TypeOffset)> sizes,
        Dictionary<string, ErNode> nodes, double top, double limit)
    {
        double x = Margin, y = top, rowHeight = 0;
        foreach (var t in tables)
        {
            var (w, h, typeOffset) = sizes[t.Id];
            if (x > Margin && x + w > limit)
            {
                x = Margin;
                y += rowHeight + RowGap;
                rowHeight = 0;
            }
            nodes[t.Id] = new ErNode(t, x, y, w, h, typeOffset);
            x += w + RowGap;
            rowHeight = Math.Max(rowHeight, h);
        }
    }

    static (double W, double H, double TypeOffset) Measure(ErTable t)
    {
        var nameChars = t.Columns.Select(c => c.Name.Length).DefaultIfEmpty(0).Max();
        var typeChars = t.Columns.Select(c => c.DisplayType.Length).DefaultIfEmpty(0).Max();
        var typeOffset = Padding + BadgeWidth + nameChars * CharWidth + TypeGap;
        var rows = Math.Max(1, t.Columns.Count);
        var subtitleWidth = Padding * 2 + (t.Subtitle?.Length ?? 0) * CharWidth;
        var width = Math.Max(MinWidth, Math.Max(Math.Max(typeOffset + typeChars * CharWidth + Padding, subtitleWidth),
            t.Title.Length * (CharWidth + 0.6) + Padding * 2));
        return (Math.Ceiling(width), HeaderHeight + RowHeight * rows + 6, typeOffset);
    }

    static ErEdge Route(ErRelation r, ErNode child, ErNode parent)
    {
        var fromY = child.RowY(r.FromColumns[0]);
        var toY = parent.RowY(r.ToColumns[0]);
        ErPoint start, end, c1, c2;

        if (child == parent)
        {
            // A loop on the right-hand side.
            start = new ErPoint(child.Right, fromY);
            end = new ErPoint(child.Right, toY);
            (c1, c2) = (new ErPoint(child.Right + 50, fromY), new ErPoint(child.Right + 50, toY));
        }
        else if (child.X >= parent.Right)
        {
            (start, end) = (new ErPoint(child.X, fromY), new ErPoint(parent.Right, toY));
            var d = Math.Max(40, (start.X - end.X) / 2);
            (c1, c2) = (new ErPoint(start.X - d, fromY), new ErPoint(end.X + d, toY));
        }
        else if (child.Right <= parent.X)
        {
            (start, end) = (new ErPoint(child.Right, fromY), new ErPoint(parent.X, toY));
            var d = Math.Max(40, (end.X - start.X) / 2);
            (c1, c2) = (new ErPoint(start.X + d, fromY), new ErPoint(end.X - d, toY));
        }
        else
        {
            // Overlapping columns (two tables in the same column): both ends on the right-hand side, bulging outwards.
            var bulge = Math.Max(child.Right, parent.Right) + 50;
            (start, end) = (new ErPoint(child.Right, fromY), new ErPoint(parent.Right, toY));
            (c1, c2) = (new ErPoint(bulge, fromY), new ErPoint(bulge, toY));
        }

        return new ErEdge(r, start, c1, c2, end, ArrowHead(c2, end));
    }

    /// <summary>A small triangle whose tip is <paramref name="tip"/>, pointing away from <paramref name="from"/>.</summary>
    static ErPoint[] ArrowHead(ErPoint from, ErPoint tip)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        (dx, dy) = length < 0.001 ? (1, 0) : (dx / length, dy / length);
        var baseX = tip.X - dx * 9;
        var baseY = tip.Y - dy * 9;
        return [tip, new ErPoint(baseX - dy * 4.5, baseY + dx * 4.5), new ErPoint(baseX + dy * 4.5, baseY - dx * 4.5)];
    }
}
