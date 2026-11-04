using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public sealed record DiffFilterChoice(DiffStatus? Status, string Label);

public sealed partial class SchemaCompareViewModel : ViewModelBase
{
    const int ContextLines = 3;

    readonly ICredentialStore _credentials;
    CancellationTokenSource? _cts;
    List<SchemaDifference> _all = [];
    string? _pendingSourceDatabase;
    string? _pendingTargetDatabase;

    public SchemaCompareViewModel(
        IReadOnlyList<ConnectionProfile> profiles, ICredentialStore credentials,
        ConnectionProfile? sourceProfile = null, string? sourceDatabase = null)
    {
        _credentials = credentials;
        Profiles = profiles;
        SelectedFilter = Filters[0];
        _pendingSourceDatabase = sourceDatabase;
        SourceProfile = sourceProfile is null ? null : profiles.FirstOrDefault(p => p.Id == sourceProfile.Id) ?? sourceProfile;
    }

    public IReadOnlyList<ConnectionProfile> Profiles { get; }
    public ObservableCollection<string> SourceDatabases { get; } = [];
    public ObservableCollection<string> TargetDatabases { get; } = [];
    public ObservableCollection<SchemaDifference> Differences { get; } = [];
    public ObservableCollection<DiffLine> DetailLines { get; } = [];

