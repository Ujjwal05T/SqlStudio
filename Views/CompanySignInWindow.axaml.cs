using Avalonia.Controls;
using Avalonia.Interactivity;
using SqlStudio.ViewModels;

namespace SqlStudio.Views;

public partial class CompanySignInWindow : Window
{
    public CompanySignInWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CompanySignInViewModel vm)
            vm.Succeeded += Close;
    }

    void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
