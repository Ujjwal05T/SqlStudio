using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public enum NodeKind { Server, SystemGroup, Database, Folder, Table, View, Procedure, Function, Column, Info }

/// <summary>A node in the server tree. Children load the first time the node is expanded.</summary>
public sealed partial class ExplorerNode : ViewModelBase
{
    Func<Task<List<ExplorerNode>>>? _loader;
    bool _loaded;
    bool _inMatchedContext; // this node, or an ancestor, matches the filter

    ExplorerNode(NodeKind kind, string title, ServerSession session)
    {
        Kind = kind;
        Title = title;
        Session = session;
    }

    public NodeKind Kind { get; }
    public ServerSession Session { get; }
    public string? Database { get; private init; }
    public SchemaObject? Object { get; private init; }
    public string IconKind { get; private init; } = "";

    /// <summary>Columns sit under a table; a softer icon keeps them from competing with the objects around them.</summary>
    public double IconOpacity => Kind == NodeKind.Column ? 0.6 : 1;
    public string? Detail { get; private init; }
    public bool IsError { get; private init; }
    public DatabaseInfo? Info { get; private init; }
    public ObservableCollection<ExplorerNode> Children { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public ConnectionProfile Profile => Session.Profile;

    /// <summary>The text typed in the sidebar filter. One tree per window, so a shared value keeps nodes loaded later in step.</summary>
    public static string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsFilterVisible { get; set; } = true;

    // Environment tag only shows on the server row; databases below it sit under that row anyway.
    public bool HasEnv => IsServer && Profile.HasEnvironment;
    public string EnvShort => IsServer ? Profile.EnvironmentShort : "";
    public bool IsDev => IsServer && Profile.Environment == ServerEnvironment.Development;
    public bool IsStaging => IsServer && Profile.Environment == ServerEnvironment.Staging;
    public bool IsProd => IsServer && Profile.Environment == ServerEnvironment.Production;
    public bool IsServer => Kind == NodeKind.Server;
    public bool IsDatabase => Kind == NodeKind.Database;
    public bool IsTableOrView => Kind is NodeKind.Table or NodeKind.View;
    public bool CanRefresh => _loader is not null;
    public bool CanNewQuery => Kind is NodeKind.Server or NodeKind.Database;
    public bool CanBackup => Kind == NodeKind.Database && Info is { IsOnline: true } && Database != "tempdb";
    public bool CanCompare => Kind == NodeKind.Server || (Kind == NodeKind.Database && Info is { IsOnline: true });
    public bool CanDiagram => Kind == NodeKind.Database && Info is { IsOnline: true } && Database != "tempdb";
    public bool CanDependencies => CanDiagram || Kind is NodeKind.Table or NodeKind.View or NodeKind.Procedure or NodeKind.Function;
    public bool CanRestore => Kind == NodeKind.Server || (Kind == NodeKind.Database && Info is { IsSystem: false });

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
            _ = LoadAsync();
    }

    /// <summary>
    /// Shows this node if it, an ancestor, or a loaded descendant matches the filter. Returns whether it is visible.
    /// Only nodes already loaded can be searched; the rest load when expanded.
    /// </summary>
    public bool ApplyFilter(bool ancestorMatched = false)
    {
        var filter = Filter;
        if (filter.Length == 0)
        {
            _inMatchedContext = false;
            IsFilterVisible = true;
            foreach (var child in Children)
                child.ApplyFilter();
            return true;
        }

        var matched = ancestorMatched || (Kind != NodeKind.Info && Title.Contains(filter, StringComparison.OrdinalIgnoreCase));
        _inMatchedContext = matched;
        var anyChild = false;
        foreach (var child in Children)
            anyChild |= child.ApplyFilter(matched);

        IsFilterVisible = matched || anyChild;
        if (anyChild && !matched && _loaded)
            IsExpanded = true; // reveal the match
        return IsFilterVisible;
    }

    public async Task RefreshAsync()
    {
        _loaded = false;
        await LoadAsync();
    }

