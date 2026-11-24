using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Services;
using SqlStudio.Services.Company;

namespace SqlStudio.ViewModels;

/// <summary>The company sign-in form: email and password. The password is never kept past the attempt.</summary>
public sealed partial class CompanySignInViewModel : ViewModelBase
{
    readonly CompanySession _session;

    public CompanySignInViewModel(CompanySession session, SettingsStore settings)
    {
        _session = session;
        Email = settings.Load().Company.LastEmail;
    }

    [ObservableProperty]
    public partial string Email { get; set; } = "";

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsBusy { get; set; }

    public bool HasError => Error is not null;
    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Raised after a successful sign-in, so a hosting window can close.</summary>
    public event Action? Succeeded;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    async Task SignInAsync()
    {
        var email = Email.Trim();
        var password = Password;
        if (email.Length == 0 || password.Length == 0)
        {
            Error = "Enter your company email and password.";
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            await _session.SignInAsync(email, password, default);
            Password = "";
            Succeeded?.Invoke();
        }
        catch (CompanyApiException ex)
        {
            Error = ex.Message;
        }
        catch (Exception)
        {
            Error = "Couldn't sign in. Try again.";
        }
        finally
        {
            Password = ""; // never kept, whatever the outcome
            IsBusy = false;
        }
    }

    bool CanSignIn() => !IsBusy;
}
