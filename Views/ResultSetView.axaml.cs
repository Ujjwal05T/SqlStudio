using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SqlStudio.Services;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class ResultSetView : UserControl
{
    readonly List<Icon> _sortIcons = [];
    ResultSetViewModel? _vm;
    MenuItem _copyCell = null!, _copyRow = null!, _copyRowHeaders = null!, _copyColumn = null!, _copyJson = null!, _copyInsert = null!;
    MenuItem _view = null!, _edit = null!, _setNull = null!, _useDefault = null!, _delete = null!;
    MenuItem _copyColumnHeader = null!, _selectColumn = null!, _selectAll = null!;
    readonly List<StackPanel> _headers = [];
    DispatcherTimer? _copyTimer;
    Separator _editSeparator = null!;

    public ResultSetView()
    {
        InitializeComponent();
        Grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString();
        Grid.SelectionChanged += (_, _) => OnGridSelectionChanged();
        Grid.CellPointerPressed += OnCellPointerPressed;
        Grid.BeginningEdit += OnBeginningEdit;
        Grid.CellEditEnding += OnCellEditEnding;
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerReleasedEvent, (_, _) => _dragging = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        BuildContextMenu();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.RowAdded -= OnRowAdded;
        }

        Grid.Columns.Clear();
        _sortIcons.Clear();
        _headers.Clear();
        _vm = DataContext as ResultSetViewModel;
        if (_vm is null)
        {
            Grid.ItemsSource = null;
            return;
        }

        for (var i = 0; i < _vm.Data.Columns.Count; i++)
            Grid.Columns.Add(BuildColumn(_vm, i));

        _vm.PropertyChanged += OnVmChanged;
        _vm.RowAdded += OnRowAdded;
        _vm.SelectedRows = () => Grid.SelectedItems.OfType<GridRow>().ToList();
        _vm.Confirm = (title, message, confirm, cancel) =>
            TopLevel.GetTopLevel(this) is Window owner ? Dialogs.ConfirmAsync(owner, title, message, confirm, cancelLabel: cancel) : Task.FromResult(true);
        _vm.ReviewAsync = review =>
            TopLevel.GetTopLevel(this) is Window owner ? new ReviewChangesWindow { DataContext = review }.ShowDialog<bool>(owner) : Task.FromResult(false);

        Grid.IsReadOnly = !_vm.IsEditing;
        Grid.ItemsSource = _vm.Rows;
        UpdateSortIcons();
    }

    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
            return;
        switch (e.PropertyName)
        {
            case nameof(ResultSetViewModel.Rows):
                Grid.ItemsSource = _vm.Rows;
                break;
            case nameof(ResultSetViewModel.IsEditing):
                Grid.IsReadOnly = !_vm.IsEditing;
                break;
            case nameof(ResultSetViewModel.SortColumn) or nameof(ResultSetViewModel.SortDescending):
                UpdateSortIcons();
                break;
            case nameof(ResultSetViewModel.RangeVersion):
                UpdateHeaderHighlight();
                UpdateSelectionStatus();
                break;
            case nameof(ResultSetViewModel.ColumnSelection):
                if (_vm.ColumnSelection >= 0 && Grid.SelectedItems.Count > 0)
                    Grid.SelectedItems.Clear(); // a column and some rows are never selected together
                UpdateHeaderHighlight();
                UpdateSelectionStatus();
                break;
        }
    }

    void UpdateSortIcons()
    {
        for (var i = 0; i < _sortIcons.Count; i++)
        {
            _sortIcons[i].IsVisible = _vm?.SortColumn == i;
            _sortIcons[i].Kind = _vm?.SortDescending == true ? "chevron-down" : "chevron-up";
        }
    }

    // ---- clicking and copying ----

    int IndexOf(DataGridColumn? column) => column is null ? -1 : Grid.Columns.IndexOf(column);

    void OnCellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (_vm is null || e.Row.DataContext is not GridRow row)
            return;
        var column = IndexOf(e.Column);
        var pointer = e.PointerPressedEventArgs.GetCurrentPoint(this).Properties;
        if (pointer.IsLeftButtonPressed)
        {
            // A click starts a block of cells (Shift+click stretches it); dragging then reaches further.
            if (e.PointerPressedEventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift) && _vm.CellRow is not null)
                _vm.ExtendTo(row, column);
            else
                _vm.SelectCell(row, column);
            _dragging = true;
            _cellPressPending = true;
            // The grid also selects the whole row; the block, not the row, is what a copy takes.
            Dispatcher.UIThread.Post(DropRowSelectionKeepingBlock, DispatcherPriority.Input);
            return;
        }
        if (!_vm.InBlock(row, column))
            _vm.SelectCell(row, column);
        // A right-click on a row outside the selection selects that row, so the menu acts on what was clicked.
        if (e.PointerPressedEventArgs.GetCurrentPoint(this).Properties.IsRightButtonPressed && !Grid.SelectedItems.Contains(row))
            Grid.SelectedItem = row;
    }

    bool _dragging, _cellPressPending;

    void DropRowSelectionKeepingBlock()
    {
        _cellPressPending = false;
        if (_vm is null || Grid.SelectedItems.Count == 0)
            return;
        Grid.SelectedItems.Clear();
        UpdateSelectionStatus();
    }

    void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _vm is null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = false;
            return;
        }
        if (Grid.InputHitTest(e.GetPosition(Grid)) is Visual hit && hit.FindAncestorOfType<GridCellView>(includeSelf: true) is { } cell)
        {
            var reach = _vm.Reach;
            if (reach is null || reach.Value.Row != _vm.RowIndexOf(cell.Row) || reach.Value.Column != cell.Column)
                _vm.ExtendTo(cell.Row, cell.Column);
        }
    }

    // Clicking a row number selects whole rows, so there is no single clicked cell any more.
    void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && e.Source is Visual visual && visual.FindAncestorOfType<DataGridRowHeader>() is not null)
            _vm.SelectCell(null, -1);

        // The small square above the row numbers selects every row, as in SSMS.
        if (_vm is not null && e.Source is Visual corner && corner.FindAncestorOfType<DataGridColumnHeader>() is { Name: "PART_TopLeftCornerHeader" })
        {
            e.Handled = true;
            SelectAllRows();
        }
    }

    void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Source is TextBox)
            return; // while a cell is being edited, keys belong to its text box

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.C)
        {
            e.Handled = true;
            var withHeaders = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            var rows = SelectedRows();
            if (_vm.ColumnSelection < 0 && _vm.HasBlock && _vm.Range is { } block && Grid.SelectedItems.Count == 0)
                _ = CopyBlockAsync(block, withHeaders);
            else if (_vm.ColumnSelection >= 0)
                _ = CopyColumnAsync(_vm.ColumnSelection, withHeaders);
            else if (withHeaders)
                _ = CopyRowsAsync(rows, withHeaders: true);
            else if (_vm.CellRow is { } cellRow && _vm.CellColumn >= 0 && rows.Count <= 1)
                _ = CopyCellAsync(cellRow, _vm.CellColumn);
            else
                _ = CopyRowsAsync(rows, withHeaders: false);
        }
        else if (command && e.Key == Key.A)
        {
            e.Handled = true;
            SelectAllRows();
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && !command && !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && MoveCell(e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.HasBlock)
        {
            e.Handled = true;
            if (_vm.CellRow is { } anchor)
                _vm.SelectCell(anchor, _vm.CellColumn);
        }
        else if (e.Key == Key.Delete && _vm.IsEditing && _vm.CellRow is { } row && _vm.CellColumn >= 0)
        {
            e.Handled = true;
            _vm.SetNull(row, _vm.CellColumn);
        }
    }

    /// <summary>Arrow keys move the selected cell; with Shift they stretch the block, as in SSMS. False when no cell is selected.</summary>
    bool MoveCell(Key key, bool extend)
    {
        if (_vm is null || _vm.Rows.Count == 0 || _vm.CellRow is null || Grid.SelectedItems.Count > 0 || _vm.ColumnSelection >= 0)
            return false;
        if (_vm.Reach is not { } from)
            return false;
        var (row, column) = from;
        switch (key)
        {
            case Key.Up: row--; break;
            case Key.Down: row++; break;
            case Key.Left: column--; break;
            default: column++; break;
        }
        row = Math.Clamp(row, 0, _vm.Rows.Count - 1);
        column = Math.Clamp(column, 0, _vm.Data.Columns.Count - 1);
        var target = _vm.Rows[row];
        if (extend)
            _vm.ExtendTo(target, column);
        else
            _vm.SelectCell(target, column);
        Grid.ScrollIntoView(target, Grid.Columns[column]);
        return true;
    }

    List<GridRow> SelectedRows()
    {
        var rows = Grid.SelectedItems.OfType<GridRow>().ToList();
        if (rows.Count == 0 && _vm?.CellRow is { } cell)
            rows.Add(cell);
        if (rows.Count > 1 && _vm is not null)
        {
            // The grid lists a selection in the order it was clicked; a copy should read top to bottom, as on screen.
            var position = new Dictionary<GridRow, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < _vm.Rows.Count; i++)
                position[_vm.Rows[i]] = i;
            rows.Sort((a, b) => position.GetValueOrDefault(a, int.MaxValue).CompareTo(position.GetValueOrDefault(b, int.MaxValue)));
        }
        return rows;
    }

    // ---- selecting ----

    void SelectAllRows()
    {
        if (_vm is null)
            return;
        Grid.SelectAll();
        _vm.SelectCell(null, -1);
        UpdateSelectionStatus();
    }

    void OnGridSelectionChanged()
    {
        // Picking rows (even with the arrow keys) replaces a column selection or a block of cells.
        if (_vm is not null && !_cellPressPending && Grid.SelectedItems.Count > 0 && (_vm.ColumnSelection >= 0 || _vm.HasBlock))
            _vm.SelectCell(null, -1);
        UpdateSelectionStatus();
    }

    void UpdateSelectionStatus()
    {
        if (_vm is null)
        {
            SelectionStatus.Text = "";
            return;
        }
        var count = Grid.SelectedItems.Count;
        SelectionStatus.Text = _vm.ColumnSelectionName is { } name ? $"Column “{name}” selected"
            : _vm.Range is { } r && _vm.HasBlock ? $"{CopyFeedback.Number((r.LastRow - r.FirstRow + 1) * (r.LastColumn - r.FirstColumn + 1))} cells selected ({r.LastRow - r.FirstRow + 1} × {r.LastColumn - r.FirstColumn + 1})"
            : count > 0 ? $"{CopyFeedback.Number(count)} selected"
            : "";
    }

    void UpdateHeaderHighlight()
    {
        var accent = this.TryFindResource("AccentBrush", ActualThemeVariant, out var b) && b is ISolidColorBrush s ? s.Color : (Color?)null;
        for (var i = 0; i < _headers.Count; i++)
        {
            var inBlock = _vm?.Range is { } r && _vm.HasBlock && i >= r.FirstColumn && i <= r.LastColumn;
            _headers[i].Background = accent is { } c && (_vm?.ColumnSelection == i || inBlock) ? new SolidColorBrush(c, 0.22) : Brushes.Transparent;
        }
    }

    /// <summary>A plain click sorts by the column. ⌘ or Ctrl + click selects the whole column instead, and again to deselect it.</summary>
    public void OnHeaderClicked(int index, KeyModifiers modifiers)
    {
        if (_vm is null)
            return;
        if (modifiers.HasFlag(KeyModifiers.Meta) || modifiers.HasFlag(KeyModifiers.Control))
            _vm.SelectColumn(_vm.ColumnSelection == index ? -1 : index);
        else
            _vm.ToggleSort(index);
    }

    ContextMenu BuildHeaderMenu(int index)
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
        return new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Select column", () => _vm?.SelectColumn(index)),
                new Separator(),
                Item("Copy column", () => _ = CopyColumnAsync(index, withHeader: false)),
                Item("Copy column with header", () => _ = CopyColumnAsync(index, withHeader: true)),
            },
        };
    }

    // ---- copying, always with a message saying what was copied ----

    async Task CopyAsync(string text, string what)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            ShowCopyMessage("Couldn't reach the clipboard", isError: true);
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            ShowCopyMessage(CopyFeedback.Message(what, text), isError: false);
        }
        catch (Exception ex)
        {
            ShowCopyMessage("Couldn't copy: " + ex.Message, isError: true);
        }
    }

    /// <summary>Shows the message beside the row count for a few seconds.</summary>
    public void ShowCopyMessage(string message, bool isError)
    {
        CopyStatus.Text = message;
        CopyStatus.Classes.Remove("error");
        if (isError)
            CopyStatus.Classes.Add("error");
        CopyIcon.IsVisible = !isError;
        CopyBadge.IsVisible = true;

        _copyTimer?.Stop();
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isError ? 6 : 4) };
        _copyTimer.Tick += (_, _) =>
        {
            _copyTimer?.Stop();
            CopyBadge.IsVisible = false;
        };
        _copyTimer.Start();
    }

    Task CopyCellAsync(GridRow row, int column) =>
        CopyAsync(GridCopy.Cell(_vm!.Data, row, column), $"cell “{_vm.Data.Columns[column].Name}”");

    Task CopyRowsAsync(IReadOnlyList<GridRow> rows, bool withHeaders)
    {
        if (_vm is null)
            return Task.CompletedTask;
        if (rows.Count == 0)
        {
            ShowCopyMessage("Nothing selected. Click a row number or press ⌘A, or use Copy for the whole result.", isError: true);
            return Task.CompletedTask;
        }
        return CopyAsync(GridCopy.Rows(_vm.Data, rows, withHeaders), CopyFeedback.Rows(rows.Count) + (withHeaders ? " with headers" : ""));
    }

    Task CopyBlockAsync((int FirstRow, int LastRow, int FirstColumn, int LastColumn) block, bool withHeaders)
    {
        if (_vm is null)
            return Task.CompletedTask;
        var rows = new List<GridRow>();
        for (var i = block.FirstRow; i <= block.LastRow; i++)
            rows.Add(_vm.Rows[i]);
        var columns = block.LastColumn - block.FirstColumn + 1;
        return CopyAsync(
            GridCopy.Range(_vm.Data, rows, block.FirstColumn, block.LastColumn, withHeaders),
            $"{CopyFeedback.Number(rows.Count * columns)} cells ({rows.Count} × {columns}){(withHeaders ? " with headers" : "")}");
    }

    Task CopyColumnAsync(int column, bool withHeader)
    {
        if (_vm is null || column < 0 || column >= _vm.Data.Columns.Count)
            return Task.CompletedTask;
        var name = _vm.Data.Columns[column].Name;
        return CopyAsync(
            GridCopy.Column(_vm.Data, _vm.Rows, column, withHeader),
            $"column “{name}”{(withHeader ? " with header" : "")} · {CopyFeedback.Values(_vm.Rows.Count)}");
    }

    // ---- the Copy menu: the whole result as text ----

    void OnCopyAgentMarkdown(object? sender, RoutedEventArgs e) => _ = CopyForAgentAsync(AgentFormat.Markdown);
    void OnCopyAgentJson(object? sender, RoutedEventArgs e) => _ = CopyForAgentAsync(AgentFormat.Json);
    void OnCopyMarkdown(object? sender, RoutedEventArgs e) => _ = CopyWholeAsync("as a Markdown table", (set, rows) => ResultText.Markdown(set, rows));
    void OnCopyCsv(object? sender, RoutedEventArgs e) => _ = CopyWholeAsync("as CSV", ResultText.Csv);
    void OnCopyTsv(object? sender, RoutedEventArgs e) => _ = CopyWholeAsync("as tab-separated text", (set, rows) => GridCopy.Rows(set, rows, withHeaders: true));
    void OnCopyJsonAll(object? sender, RoutedEventArgs e) => _ = CopyWholeAsync("as JSON", GridCopy.Json);

    /// <summary>Copies the rows in view, so an active filter or sort is respected.</summary>
    async Task CopyWholeAsync(string how, Func<ResultSet, IReadOnlyList<GridRow>, string> build)
    {
        if (_vm is null)
            return;
        var (set, rows) = (_vm.Data, _vm.Rows);
        if (rows.Count == 0)
        {
            ShowCopyMessage("There are no rows to copy", isError: true);
            return;
        }
        if (rows.Count > 20_000)
            ShowCopyMessage($"Preparing {CopyFeedback.Rows(rows.Count)}…", isError: false);
        var text = await Task.Run(() => build(set, rows));
        await CopyAsync(text, $"{CopyFeedback.Rows(rows.Count)} {how}");
    }

    async Task CopyForAgentAsync(AgentFormat format)
    {
        if (_vm is null)
            return;
        var (set, rows, context) = (_vm.Data, _vm.Rows, _vm.BuildAgentContext());
        if (rows.Count == 0)
        {
            ShowCopyMessage("There are no rows to copy", isError: true);
            return;
        }
        var copy = await Task.Run(() => ResultText.ForAgent(set, rows, context, format));
        var kind = format == AgentFormat.Markdown ? "Markdown" : "JSON";
        await CopyAsync(copy.Text, copy.Cut
            ? $"for an AI agent ({kind}): only the first {CopyFeedback.Number(copy.RowsIncluded)} of {CopyFeedback.Number(copy.RowsAvailable)} rows, and the text says so"
            : $"{CopyFeedback.Rows(copy.RowsIncluded)} for an AI agent ({kind}), with the query");
    }

    void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // While editing, a double-click starts the edit; otherwise it opens the full value.
        if (_vm is null || _vm.IsEditing || _vm.CellRow is not { } row || _vm.CellColumn < 0)
            return;
        if (e.Source is Visual visual && visual.FindAncestorOfType<DataGridCell>() is not null)
            ShowValue(row, _vm.CellColumn);
    }

    void ShowValue(GridRow row, int column)
    {
        if (_vm is null || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var col = _vm.Data.Columns[column];
        new CellViewerWindow(col.Name, col.TypeName, GridCopy.Cell(_vm.Data, row, column)).Show(owner);
    }

    // ---- right-click menu ----

    void BuildContextMenu()
    {
        MenuItem Item(string header, EventHandler<RoutedEventArgs> click)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            return item;
        }

        _copyCell = Item("Copy cell", (_, _) => { if (_vm?.CellRow is { } r) _ = CopyCellAsync(r, _vm.CellColumn); });
        _copyRow = Item("Copy row", (_, _) => _ = CopyRowsAsync(SelectedRows(), false));
        _copyRowHeaders = Item("Copy row with headers", (_, _) => _ = CopyRowsAsync(SelectedRows(), true));
        _copyColumn = Item("Copy column", (_, _) => { if (_vm is { CellColumn: >= 0 } vm) _ = CopyColumnAsync(vm.CellColumn, false); });
        _copyColumnHeader = Item("Copy column with header", (_, _) => { if (_vm is { CellColumn: >= 0 } vm) _ = CopyColumnAsync(vm.CellColumn, true); });
        _copyJson = Item("Copy as JSON", (_, _) =>
        {
            if (_vm is null) return;
            var rows = SelectedRows();
            _ = CopyAsync(GridCopy.Json(_vm.Data, rows), $"{CopyFeedback.Rows(rows.Count)} as JSON");
        });
        _copyInsert = Item("Copy as INSERT", async (_, _) =>
        {
            if (_vm is null) return;
            var rows = SelectedRows();
            await CopyAsync(await _vm.InsertScriptAsync(rows), $"{CopyFeedback.Rows(rows.Count)} as INSERT statements");
        });
        _selectColumn = Item("Select column", (_, _) => { if (_vm is { CellColumn: >= 0 } vm) vm.SelectColumn(vm.CellColumn); });
        _selectAll = Item("Select all rows", (_, _) => SelectAllRows());
        _view = Item("View value…", (_, _) => { if (_vm?.CellRow is { } r && _vm.CellColumn >= 0) ShowValue(r, _vm.CellColumn); });
        _edit = Item("Edit cell", (_, _) => Grid.BeginEdit());
        _setNull = Item("Set to NULL", (_, _) => { if (_vm?.CellRow is { } r) _vm.SetNull(r, _vm.CellColumn); });
        _useDefault = Item("Use the default value", (_, _) => { if (_vm?.CellRow is { } r) _vm.UseDefault(r, _vm.CellColumn); });
        _delete = Item("Delete row", (_, _) => _vm?.DeleteRowsCommand.Execute(null));
        _editSeparator = new Separator();

        var menu = new ContextMenu
        {
            ItemsSource = new object[] { _copyCell, _copyRow, _copyRowHeaders, _copyColumn, _copyColumnHeader, _copyJson, _copyInsert, new Separator(), _selectColumn, _selectAll, new Separator(), _view, _editSeparator, _edit, _setNull, _useDefault, _delete },
        };
        menu.Opening += (_, _) => UpdateContextMenu();
        Grid.ContextMenu = menu;
    }

    void UpdateContextMenu()
    {
        if (_vm is null)
            return;
        var row = _vm.CellRow;
        var hasCell = row is not null && _vm.CellColumn >= 0;
        var editing = _vm.IsEditing;
        var canEdit = hasCell && _vm.CanEditCell(row!, _vm.CellColumn);

        _copyCell.IsEnabled = _view.IsEnabled = _copyColumn.IsEnabled = _copyColumnHeader.IsEnabled = _selectColumn.IsEnabled = hasCell;
        _editSeparator.IsVisible = editing;
        _edit.IsVisible = _setNull.IsVisible = editing;
        _edit.IsEnabled = canEdit;
        _setNull.IsEnabled = canEdit && _vm.Target!.Columns[_vm.CellColumn].Nullable;
        _useDefault.IsVisible = editing && row?.State == RowState.Added;
        _useDefault.IsEnabled = canEdit && row is { } r && r.IsAssigned(_vm.CellColumn);
        _delete.IsVisible = editing;
        _delete.Header = row?.State == RowState.Deleted ? "Undo delete" : row?.State == RowState.Added ? "Remove new row" : "Delete row";
    }

    // ---- editing ----

    void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_vm is null || e.Row.DataContext is not GridRow row || !_vm.CanEditCell(row, IndexOf(e.Column)))
            e.Cancel = true;
    }

    void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_vm is null || e.EditAction != DataGridEditAction.Commit || e.EditingElement is not TextBox box || e.Row.DataContext is not GridRow row)
            return;
        if (!_vm.TrySetCell(row, IndexOf(e.Column), box.Text ?? "", out var error))
        {
            e.Cancel = true; // stay in the cell so the value can be fixed
            _vm.ShowError(error);
        }
    }

    void OnRowAdded(GridRow row)
    {
        // The grid is rebuilt for the new row; wait until it has laid out before scrolling and editing.
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm?.Target is not { } target)
                return;
            var column = target.Columns.ToList().FindIndex(c => c.CanEditNew);
            if (column < 0)
                return;
            Grid.SelectedItem = row;
            Grid.ScrollIntoView(row, Grid.Columns[column]);
            _vm.SelectCell(row, column);
            Grid.CurrentColumn = Grid.Columns[column];
            Grid.BeginEdit();
        }, DispatcherPriority.Background);
    }

    DataGridTemplateColumn BuildColumn(ResultSetViewModel vm, int index)
    {
        var column = vm.Data.Columns[index];
        var numeric = CellFormatter.IsNumeric(column.TypeName);

        // A click on the header sorts by this column. The handler also sees clicks the grid already handled.
        var arrow = new Icon { Kind = "chevron-up", Width = 12, Height = 12, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _sortIcons.Add(arrow);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Background = Brushes.Transparent,
            HorizontalAlignment = numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Children =
            {
                // A TextBlock header keeps underscores in names like user_id from being eaten as access keys.
                new TextBlock { Text = column.Name, FontWeight = FontWeight.SemiBold },
                arrow,
            },
        };
        header.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                OnHeaderClicked(index, e.KeyModifiers);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        header.ContextMenu = BuildHeaderMenu(index);
        _headers.Add(header);

        return new DataGridTemplateColumn
        {
            Header = header,
            CellTemplate = new FuncDataTemplate<GridRow>((row, _) => row is null ? null : new GridCellView(row, index, vm)),
            CellEditingTemplate = new FuncDataTemplate<GridRow>((row, _) => row is null ? null : BuildEditor(vm, row, index)),
            IsReadOnly = false,
            MinWidth = 60,
            MaxWidth = 700,
        };
    }

    static TextBox BuildEditor(ResultSetViewModel vm, GridRow row, int index)
    {
        var text = !row.IsAssigned(index) || row[index] is null ? "" : CellFormatter.Format(row[index], vm.Data.Columns[index].TypeName);
        var box = new TextBox
        {
            Text = text,
            VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = row[index] is null && row.IsAssigned(index) ? "NULL" : null,
        };
        box.AttachedToVisualTree += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return box;
    }

    void OnExportCsv(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Csv);
    void OnExportExcel(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Excel);
    void OnExportJson(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Json);

    async Task ExportAsync(ExportFormat format)
    {
        if (DataContext is not ResultSetViewModel vm || TopLevel.GetTopLevel(this) is not { } top)
            return;

        var extension = ResultExporter.Extension(format);
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export result",
            SuggestedFileName = $"result.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(format.ToString()) { Patterns = [$"*.{extension}"] }],
        });
        if (file is null)
            return;

        ExportStatus.Text = "Exporting…";
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0); // overwriting a longer file must not leave its old tail behind
            await Task.Run(() => ResultExporter.Write(vm.Data, format, stream));
            ExportStatus.Text = $"Saved {file.Name} ({vm.Data.Rows.Count:N0} rows)" +
                (vm.Data.Truncated ? ". The grid was cut at the row limit, so the file is too." : "");
        }
        catch (Exception ex)
        {
            ExportStatus.Text = "";
            if (TopLevel.GetTopLevel(this) is Window owner)
                await Dialogs.ConfirmAsync(owner, "Could not export", ex.Message, "OK", showCancel: false);
        }
    }


}