    async Task LoadAsync()
    {
        if (_loader is null)
            return;
        _loaded = true;
        Children.Clear();
        Children.Add(Placeholder("Loading…"));
        try
        {
            var nodes = await _loader();
            Children.Clear();
            foreach (var node in nodes)
            {
                node.ApplyFilter(_inMatchedContext);
                Children.Add(node);
            }
        }
        catch (Exception ex)
        {
            _loaded = false; // let the user retry by collapsing and expanding again
            Children.Clear();
            Children.Add(new ExplorerNode(NodeKind.Info, ex.Message, Session) { IsError = true });
        }
    }

    static ExplorerNode Placeholder(string text) => new(NodeKind.Info, text, null!);

    ExplorerNode WithLoader(Func<Task<List<ExplorerNode>>> loader)
    {
        _loader = loader;
        Children.Add(Placeholder("Loading…")); // makes the expander arrow appear
        return this;
    }

    // ---- tree construction ----

    public static ExplorerNode ForServer(ServerSession session) =>
        new ExplorerNode(NodeKind.Server, session.Profile.DisplayName, session) { IconKind = "server", Detail = session.Profile.Server }
            .WithLoader(() => LoadDatabasesAsync(session));

    static async Task<List<ExplorerNode>> LoadDatabasesAsync(ServerSession session)
    {
        var dbs = await session.UseAsync(CatalogService.ListDatabasesAsync);
        var nodes = dbs.Where(d => !d.IsSystem).Select(d => ForDatabase(session, d)).ToList();
        var system = new ExplorerNode(NodeKind.SystemGroup, "System databases", session) { IconKind = "layers" }
            .WithLoader(() => Task.FromResult(dbs.Where(d => d.IsSystem).Select(d => ForDatabase(session, d)).ToList()));
        nodes.Add(system);
        return nodes;
    }

    static ExplorerNode ForDatabase(ServerSession session, DatabaseInfo db)
    {
        var node = new ExplorerNode(NodeKind.Database, db.Name, session)
        {
            Database = db.Name,
            Info = db,
            IconKind = "database",
            Detail = db.IsOnline ? null : db.State.ToLowerInvariant().Replace('_', ' '),
        };
        return node.WithLoader(() => Task.FromResult(db.IsOnline
            ? new List<ExplorerNode>
            {
                Folder(session, db.Name, "Tables", ObjectKind.Table),
                Folder(session, db.Name, "Views", ObjectKind.View),
                Folder(session, db.Name, "Stored procedures", ObjectKind.Procedure),
                Folder(session, db.Name, "Functions", ObjectKind.Function),
            }
            : [new ExplorerNode(NodeKind.Info, $"Database is {db.State.ToLowerInvariant().Replace('_', ' ')}", session)]));
    }

    static ExplorerNode Folder(ServerSession session, string database, string title, ObjectKind kind) =>
        new ExplorerNode(NodeKind.Folder, title, session) { Database = database, IconKind = "folder" }
            .WithLoader(async () =>
            {
                var objects = await session.UseAsync(c => CatalogService.ListObjectsAsync(c, database, kind));
                return objects.Select(o => ForObject(session, database, o, kind)).ToList();
            });

    static ExplorerNode ForObject(ServerSession session, string database, SchemaObject obj, ObjectKind kind)
    {
        var (nodeKind, icon) = kind switch
        {
            ObjectKind.Table => (NodeKind.Table, "table"),
            ObjectKind.View => (NodeKind.View, "view"),
            ObjectKind.Procedure => (NodeKind.Procedure, "procedure"),
            _ => (NodeKind.Function, "function"),
        };
        var node = new ExplorerNode(nodeKind, $"{obj.Schema}.{obj.Name}", session) { Database = database, Object = obj, IconKind = icon };
        return kind is ObjectKind.Table or ObjectKind.View
            ? node.WithLoader(async () =>
            {
                var columns = await session.UseAsync(c => CatalogService.ListColumnsAsync(c, database, obj));
                return columns.Select(col => new ExplorerNode(NodeKind.Column, col.Name, session)
                {
                    Database = database,
                    IconKind = "column",
                    Detail = $"{col.Type}{(col.Nullable ? ", null" : "")}",
                }).ToList();
            })
            : node;
    }
}
