using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.TextMate;
using SqlStudio.Services;
using SqlStudio.Services.Completion;
using SqlStudio.ViewModels;
using TextMateSharp.Grammars;

namespace SqlStudio.Views;

public partial class QueryTabView : UserControl
{
    readonly RegistryOptions _registry = new(ThemeName.DarkPlus);
    readonly TextMate.Installation _textMate;

    public QueryTabView()
    {
        InitializeComponent();
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CaretText.Text = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";

        _textMate = Editor.InstallTextMate(_registry);
        _textMate.SetGrammar(_registry.GetScopeByLanguageId(_registry.GetLanguageByExtension(".sql").Id));
        ApplyEditorTheme();
        AddGutterSpacing();
        ActualThemeVariantChanged += (_, _) => ApplyEditorTheme();

        Editor.TextChanged += (_, _) => { UpdateHint(); UpdateHighlighting(); };
        Editor.TextArea.TextEntered += OnTextEntered;
        BuildSnippetsMenu();
        BuildPlanMenu();

        DataContextChanged += (_, _) =>
        {
            if (Vm is { } vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(QueryTabViewModel.ShowResults))
                        ApplyResultsVisibility();
                };
            ApplyResultsVisibility();
        };

        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    // The editor/results split the user dragged is remembered, so closing the results or running again brings back the same layout.
    const double MinPaneHeight = 120;
    GridLength _editorHeight = new(1, GridUnitType.Star);
    GridLength _resultsHeight = new(1, GridUnitType.Star);
    bool _resultsShown;
    bool _layoutApplied;

    /// <summary>With nothing to show, the results row collapses to nothing and the editor takes the full height; it shares the space once there are results.</summary>
    void ApplyResultsVisibility()
    {
        var show = Vm?.ShowResults ?? false;
        if (_layoutApplied && show == _resultsShown)
            return;
        var rows = ((Grid)ResultsCard.Parent!).RowDefinitions;
        if (!show && _layoutApplied)
        {
            // keep what the user dragged (only meaningful while both panes were showing)
            _editorHeight = rows[1].Height;
            _resultsHeight = rows[3].Height;
        }
        _layoutApplied = true;
        _resultsShown = show;
        ResultsSplitter.IsVisible = show;
        ResultsCard.IsVisible = show;
        rows[1].MinHeight = show ? MinPaneHeight : 0;   // neither pane can be dragged away to nothing
        rows[3].MinHeight = show ? MinPaneHeight : 0;
        rows[2].Height = show ? new GridLength(8) : new GridLength(0);
        rows[1].Height = show ? _editorHeight : new GridLength(1, GridUnitType.Star);
        rows[3].Height = show ? _resultsHeight : new GridLength(0);
    }

    QueryTabViewModel? Vm => DataContext as QueryTabViewModel;

