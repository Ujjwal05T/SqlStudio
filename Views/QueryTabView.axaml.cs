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

        _textMate = Editor.InstallTextMate(_registry);
        _textMate.SetGrammar(_registry.GetScopeByLanguageId(_registry.GetLanguageByExtension(".sql").Id));
        ApplyEditorTheme();
        AddGutterSpacing();
        ActualThemeVariantChanged += (_, _) => ApplyEditorTheme();

        Editor.TextChanged += (_, _) => UpdateHint();
        Editor.TextArea.TextEntered += OnTextEntered;
        BuildSnippetsMenu();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    QueryTabViewModel? Vm => DataContext as QueryTabViewModel;

    void ApplyEditorTheme()
    {
        // Syntax colours follow light/dark; the card behind the editor follows the chosen palette.
        var theme = ThemeService.Current.IsDark ? ThemeName.DarkPlus : ThemeName.LightPlus;
        _textMate.SetTheme(_registry.LoadTheme(theme));
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
        if (_completion is not null || Vm is null || string.IsNullOrEmpty(e.Text))
            return;
        var c = e.Text[0];
        if (c is '.' or '[' or '_' || char.IsLetter(c))
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
        window.CompletionList.SelectItem(result.Prefix);
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

        var run = e.Key == Key.F5 || (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Meta));
        var stop = e.Key == Key.OemPeriod && e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (run && vm.ExecuteCommand.CanExecute(null))
            vm.ExecuteCommand.Execute(null);
        else if (stop && vm.CancelCommand.CanExecute(null))
            vm.CancelCommand.Execute(null);
        else if (!run && !stop)
            return;

        e.Handled = true;
    }
}
