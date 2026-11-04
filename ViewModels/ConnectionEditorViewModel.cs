using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public sealed record AuthChoice(AuthMode Mode, string Label);

public sealed record EnvironmentChoice(ServerEnvironment Value, string Label);

public sealed partial class ConnectionEditorViewModel : ViewModelBase
{
    readonly ICredentialStore _credentials;
    readonly Guid _id;
    readonly bool _isNew;

    public ConnectionEditorViewModel(ConnectionProfile? existing, ICredentialStore credentials)
    {
        _credentials = credentials;
        var p = existing ?? new ConnectionProfile();
        _isNew = existing is null;
        _id = p.Id;
        Name = p.Name;
        Server = p.Server;
        SelectedAuth = AuthChoices.First(c => c.Mode == p.Auth);
        Username = p.Username;
        DefaultDatabase = p.DefaultDatabase ?? "";
        Encrypt = p.Encrypt;
        TrustServerCertificate = p.TrustServerCertificate;
        SelectedEnvironment = EnvironmentChoices.First(c => c.Value == p.Environment);
    }

    public IReadOnlyList<AuthChoice> AuthChoices { get; } =
    [
        new(AuthMode.SqlLogin, "SQL Server login"),
        new(AuthMode.EntraInteractive, "Microsoft Entra ID (browser sign-in)"),
    ];

    public IReadOnlyList<EnvironmentChoice> EnvironmentChoices { get; } =
    [
        new(ServerEnvironment.None, "Not set"),
        new(ServerEnvironment.Development, "Development"),
        new(ServerEnvironment.Staging, "Staging"),
        new(ServerEnvironment.Production, "Production"),
    ];

    public string Heading => _isNew ? "Add a server" : "Edit server";
    public string PasswordHint => _isNew ? "Password" : "Password (leave blank to keep the saved one)";

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))] public partial string Server { get; set; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowPassword))] public partial AuthChoice SelectedAuth { get; set; }
    [ObservableProperty] public partial string Username { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string DefaultDatabase { get; set; }
    [ObservableProperty] public partial bool Encrypt { get; set; }
    [ObservableProperty] public partial bool TrustServerCertificate { get; set; }
    [ObservableProperty] public partial EnvironmentChoice SelectedEnvironment { get; set; }
    [ObservableProperty] public partial string TestResult { get; set; } = "";
    [ObservableProperty] public partial bool TestSucceeded { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TestCommand))] public partial bool IsTesting { get; set; }

    public bool ShowPassword => SelectedAuth.Mode == AuthMode.SqlLogin;
    public bool CanSave => !string.IsNullOrWhiteSpace(Server);

    public ConnectionProfile ToProfile() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        Server = Server.Trim(),
        Auth = SelectedAuth.Mode,
        Username = Username.Trim(),
        DefaultDatabase = string.IsNullOrWhiteSpace(DefaultDatabase) ? null : DefaultDatabase.Trim(),
        Encrypt = Encrypt,
        TrustServerCertificate = TrustServerCertificate,
        Environment = SelectedEnvironment.Value,
    };

    bool CanTest() => !IsTesting && CanSave;

    [RelayCommand(CanExecute = nameof(CanTest))]
    async Task TestAsync()
    {
        IsTesting = true;
        TestResult = "Testing…";
        TestSucceeded = false;
        try
        {
            var profile = ToProfile();
            var password = Password.Length > 0 ? Password : await Task.Run(() => _credentials.Get(_id));
            await using var connection = new SqlConnection(ConnectionFactory.BuildConnectionString(profile, password));
            await connection.OpenAsync();
            TestResult = $"Connected to SQL Server {connection.ServerVersion}";
            TestSucceeded = true;
        }
        catch (Exception ex)
        {
            TestResult = $"Couldn't connect. {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }
}
