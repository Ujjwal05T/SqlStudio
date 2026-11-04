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
    Separator _editSeparator = null!;

    public ResultSetView()
    {
        InitializeComponent();
        Grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.Index + 1).ToString();
        Grid.CellPointerPressed += OnCellPointerPressed;
        Grid.BeginningEdit += OnBeginningEdit;
        Grid.CellEditEnding += OnCellEditEnding;
        Grid.DoubleTapped += OnGridDoubleTapped;
        Grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
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
        _vm.SelectCell(row, IndexOf(e.Column));
        // A right-click on a row outside the selection selects that row, so the menu acts on what was clicked.
        if (e.PointerPressedEventArgs.GetCurrentPoint(this).Properties.IsRightButtonPressed && !Grid.SelectedItems.Contains(row))
            Grid.SelectedItem = row;
    }

    // Clicking a row number selects whole rows, so there is no single clicked cell any more.
    void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && e.Source is Visual visual && visual.FindAncestorOfType<DataGridRowHeader>() is not null)
            _vm.SelectCell(null, -1);
    }

    void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Source is TextBox)
            return; // while a cell is being edited, keys belong to its text box

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key == Key.C)
        {
            e.Handled = true;
            var rows = SelectedRows();
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                _ = CopyAsync(GridCopy.Rows(_vm.Data, rows, withHeaders: true));
            else if (_vm.CellRow is { } cellRow && _vm.CellColumn >= 0 && rows.Count <= 1)
                _ = CopyAsync(GridCopy.Cell(_vm.Data, cellRow, _vm.CellColumn));
            else
                _ = CopyAsync(GridCopy.Rows(_vm.Data, rows, withHeaders: false));
        }
        else if (e.Key == Key.Delete && _vm.IsEditing && _vm.CellRow is { } row && _vm.CellColumn >= 0)
        {
            e.Handled = true;
            _vm.SetNull(row, _vm.CellColumn);
        }
    }

    List<GridRow> SelectedRows()
    {
        var rows = Grid.SelectedItems.OfType<GridRow>().ToList();
        if (rows.Count == 0 && _vm?.CellRow is { } cell)
            rows.Add(cell);
        return rows;
    }

    async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
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

        _copyCell = Item("Copy cell", (_, _) => { if (_vm?.CellRow is { } r) _ = CopyAsync(GridCopy.Cell(_vm.Data, r, _vm.CellColumn)); });
        _copyRow = Item("Copy row", (_, _) => { if (_vm is not null) _ = CopyAsync(GridCopy.Rows(_vm.Data, SelectedRows(), false)); });
        _copyRowHeaders = Item("Copy row with headers", (_, _) => { if (_vm is not null) _ = CopyAsync(GridCopy.Rows(_vm.Data, SelectedRows(), true)); });
        _copyColumn = Item("Copy column", (_, _) => { if (_vm is not null && _vm.CellColumn >= 0) _ = CopyAsync(GridCopy.Column(_vm.Data, _vm.Rows, _vm.CellColumn, false)); });
        _copyJson = Item("Copy as JSON", (_, _) => { if (_vm is not null) _ = CopyAsync(GridCopy.Json(_vm.Data, SelectedRows())); });
        _copyInsert = Item("Copy as INSERT", async (_, _) => { if (_vm is not null) await CopyAsync(await _vm.InsertScriptAsync(SelectedRows())); });
        _view = Item("View value…", (_, _) => { if (_vm?.CellRow is { } r && _vm.CellColumn >= 0) ShowValue(r, _vm.CellColumn); });
        _edit = Item("Edit cell", (_, _) => Grid.BeginEdit());
        _setNull = Item("Set to NULL", (_, _) => { if (_vm?.CellRow is { } r) _vm.SetNull(r, _vm.CellColumn); });
        _useDefault = Item("Use the default value", (_, _) => { if (_vm?.CellRow is { } r) _vm.UseDefault(r, _vm.CellColumn); });
        _delete = Item("Delete row", (_, _) => _vm?.DeleteRowsCommand.Execute(null));
        _editSeparator = new Separator();

        var menu = new ContextMenu
        {
            ItemsSource = new object[] { _copyCell, _copyRow, _copyRowHeaders, _copyColumn, _copyJson, _copyInsert, new Separator(), _view, _editSeparator, _edit, _setNull, _useDefault, _delete },
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

        _copyCell.IsEnabled = _view.IsEnabled = _copyColumn.IsEnabled = hasCell;
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
                vm.ToggleSort(index);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

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