    public IReadOnlyList<DiffFilterChoice> Filters { get; } =
    [
        new(null, "All differences"),
        new(DiffStatus.OnlyInSource, "Only in source"),
        new(DiffStatus.OnlyInTarget, "Only in target"),
        new(DiffStatus.Different, "Different"),
    ];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand))] public partial ConnectionProfile? SourceProfile { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand))] public partial string? SourceDatabase { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand))] public partial ConnectionProfile? TargetProfile { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand))] public partial string? TargetDatabase { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(CancelCommand))] public partial bool IsComparing { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "Pick a source and a target, then compare.";
    [ObservableProperty] public partial bool LastRunFailed { get; set; }
    [ObservableProperty] public partial DiffFilterChoice SelectedFilter { get; set; }
    [ObservableProperty] public partial string FilterText { get; set; } = "";
    [ObservableProperty] public partial bool ChangesOnly { get; set; } = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] public partial SchemaDifference? SelectedDifference { get; set; }

    public bool HasSelection => SelectedDifference is not null;
    public bool HasResults => _all.Count > 0;

    partial void OnSourceProfileChanged(ConnectionProfile? value) => _ = LoadDatabasesAsync(value, source: true);
    partial void OnTargetProfileChanged(ConnectionProfile? value) => _ = LoadDatabasesAsync(value, source: false);
    partial void OnSelectedFilterChanged(DiffFilterChoice value) => ApplyFilter();
    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnChangesOnlyChanged(bool value) => ShowDetail();
    partial void OnSelectedDifferenceChanged(SchemaDifference? value) => ShowDetail();

    async Task LoadDatabasesAsync(ConnectionProfile? profile, bool source)
    {
        var list = source ? SourceDatabases : TargetDatabases;
        list.Clear();
        if (source) SourceDatabase = null; else TargetDatabase = null;
        if (profile is null)
            return;

        try
        {
            await using var connection = await ServerSession.OpenAsync(profile, _credentials, "master");
            var names = (await CatalogService.ListDatabasesAsync(connection))
                .Where(d => d.IsOnline && !d.IsSystem).Select(d => d.Name).ToList();
            if ((source ? SourceProfile : TargetProfile) != profile)
                return; // the user picked another server while this one was loading

            foreach (var name in names)
                list.Add(name);
            var wanted = source ? _pendingSourceDatabase : _pendingTargetDatabase;
            if (wanted is not null && names.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            {
                var match = names.First(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
                if (source) SourceDatabase = match; else TargetDatabase = match;
            }
            if (source) _pendingSourceDatabase = null; else _pendingTargetDatabase = null;
            LastRunFailed = false;
        }
        catch (Exception ex)
        {
            LastRunFailed = true;
            StatusText = $"Could not list databases on {profile.DisplayName}: {ex.Message}";
        }
    }

    bool CanCompare() =>
        !IsComparing && SourceProfile is not null && TargetProfile is not null &&
        !string.IsNullOrEmpty(SourceDatabase) && !string.IsNullOrEmpty(TargetDatabase);

    [RelayCommand]
    void Swap()
    {
        var (sp, sd, tp, td) = (SourceProfile, SourceDatabase, TargetProfile, TargetDatabase);
        SetSide(source: true, tp, td);
        SetSide(source: false, sp, sd);
    }

    // Changing the server reloads its database list and then picks the pending database; the same server only needs the database set.
    void SetSide(bool source, ConnectionProfile? profile, string? database)
    {
        if (ReferenceEquals(source ? SourceProfile : TargetProfile, profile))
        {
            if (source) SourceDatabase = database; else TargetDatabase = database;
            return;
        }
        if (source) _pendingSourceDatabase = database; else _pendingTargetDatabase = database;
        if (source) SourceProfile = profile; else TargetProfile = profile;
    }

    [RelayCommand(CanExecute = nameof(CanCompare))]
    async Task CompareAsync()
    {
        IsComparing = true;
        LastRunFailed = false;
        StatusText = "Reading both schemas…";
        ClearResults();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var sourceTask = CaptureAsync(SourceProfile!, SourceDatabase!, token);
            var targetTask = CaptureAsync(TargetProfile!, TargetDatabase!, token);
            await Task.WhenAll(sourceTask, targetTask);
            var (source, target) = (sourceTask.Result, targetTask.Result);

            StatusText = "Comparing…";
            _all = await Task.Run(() => SchemaComparer.Compare(source, target), token);
            OnPropertyChanged(nameof(HasResults));
            ApplyFilter();

            var onlySource = _all.Count(d => d.Status == DiffStatus.OnlyInSource);
            var onlyTarget = _all.Count(d => d.Status == DiffStatus.OnlyInTarget);
            var different = _all.Count(d => d.Status == DiffStatus.Different);
            StatusText = _all.Count == 0
                ? $"The schemas match ({source.Objects.Count:N0} objects compared)."
                : $"{_all.Count:N0} difference{(_all.Count == 1 ? "" : "s")} in {Math.Max(source.Objects.Count, target.Objects.Count):N0} objects: " +
                  $"{onlySource:N0} only in source, {onlyTarget:N0} only in target, {different:N0} different.";
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
            IsComparing = false;
        }
    }

    async Task<SchemaSnapshot> CaptureAsync(ConnectionProfile profile, string database, CancellationToken ct)
    {
        await using var connection = await ServerSession.OpenAsync(profile, _credentials, "master", ct);
        return await SchemaSnapshotter.CaptureAsync(connection, database, ct);
    }

    [RelayCommand(CanExecute = nameof(IsComparing))]
    void Cancel() => _cts?.Cancel();

    void ClearResults()
    {
        _all = [];
        Differences.Clear();
        SelectedDifference = null;
        OnPropertyChanged(nameof(HasResults));
    }

    void ApplyFilter()
    {
        var keep = SelectedDifference;
        var text = FilterText.Trim();
        Differences.Clear();
        foreach (var d in _all.Where(d =>
                     (SelectedFilter.Status is null || d.Status == SelectedFilter.Status) &&
                     (text.Length == 0 || d.FullName.Contains(text, StringComparison.OrdinalIgnoreCase))))
            Differences.Add(d);
        SelectedDifference = keep is not null && Differences.Contains(keep) ? keep : Differences.FirstOrDefault();
    }

    void ShowDetail()
    {
        DetailLines.Clear();
        if (SelectedDifference is null)
            return;
        foreach (var line in ChangesOnly ? WithContext(SelectedDifference.Lines, ContextLines) : SelectedDifference.Lines)
            DetailLines.Add(line);
    }

    /// <summary>Keeps changed lines plus a few lines around each, and replaces long unchanged stretches with a "⋯" row.</summary>
    public static List<DiffLine> WithContext(IReadOnlyList<DiffLine> lines, int context)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffLineKind.Same)
                continue;
            for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context); j++)
                keep[j] = true;
        }

        var result = new List<DiffLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i])
                result.Add(lines[i]);
            else if (result.Count == 0 || result[^1].Text != "⋯" || result[^1].Kind != DiffLineKind.Same)
                result.Add(new DiffLine(DiffLineKind.Same, "⋯"));
        }
        return result;
    }

    /// <summary>The shown differences as plain text, for pasting into a ticket or a chat.</summary>
    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Source: {SourceProfile?.DisplayName} / {SourceDatabase}");
        sb.AppendLine($"Target: {TargetProfile?.DisplayName} / {TargetDatabase}");
        sb.AppendLine("+ in source only, − in target only");
        foreach (var d in Differences)
        {
            sb.AppendLine().AppendLine($"[{d.StatusLabel}] {d.Kind} {d.FullName}");
            foreach (var line in WithContext(d.Lines, ContextLines))
                sb.AppendLine($"  {line.Marker} {line.Text}");
        }
        return sb.ToString();
    }
}
