using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;
using SqlStudio.Views;

namespace SqlStudio.ViewModels;

/// <summary>Everything needed to open a connection for editing this result: the server it came from.</summary>
public sealed record ResultEditContext(ConnectionProfile Profile, ICredentialStore Credentials)
{
    public bool IsProd => Profile.Environment == ServerEnvironment.Production;
    public string ServerName => Profile.DisplayName;
}

/// <summary>
/// One result grid: its rows (with sorting and filtering), the clicked cell, and, when the result comes from a single
/// table with a key, editing with a review step before anything is saved.
/// </summary>
public sealed partial class ResultSetViewModel : ResultPanelViewModel
{
    readonly ResultEditContext? _context;
    readonly List<GridRow> _all;
    readonly HashSet<GridRow> _dirty = [];
    CancellationTokenSource? _viewCts;
    EditAnalysis? _analysis;
    GridRow? _cellRow;

    public ResultSetViewModel(ResultSet data, int index, ResultEditContext? context = null)
    {
        Data = data;
        _context = context;
        _index = index;
        _all = data.Rows.Select(r => new GridRow(r)).ToList();
        Rows = _all;
        UpdateTitle();
    }

    readonly int _index;

    public ResultSet Data { get; }

    /// <summary>The rows the grid shows: all rows, or the ones matching the filter, in the chosen order.</summary>
    public IReadOnlyList<GridRow> Rows { get; private set; }

    // ---- hooks the window provides ----

    /// <summary>Shows the review dialog. Returns true to go ahead and save.</summary>
    public Func<ChangeReviewViewModel, Task<bool>>? ReviewAsync { get; set; }

    /// <summary>Asks the user to confirm throwing away edits. Title, message, confirm label, cancel label.</summary>
    public Func<string, string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>The rows currently selected in the grid.</summary>
    public Func<IReadOnlyList<GridRow>>? SelectedRows { get; set; }

    /// <summary>A new row was added: the grid scrolls to it and starts editing.</summary>
    public event Action<GridRow>? RowAdded;

    // ---- state ----

