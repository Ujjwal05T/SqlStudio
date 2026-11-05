using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlStudio.Models;
using SqlStudio.Services.Mcp;

namespace SqlStudio.ViewModels;

/// <summary>One saved server and whether Claude may use it.</summary>
public sealed partial class ClaudeConnectionRow : ObservableObject
{
    readonly ClaudeAccess _access;
    readonly ConnectionProfile _profile;

    public ClaudeConnectionRow(ClaudeAccess access, ConnectionProfile profile)
    {
        _access = access;
        _profile = profile;
        Allowed = access.IsAllowed(profile);
    }

    public string Name => _profile.DisplayName;
    public string Server => _profile.Server;
    public bool HasEnv => _profile.HasEnvironment;
    public string EnvShort => _profile.EnvironmentShort;
    public bool IsProd => _profile.Environment == ServerEnvironment.Production;

    [ObservableProperty]
    public partial bool Allowed { get; set; }

    partial void OnAllowedChanged(bool value) => _access.SetAllowed(_profile, value);
}

public sealed partial class ClaudeConnectViewModel : ViewModelBase
{
    readonly ClaudeAccess _access;

    public ClaudeConnectViewModel(ClaudeAccess access, IReadOnlyList<ConnectionProfile>? profiles = null)
    {
        _access = access;
        IsEnabled = access.IsEnabled;
        foreach (var profile in profiles ?? [])
            Connections.Add(new ClaudeConnectionRow(access, profile));
        access.Changed += Refresh;
    }

    public ObservableCollection<ClaudeConnectionRow> Connections { get; } = [];
    public ObservableCollection<McpActivity> Activity => _access.Activity;
    public bool HasConnections => Connections.Count > 0;
    public string AddCommand => IsEnabled ? _access.AddCommand : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddCommand), nameof(StatusText), nameof(HasError))]
    public partial bool IsEnabled { get; set; }

    public string StatusText => !IsEnabled
        ? "Off. Claude cannot reach SqlStudio."
        : _access.IsListening
            ? $"On. Listening on this computer only (port {_access.Port})."
            : _access.Error ?? "Starting…";

    public bool HasError => IsEnabled && !_access.IsListening && _access.Error is not null;

    partial void OnIsEnabledChanged(bool value)
    {
        if (value != _access.IsEnabled)
            _access.SetEnabled(value);
    }

    void Refresh()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(AddCommand));
    }
}