    void ApplyEditorTheme()
    {
        // Text, selection, operators and syntax colours are all built from the chosen palette (see EditorTheme).
        var palette = ThemeService.Current;
        _textMate.SetTheme(EditorTheme.Load(palette));
        var colors = EditorTheme.For(palette);
        var area = Editor.TextArea;
        area.SelectionBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colors.Selection));
        area.SelectionForeground = null;     // keep each token's own colour inside the selection
        area.SelectionBorder = null;
        area.Caret.CaretBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colors.Foreground));
        Editor.Background = Avalonia.Media.Brushes.Transparent; // TextMate paints its own background; let the card show through
    }

    /// <summary>Breathing room on both sides of the line-number divider, so digits and code do not touch it.</summary>
    void AddGutterSpacing()
    {
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>())
            margin.Margin = new Thickness(6, 0, 6, 0);
        Editor.TextArea.LeftMargins.Add(new Border { Width = 10 });
    }

    // ---- suggestions (IntelliSense) ----

    CompletionWindow? _completion;
    int _completionRequest;

    public bool IsCompletionOpen => _completion is not null;

    /// <summary>Typing a letter, a dot or an opening bracket asks for suggestions, like SSMS does.</summary>
    void OnTextEntered(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        if (Vm is null || string.IsNullOrEmpty(e.Text))
            return;
        var c = e.Text[0];
        // The popup is rebuilt on every keystroke (it does not filter by itself), so fuzzy matches stay in the list as you type.
        if (c is '.' or '[' or '_' || char.IsLetterOrDigit(c))
            _ = ShowCompletionAsync(manual: false);
    }

    public async Task ShowCompletionAsync(bool manual)
    {
        if (Vm is not { } vm)
            return;
        var request = ++_completionRequest;
        var caret = Editor.CaretOffset;

        // Only the text around the cursor is analysed, so typing stays instant in very large scripts.
        const int around = 8000;
        var doc = Editor.Document;
        var start = Math.Max(0, caret - around);
        var text = doc.GetText(start, Math.Min(doc.TextLength, caret + around) - start);

        CompletionResult? result;
        try
        {
            result = await vm.GetCompletionAsync(text, caret - start, manual);
        }
        catch (Exception)
        {
            return;
        }

        // The user may have typed on, moved, or asked again while the names were loading.
        if (result is null || request != _completionRequest || Editor.CaretOffset != caret || !ReferenceEquals(Vm, vm))
            return;

        _completion?.Close();
        var before = result.InBracket ? 1 : 0;
        var window = new CompletionWindow(Editor.TextArea) { StartOffset = start + result.ReplaceStart + before, Width = 460 };
        var count = result.Items.Count;
        for (var i = 0; i < count; i++)
            window.CompletionList.CompletionData.Add(
                new SqlCompletionData(result.Items[i], count - i, before, result.ExtendAfter, OnSuggestionInserted));
        window.CompletionList.IsFiltering = false; // the provider already matched and ranked everything, including non-prefix matches
        if (window.CompletionList.CompletionData.Count > 0)
            window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completion, window))
                _completion = null;
        };
        _completion = window;
        window.Show();
    }

    // Choosing a schema inserts "sales." so the next list (its tables) opens straight away.
    void OnSuggestionInserted(string inserted)
    {
        if (inserted.EndsWith('.'))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ShowCompletionAsync(manual: false));
    }

    // TextMate colours text on the UI thread, and its cost explodes on very long lines (minified SQL, pasted result dumps), which
    // froze the window long enough for Windows to call the app "not responding". Such text is shown plain instead.
    const int MaxHighlightLineLength = 2000;
    const int MaxHighlightLength = 400_000;
    bool _highlighting = true;

    void UpdateHighlighting()
    {
        var doc = Editor.Document;
        var plain = doc.TextLength > MaxHighlightLength
            || (doc.TextLength > MaxHighlightLineLength && doc.Lines.Any(l => l.Length > MaxHighlightLineLength));
        if (plain == !_highlighting)
            return;
        _highlighting = !plain;
        _textMate.SetGrammar(plain ? null : _registry.GetScopeByLanguageId(_registry.GetLanguageByExtension(".sql").Id));
    }

    void UpdateHint() => EmptyHint.IsVisible = Editor.Document.TextLength == 0;

    void BuildSnippetsMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in Snippets.ByCategory)
        {
            var category = new MenuItem { Header = group.Key };
            foreach (var snippet in group)
            {
                var item = new MenuItem { Header = snippet.Name };
                item.Click += (_, _) => InsertSnippet(snippet);
                category.Items.Add(item);
            }
            menu.Items.Add(category);
        }
        SnippetsButton.Flyout = menu;
    }

    void BuildPlanMenu()
    {
        var estimated = new MenuItem { Header = "Estimated plan (does not run the query)" };
        estimated.Click += (_, _) => Vm?.ShowEstimatedPlanCommand.Execute(null);
        var actual = new MenuItem { Header = "Actual plan (runs the query)" };
        actual.Click += (_, _) => Vm?.ShowActualPlanCommand.Execute(null);
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(estimated);
        menu.Items.Add(actual);
        PlanButton.Flyout = menu;
    }

    void InsertSnippet(Services.Snippet snippet)
    {
        // Replaces the selection if there is one, otherwise inserts at the cursor.
        Editor.TextArea.Selection.ReplaceSelectionWithText(snippet.Sql.TrimEnd() + "\n");
        Editor.Focus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Vm is not { } vm)
            return;
        Editor.Text = vm.SqlText;
        UpdateHint();
        vm.ScriptProvider = () => string.IsNullOrWhiteSpace(Editor.SelectedText) ? Editor.Text : Editor.SelectedText;
        Editor.Focus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _completion?.Close();
        // The tab control rebuilds this view on every tab switch, so park the text in the view model.
        if (Vm is { } vm)
        {
            vm.SqlText = Editor.Text;
            vm.ScriptProvider = () => vm.SqlText;
        }
        base.OnDetachedFromVisualTree(e);
    }

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        // Ctrl+Space or Ctrl+J asks for suggestions on demand (⌘Space belongs to Spotlight)
        if (e.Key is Key.Space or Key.J && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = ShowCompletionAsync(manual: true);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Back or Key.Delete && _completion is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ShowCompletionAsync(manual: false));

        var run = e.Key == Key.F5 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(Shortcuts.Command));
        var stop = e.Key == Key.OemPeriod && e.KeyModifiers.HasFlag(Shortcuts.Command);
        if (run && vm.ExecuteCommand.CanExecute(null))
            vm.ExecuteCommand.Execute(null);
        else if (stop && vm.CancelCommand.CanExecute(null))
            vm.CancelCommand.Execute(null);
        else if (!run && !stop)
            return;

        e.Handled = true;
    }
}
