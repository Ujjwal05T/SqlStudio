using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

/// <summary>What a diagram window shows: how to read the model and how to word it.</summary>
public sealed record DiagramSource(
    string WindowTitle, string FileStem, string? Subject,
    Func<ConnectionProfile, string, ICredentialStore, CancellationToken, Task<ErModel>> Load, Func<ErModel, string> Describe)
{
    /// <summary>A second heading line, when "database on server" is not the right one.</summary>
    public string? Detail { get; init; }

    /// <summary>The colour key drawn under the diagram.</summary>
    public IReadOnlyList<(DiffStatus Change, string Label)> Legend { get; init; } = [];

    /// <summary>When set, the window offers to hide everything that did not change.</summary>
    public Func<ErModel, ErModel>? OnlyChanged { get; init; }

    static async Task<T> WithConnectionAsync<T>(ConnectionProfile profile, ICredentialStore credentials, CancellationToken ct, Func<Microsoft.Data.SqlClient.SqlConnection, Task<T>> work)
    {
        await using var connection = await ServerSession.OpenAsync(profile, credentials, "master", ct);
        return await work(connection);
    }

    /// <summary>Tables, columns and foreign keys.</summary>
    public static DiagramSource Er { get; } = new("ER diagram", "ER diagram", null,
        (profile, database, credentials, ct) => WithConnectionAsync(profile, credentials, ct, c => ErCatalog.LoadAsync(c, database, ct)),
        model => model.Tables.Count == 0
            ? "This database has no tables."
            : $"{model.Tables.Count:N0} table{(model.Tables.Count == 1 ? "" : "s")}, " +
              $"{model.Relations.Count:N0} relationship{(model.Relations.Count == 1 ? "" : "s")}");

    /// <summary>Which views, procedures and functions use which objects. With a focus, only the chains through that object.</summary>
    public static DiagramSource Dependencies(DependencyObject? focus = null) => new(
        "Dependencies", "dependencies", focus is null ? null : $"What uses {focus.Id}, and what it uses",
        (profile, database, credentials, ct) => WithConnectionAsync(profile, credentials, ct,
            async c => DependencyGraph.Build(await DependencyCatalog.LoadAsync(c, database, ct), focus)),
        model => (focus, model.Relations.Count) switch
        {
            (null, 0) => "No view, procedure or function in this database refers to another object.",
            (not null, 0) => $"Nothing uses {focus.Id}, and it uses no other object in this database.",
            _ => $"{model.Tables.Count:N0} object{(model.Tables.Count == 1 ? "" : "s")}, " +
                 $"{model.Relations.Count:N0} dependenc{(model.Relations.Count == 1 ? "y" : "ies")}. An arrow points from an object to what it uses.",
        });

    /// <summary>The tables of two databases in one picture, coloured by what the schema compare found.</summary>
    public static DiagramSource Diff(
        ConnectionProfile sourceProfile, string sourceDatabase, ConnectionProfile targetProfile, string targetDatabase,
        IReadOnlyList<SchemaDifference> differences)
    {
        var otherDifferences = differences.Count(d => d.Kind != "Table");
        return new("Schema differences", "schema differences", "Schema differences",
            async (_, _, credentials, ct) =>
            {
                var source = WithConnectionAsync(sourceProfile, credentials, ct, c => ErCatalog.LoadAsync(c, sourceDatabase, ct));
                var target = WithConnectionAsync(targetProfile, credentials, ct, c => ErCatalog.LoadAsync(c, targetDatabase, ct));
                await Task.WhenAll(source, target);
                return SchemaDiffDiagram.Build(source.Result, target.Result, differences);
            },
            model => DescribeDiff(model, otherDifferences))
        {
            Detail = $"Source: {sourceDatabase} on {sourceProfile.DisplayName}   ·   Target: {targetDatabase} on {targetProfile.DisplayName}",
            Legend =
            [
                (DiffStatus.OnlyInSource, "only in source"),
                (DiffStatus.OnlyInTarget, "only in target"),
                (DiffStatus.Different, "different"),
            ],
            OnlyChanged = SchemaDiffDiagram.OnlyChanged,
        };
    }

    static string DescribeDiff(ErModel model, int otherDifferences)
    {
        var parts = new[]
        {
            (model.Tables.Count(t => t.Change == DiffStatus.OnlyInSource), "only in source"),
            (model.Tables.Count(t => t.Change == DiffStatus.OnlyInTarget), "only in target"),
            (model.Tables.Count(t => t.Change == DiffStatus.Different), "different"),
        }.Where(p => p.Item1 > 0).Select(p => $"{p.Item1:N0} {p.Item2}").ToList();

        var text = parts.Count == 0 ? "No table differences." : "Tables: " + string.Join(", ", parts) + ".";
        if (otherDifferences > 0)
            text += $" {otherDifferences:N0} view, procedure, function or trigger difference{(otherDifferences == 1 ? "" : "s")} not drawn; see the compare list.";
        return text;
    }
}

public sealed partial class ErDiagramViewModel : ViewModelBase
{
    readonly ICredentialStore _credentials;
    CancellationTokenSource? _cts;

    public ErDiagramViewModel(ConnectionProfile profile, string database, ICredentialStore credentials, DiagramSource? source = null)
    {
        Source = source ?? DiagramSource.Er;
        Profile = profile;
        Database = database;
        _credentials = credentials;
    }

    public ConnectionProfile Profile { get; }
    public string Database { get; }
    public DiagramSource Source { get; }
    public string WindowTitle => Source.WindowTitle;
    public string Heading => Source.Subject ?? $"{Database} on {Profile.DisplayName}";
    public string SubHeading => Source.Detail ?? (Source.Subject is null ? "" : $"{Database} on {Profile.DisplayName}");
    public bool HasChangeFilter => Source.OnlyChanged is not null;

    [ObservableProperty] public partial ErLayoutResult? Layout { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Reading the tables…";
    [ObservableProperty] public partial bool LastRunFailed { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool ChangedOnly { get; set; } = true;

    public bool HasDiagram => Layout is { Nodes.Count: > 0 };

    ErModel? _model;

    partial void OnLayoutChanged(ErLayoutResult? value) => OnPropertyChanged(nameof(HasDiagram));
    partial void OnChangedOnlyChanged(bool value) => Rebuild();

    public async Task LoadAsync()
    {
        IsLoading = true;
        LastRunFailed = false;
        StatusText = "Reading the tables…";
        _cts = new CancellationTokenSource();
        try
        {
            SetModel(await Source.Load(Profile, Database, _credentials, _cts.Token));
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            LastRunFailed = true;
            StatusText = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsLoading = false;
        }
    }

    public void SetModel(ErModel model)
    {
        _model = model;
        Rebuild();
    }

    void Rebuild()
    {
        if (_model is null)
            return;
        var shown = ChangedOnly && Source.OnlyChanged is { } narrow ? narrow(_model) : _model;
        Layout = ErLayout.Compute(shown, Source.Legend.Count > 0 ? Source.Legend : null);
        StatusText = Source.Describe(shown);
    }

    public void Cancel() => _cts?.Cancel();
}
