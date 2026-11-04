using System.ComponentModel;

namespace SqlStudio.Services;

public enum RowState { Unchanged, Modified, Added, Deleted }

/// <summary>
/// One row in the result grid. It wraps the row's value array and remembers what was edited, so the grid can show
/// changes before they are saved and Discard can put the original values back.
/// </summary>
public sealed class GridRow : INotifyPropertyChanged
{
    static readonly PropertyChangedEventArgs AllChanged = new(string.Empty);

    object?[]? _original;          // copy of the values taken just before the first edit
    HashSet<int>? _changed;        // columns whose value differs from the original
    HashSet<int>? _assigned;       // new rows: columns the user gave a value (the rest take the database default)
    RowState _stateBeforeDelete;
    int _selectedColumn = -1;

    public GridRow(object?[] values, bool isNew = false)
    {
        Values = values;
        if (isNew)
        {
            State = RowState.Added;
            _assigned = [];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The values the grid shows. For existing rows this is the same array the query returned.</summary>
    public object?[] Values { get; }

    public RowState State { get; private set; }
    public object? this[int column] => Values[column];

    /// <summary>The cell the user last clicked, or -1. Drives the cell highlight and what Copy copies.</summary>
    public int SelectedColumn
    {
        get => _selectedColumn;
        set
        {
            if (_selectedColumn == value)
                return;
            _selectedColumn = value;
            PropertyChanged?.Invoke(this, AllChanged);
        }
    }

    public bool IsChanged(int column) => State is RowState.Modified or RowState.Deleted && _changed?.Contains(column) == true;
    public bool IsAssigned(int column) => State != RowState.Added || _assigned?.Contains(column) == true;
    public IReadOnlyCollection<int> ChangedColumns => (IReadOnlyCollection<int>?)_changed ?? [];
    public IReadOnlyCollection<int> AssignedColumns => (IReadOnlyCollection<int>?)_assigned ?? [];

    /// <summary>The value before any edit (the value used to find the row in the database).</summary>
    public object? OriginalValue(int column) => _original is null ? Values[column] : _original[column];

    public void Set(int column, object? value)
    {
        switch (State)
        {
            case RowState.Deleted:
                return;
            case RowState.Added:
                Values[column] = value;
                (_assigned ??= []).Add(column);
                break;
            default:
                _original ??= (object?[])Values.Clone();
                Values[column] = value;
                _changed ??= [];
                if (SameValue(value, _original[column]))
                    _changed.Remove(column);
                else
                    _changed.Add(column);
                State = _changed.Count > 0 ? RowState.Modified : RowState.Unchanged;
                break;
        }
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>New rows only: go back to "use the database default" for this cell.</summary>
    public void Unassign(int column)
    {
        if (State != RowState.Added)
            return;
        Values[column] = null;
        _assigned?.Remove(column);
        PropertyChanged?.Invoke(this, AllChanged);
    }

    public void MarkDeleted()
    {
        if (State is RowState.Deleted or RowState.Added)
            return;
        _stateBeforeDelete = State;
        State = RowState.Deleted;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    public void UndoDelete()
    {
        if (State != RowState.Deleted)
            return;
        State = _stateBeforeDelete;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>Puts the original values back and clears the pending change.</summary>
    public void Revert()
    {
        if (State == RowState.Added)
            return; // a new row is discarded by removing it, not by reverting it
        if (_original is not null)
            Array.Copy(_original, Values, Values.Length);
        _original = null;
        _changed = null;
        State = RowState.Unchanged;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    /// <summary>Called after the change was saved: what is shown is now what the database holds.</summary>
    public void AcceptChanges()
    {
        _original = null;
        _changed = null;
        _assigned = null;
        State = RowState.Unchanged;
        PropertyChanged?.Invoke(this, AllChanged);
    }

    static bool SameValue(object? a, object? b) => a is null || b is null ? a is null && b is null : a.Equals(b);
}
