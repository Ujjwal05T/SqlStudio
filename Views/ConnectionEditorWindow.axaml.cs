using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SqlStudio.Views;

public partial class ConnectionEditorWindow : Window
{
    public ConnectionEditorWindow() => InitializeComponent();

    void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