    [ObservableProperty] public partial string FilterText { get; set; } = "";
    [ObservableProperty] public partial int SortColumn { get; set; } = -1;
    [ObservableProperty] public partial bool SortDescending { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanToggleEdit))] public partial bool IsEditing { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanToggleEdit))] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Notice { get; set; } = "";
    [ObservableProperty] public partial bool NoticeIsError { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PendingText))] public partial int PendingCount { get; set; }

    public bool CanEdit => _context is not null && Data.SourceSql is not null && Data.SetIndex == 0;
    public bool CanToggleEdit => CanEdit && !IsBusy;
    public bool IsProd => _context?.IsProd ?? false;
    public EditTarget? Target => _analysis?.Target;
    public string EditTitle => Target is null ? "Editing" : $"Editing {Target.DisplayName}";
    public string PendingText => PendingCount == 0 ? "No changes yet" : $"{PendingCount:N0} pending change{(PendingCount == 1 ? "" : "s")}";
    public string CountText => Rows.Count == _all.Count ? $"{_all.Count:N0} row{(_all.Count == 1 ? "" : "s")}" : $"{Rows.Count:N0} of {_all.Count:N0} rows";

    partial void OnFilterTextChanged(string value) => _ = RebuildAsync(debounce: true);

    void UpdateTitle() => Title = $"Result {_index} ({_all.Count:N0}{(Data.Truncated ? "+" : "")} rows)";

    // ---- sorting and filtering ----

    public void ToggleSort(int column)
    {
        if (SortColumn != column)
        {
            SortColumn = column;
            SortDescending = false;
        }
        else if (!SortDescending)
        {
            SortDescending = true;
        }
        else
        {
            SortColumn = -1;
            SortDescending = false;
        }
        _ = RebuildAsync(debounce: false);
    }

    async Task RebuildAsync(bool debounce)
    {
        _viewCts?.Cancel();
        var cts = _viewCts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            if (debounce)
                await Task.Delay(250, ct);
            var (text, column, descending) = (FilterText.Trim(), SortColumn, SortDescending);
            var rows = await Task.Run(() => BuildView(text, column, descending, ct), ct);
            ct.ThrowIfCancellationRequested();
            Rows = rows;
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(CountText));
        }
        catch (OperationCanceledException)
        {
            // a newer filter or sort replaced this one
        }
    }

    List<GridRow> BuildView(string filter, int sortColumn, bool descending, CancellationToken ct)
    {
        IEnumerable<GridRow> rows = _all;
        if (filter.Length > 0)
        {
            var types = Data.Columns.Select(c => c.TypeName).ToArray();
            rows = rows.Where(r =>
            {
                ct.ThrowIfCancellationRequested();
                if (r.State == RowState.Added)
                    return true; // a row you just added stays visible whatever the filter says
                for (var i = 0; i < types.Length; i++)
                    if (r[i] is not null && CellFormatter.Format(r[i], types[i]).Contains(filter, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            });
        }

        if (sortColumn >= 0)
        {
            var existing = rows.Where(r => r.State != RowState.Added);
            var sorted = descending ? existing.OrderByDescending(r => r[sortColumn], ValueComparer.Instance) : existing.OrderBy(r => r[sortColumn], ValueComparer.Instance);
            rows = sorted.Concat(rows.Where(r => r.State == RowState.Added)); // new rows stay at the end
        }
        return rows.ToList();
    }

    // ---- the clicked cell ----

    public GridRow? CellRow => _cellRow;
    public int CellColumn { get; private set; } = -1;

    public void SelectCell(GridRow? row, int column)
    {
        if (_cellRow is not null && !ReferenceEquals(_cellRow, row))
            _cellRow.SelectedColumn = -1;
        _cellRow = row;
        CellColumn = row is null ? -1 : column;
        if (row is not null)
            row.SelectedColumn = column;
    }

    // ---- copy ----

    public async Task<string> InsertScriptAsync(IEnumerable<GridRow> rows)
    {
        // The real table name is used when it can be worked out; otherwise a placeholder is written.
        if (_analysis is null && CanEdit)
            await EnsureAnalysisAsync();
        return GridCopy.Insert(Data, rows, Target);
    }

    // ---- editing ----

    /// <summary>Supplies the analysis directly, so the editing rules can be exercised without a server.</summary>
    public void ApplyAnalysis(EditAnalysis analysis) => _analysis = analysis;

    async Task<EditAnalysis> EnsureAnalysisAsync()
    {
        if (_analysis is not null)
            return _analysis;
        try
        {
            await using var connection = await ServerSession.OpenAsync(_context!.Profile, _context.Credentials, Data.Database);
            _analysis = await ResultEditAnalyzer.AnalyzeAsync(connection, Data, CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new EditAnalysis(null, "Couldn't check whether this result can be edited: " + ex.Message); // not cached, so a retry can work
        }
        return _analysis;
    }

    [RelayCommand(CanExecute = nameof(CanToggleEdit))]
    async Task ToggleEditAsync()
    {
        if (IsEditing)
        {
            if (_dirty.Count > 0 && !await ConfirmAsync("Leave editing?", $"{PendingText} will be discarded.", "Discard changes", "Keep editing"))
                return;
            DiscardCore();
            IsEditing = false;
            Notice = "";
            return;
        }

        IsBusy = true;
        NoticeIsError = false;
        Notice = "Checking whether this result can be edited…";
        try
        {
            var analysis = await EnsureAnalysisAsync();
            if (analysis.Target is null)
            {
                NoticeIsError = true;
                Notice = analysis.Reason ?? "This result can't be edited.";
                return;
            }
            IsEditing = true;
            OnPropertyChanged(nameof(EditTitle));
            Notice = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public bool CanEditCell(GridRow row, int column) =>
        IsEditing && Target is { } target && row.State != RowState.Deleted &&
        (row.State == RowState.Added ? target.Columns[column].CanEditNew : target.Columns[column].CanEditExisting);

    /// <summary>Lets the grid report a value it could not accept (a letter in a number column, for example).</summary>
    public void ShowError(string message)
    {
        NoticeIsError = true;
        Notice = message;
    }

    public bool IsIdentity(int column) => Target?.Columns[column].IsIdentity == true;

    /// <summary>Applies what the user typed. Returns false, with the reason, when it isn't a valid value for the column.</summary>
    public bool TrySetCell(GridRow row, int column, string text, out string error)
    {
        error = "";
        if (!CanEditCell(row, column))
            return true;
        var col = Target!.Columns[column];
        if (!row.IsAssigned(column) && text.Length == 0)
            return true; // nothing typed into a "(default)" cell: leave it on the default
        if (!ValueParser.TryParse(text, col.TypeName, col.Nullable, out var value, out error))
            return false;
        if (Equals(value, row[column]) && row.IsAssigned(column))
            return true; // unchanged
        row.Set(column, value);
        Track(row);
        return true;
    }

    public void SetNull(GridRow row, int column)
    {
        if (!CanEditCell(row, column))
            return;
        if (!Target!.Columns[column].Nullable)
        {
            NoticeIsError = true;
            Notice = $"{Target.Columns[column].Name} doesn't allow NULL.";
            return;
        }
        row.Set(column, null);
        Track(row);
    }

    /// <summary>New rows only: leave this cell to the database default.</summary>
    public void UseDefault(GridRow row, int column)
    {
        row.Unassign(column);
        Track(row);
    }

    void Track(GridRow row)
    {
        if (row.State == RowState.Unchanged)
            _dirty.Remove(row);
        else
            _dirty.Add(row);
        PendingCount = _dirty.Count;
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        NoticeIsError = false;
        Notice = "";
    }

    [RelayCommand]
    async Task AddRowAsync()
    {
        if (!IsEditing || Target is null)
            return;
        var row = new GridRow(new object?[Data.Columns.Count], isNew: true);
        _all.Add(row);
        Data.Rows.Add(row.Values);
        Track(row);
        UpdateTitle();
        await RebuildAsync(debounce: false);
        RowAdded?.Invoke(row);
    }

    [RelayCommand]
    async Task DeleteRowsAsync()
    {
        if (!IsEditing)
            return;
        var rows = SelectedRows?.Invoke() ?? [];
        if (rows.Count == 0 && _cellRow is not null)
            rows = [_cellRow];
        var removedNew = false;
        foreach (var row in rows)
        {
            switch (row.State)
            {
                case RowState.Added:
                    _all.Remove(row);
                    Data.Rows.Remove(row.Values);
                    _dirty.Remove(row);
                    removedNew = true;
                    break;
                case RowState.Deleted:
                    row.UndoDelete();
                    Track(row);
                    break;
                default:
                    row.MarkDeleted();
                    Track(row);
                    break;
            }
        }
        PendingCount = _dirty.Count;
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        if (removedNew)
        {
            UpdateTitle();
            await RebuildAsync(debounce: false);
        }
    }

    bool HasPending() => _dirty.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(HasPending))]
    async Task DiscardAsync()
    {
        if (!await ConfirmAsync("Discard all changes?", $"{PendingText} will be thrown away. The database isn't touched.", "Discard changes", "Keep editing"))
            return;
        DiscardCore();
        await RebuildAsync(debounce: false);
    }

    void DiscardCore()
    {
        foreach (var row in _dirty.ToList())
        {
            if (row.State == RowState.Added)
            {
                _all.Remove(row);
                Data.Rows.Remove(row.Values);
                continue;
            }
            row.UndoDelete();
            row.Revert();
        }
        _dirty.Clear();
        PendingCount = 0;
        UpdateTitle();
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        Notice = "";
        _ = RebuildAsync(debounce: false);
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    async Task SaveAsync()
    {
        if (Target is not { } target || _context is null || _dirty.Count == 0)
            return;

        var rows = _all.Where(_dirty.Contains).ToList();
        var counts = $"{rows.Count(r => r.State == RowState.Modified)} updated, {rows.Count(r => r.State == RowState.Added)} added, {rows.Count(r => r.State == RowState.Deleted)} deleted";
        var review = new ChangeReviewViewModel(check => ChangeScripter.Script(ChangeScripter.Build(target, rows, check)), counts, _context.IsProd, _context.ServerName, target.DisplayName);
        if (ReviewAsync is not null && !await ReviewAsync(review))
            return;

        var changes = ChangeScripter.Build(target, rows, review.CheckOldValues);
        IsBusy = true;
        NoticeIsError = false;
        Notice = "Saving…";
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        try
        {
            var identities = await Task.Run(async () =>
            {
                await using var connection = await ServerSession.OpenAsync(_context.Profile, _context.Credentials, Data.Database);
                return await ChangeScripter.ApplyAsync(connection, changes, CancellationToken.None);
            });

            var deleted = false;
            foreach (var change in changes)
            {
                switch (change.Kind)
                {
                    case ChangeKind.Delete:
                        _all.Remove(change.Row);
                        Data.Rows.Remove(change.Row.Values);
                        deleted = true;
                        break;
                    case ChangeKind.Insert when identities.TryGetValue(change.Row, out var id) && id is not (null or DBNull) && target.IdentityColumn >= 0:
                        change.Row.Values[target.IdentityColumn] = ConvertIdentity(id, target.Columns[target.IdentityColumn].TypeName);
                        change.Row.AcceptChanges();
                        break;
                    default:
                        change.Row.AcceptChanges();
                        break;
                }
            }
            _dirty.Clear();
            PendingCount = 0;
            UpdateTitle();
            Notice = $"Saved: {counts}. Run the query again to see values the database filled in.";
            if (deleted)
                await RebuildAsync(debounce: false);
        }
        catch (Exception ex)
        {
            NoticeIsError = true;
            Notice = "Not saved. Everything was rolled back, and your edits are still here. " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
        }
    }

    static object ConvertIdentity(object value, string typeName) => typeName switch
    {
        "int" => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        "bigint" => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        "smallint" => Convert.ToInt16(value, CultureInfo.InvariantCulture),
        "tinyint" => Convert.ToByte(value, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    async Task<bool> ConfirmAsync(string title, string message, string confirm, string cancel) =>
        Confirm is null || await Confirm(title, message, confirm, cancel);
}

/// <summary>Orders cell values: NULL first, then by value; values of different types fall back to their text.</summary>
public sealed class ValueComparer : IComparer<object?>
{
    public static readonly ValueComparer Instance = new();

    public int Compare(object? x, object? y)
    {
        if (x is null || y is null)
            return x is null ? (y is null ? 0 : -1) : 1;
        if (x.GetType() == y.GetType() && x is IComparable c)
            return c.CompareTo(y);
        return string.Compare(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }
}
