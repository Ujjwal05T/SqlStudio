using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class SchemaCompareWindow : Window
{
    public SchemaCompareWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            if (DataContext is SchemaCompareViewModel { IsComparing: true } vm)
                vm.CancelCommand.Execute(null); // do not leave queries running behind a closed window
        };
    }

    async void OnCopyReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SchemaCompareViewModel vm && Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.BuildReport());
    }

    void OnShowDiagram(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SchemaCompareViewModel vm && vm.CreateDiagram() is { } diagram)
            new ErDiagramWindow { DataContext = diagram }.Show(this);
    }
}
