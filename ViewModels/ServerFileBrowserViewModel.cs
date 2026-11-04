using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.SqlClient;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public enum FileBrowseMode { Open, Save }

/// <summary>A choice in the "type" box. Extensions is empty for "all files".</summary>
public sealed record FileFilterChoice(string Label, IReadOnlyList<string> Extensions)
{
    public static readonly FileFilterChoice Backups = new("Backup files (*.bak, *.trn, *.bkp, *.dif)", ServerFileSystem.BackupExtensions);
    public static readonly FileFilterChoice DatabaseFiles = new("Database files (*.mdf, *.ndf, *.ldf)", [".mdf", ".ndf", ".ldf"]);
    public static readonly FileFilterChoice All = new("All files (*.*)", []);

    public bool Matches(string name) => Extensions.Count == 0 || Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Browses the server's file system, like SSMS's "Locate Backup File" dialog, to pick an existing file or a place to save one.</summary>
public sealed partial class ServerFileBrowserViewModel : ViewModelBase, IDisposable
{
    readonly ConnectionProfile _profile;
    readonly ICredentialStore _credentials;
    readonly string? _startPath;
    readonly string? _defaultExtension;
    readonly SemaphoreSlim _gate = new(1, 1);
    SqlConnection? _connection;
    List<ServerFileEntry> _listed = [];
    bool _windows = true;

    public ServerFileBrowserViewModel(
        ConnectionProfile profile, ICredentialStore credentials, FileBrowseMode mode, string? startPath,
        IReadOnlyList<FileFilterChoice>? filters = null, string? defaultExtension = null)
    {
        _profile = profile;
        _credentials = credentials;
        Mode = mode;
        _startPath = startPath;
        _defaultExtension = defaultExtension;
        Filters = filters ?? [FileFilterChoice.Backups, FileFilterChoice.All];
        SelectedFilter = Filters[0];
    }

    public FileBrowseMode Mode { get; }
    public bool IsSave => Mode == FileBrowseMode.Save;
    public IReadOnlyList<FileFilterChoice> Filters { get; }
    public ObservableCollection<ServerFileEntry> Entries { get; } = [];

    public string Heading => IsSave ? "Choose where to save" : "Locate a file";
    public string Subtitle => $"Folders and files on {_profile.DisplayName}, as its SQL Server service account sees them.";
    public string ChooseLabel => IsSave ? "Select" : "Open";

    /// <summary>The full path the user picked, or null if they cancelled.</summary>
    public string? Result { get; private set; }

    [ObservableProperty] public partial string CurrentPath { get; set; } = "";
    [ObservableProperty] public partial string PathText { get; set; } = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ChooseCommand))] public partial string FileName { get; set; } = "";
    [ObservableProperty] public partial ServerFileEntry? SelectedEntry { get; set; }
    [ObservableProperty] public partial FileFilterChoice SelectedFilter { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ChooseCommand), nameof(GoUpCommand))] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool StatusIsError { get; set; }

    partial void OnSelectedFilterChanged(FileFilterChoice value) => ApplyFilter();

    partial void OnSelectedEntryChanged(ServerFileEntry? value)
    {
        if (value is { IsDirectory: false })
            FileName = value.Name; // clicking a file puts its name in the box, as in any file dialog
    }

    /// <summary>Connects, works out whether the server is Windows or Linux, and opens the starting folder.</summary>
    public async Task InitializeAsync()
    {
        string? folder = null;
        var file = "";
        if (!string.IsNullOrWhiteSpace(_startPath))
        {
            var path = _startPath.Trim();
            if (ServerPath.LooksLikeFile(path))
            {
                file = ServerPath.FileName(path);
                path = path[..Math.Max(0, path.Length - file.Length)];
            }
            folder = path.Length > 0 ? path : null;
        }
        await NavigateAsync(folder, startup: true);
        if (file.Length > 0)
            FileName = file;
    }

    async Task<SqlConnection> ConnectionAsync()
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
            return _connection;
        _connection?.Dispose();
        _connection = await ServerSession.OpenAsync(_profile, _credentials, "master");
        _windows = await ServerFileSystem.IsWindowsAsync(_connection, CancellationToken.None);
        return _connection;
    }

    async Task NavigateAsync(string? path, bool startup = false)
    {
        await _gate.WaitAsync();
        IsLoading = true;
        try
        {
            var connection = await ConnectionAsync();
            List<ServerFileEntry> entries;
            string shown;

            // A starting path from the other kind of server (a Windows path on Linux) means nothing here.
            if (!string.IsNullOrWhiteSpace(path) && startup && ServerPath.LooksWindows(path) != _windows)
                path = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                shown = _windows ? "" : "/";
                entries = _windows ? await ServerFileSystem.ListDrivesAsync(connection, CancellationToken.None)
                                   : await ServerFileSystem.ListAsync(connection, "/", CancellationToken.None);
            }
            else
            {
                shown = ServerPath.Normalize(path, _windows);
                if (await ServerFileSystem.DirectoryExistsAsync(connection, shown, CancellationToken.None) == false)
                {
                    if (startup)
                    {
                        _gate.Release();
                        try { await NavigateAsync(null); } finally { await _gate.WaitAsync(); }
                        return;
                    }
                    Fail($"\"{shown}\" isn't a folder on the server.");
                    return;
                }
                entries = await ServerFileSystem.ListAsync(connection, shown, CancellationToken.None);
            }

            _listed = entries;
            CurrentPath = shown;
            PathText = shown;
            ApplyFilter();
            GoUpCommand.NotifyCanExecuteChanged();
            StatusIsError = false;
            Status = Entries.Count == 0 ? "Nothing to show here. The folder is empty, or the SQL Server service account can't see inside it." : "";
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsLoading = false;
            _gate.Release();
        }
    }

    void Fail(string message)
    {
        StatusIsError = true;
        Status = message;
        PathText = CurrentPath; // put the box back to where we really are
    }

    void ApplyFilter()
    {
        Entries.Clear();
        foreach (var entry in _listed.Where(e => e.IsDirectory || SelectedFilter.Matches(e.Name)))
            Entries.Add(entry);
    }

    [RelayCommand]
    Task GoToTypedAsync() => NavigateAsync(PathText);

    [RelayCommand]
    Task RefreshAsync() => NavigateAsync(string.IsNullOrEmpty(CurrentPath) ? null : CurrentPath);

    bool CanGoUp() => !IsLoading && !string.IsNullOrEmpty(CurrentPath) && !(!_windows && CurrentPath == "/");

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    Task GoUpAsync() => NavigateAsync(ServerPath.Parent(CurrentPath, _windows)); // null shows the drive list

    /// <summary>Double-click or Enter: a folder opens; a file is chosen (Open) or its name is filled in (Save).</summary>
    [RelayCommand]
    async Task ActivateAsync(ServerFileEntry? entry)
    {
        if (entry is null)
            return;
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.IsDrive ? entry.Name : ServerPath.Combine(CurrentPath, entry.Name, _windows));
            return;
        }
        FileName = entry.Name;
        if (!IsSave)
            Choose();
    }

    public bool CanChoose => !IsLoading && !string.IsNullOrEmpty(CurrentPath) && FileName.Trim().Length > 0;

    partial void OnCurrentPathChanged(string value) => ChooseCommand.NotifyCanExecuteChanged();

    /// <summary>Raised when a path has been picked, so the window can close.</summary>
    public event Action? Chosen;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    void Choose()
    {
        var name = FileName.Trim();
        if (name.IndexOfAny(['/', '\\']) >= 0)
        {
            Fail("The file name can't contain slashes. Open the folder first, then type just the name.");
            return;
        }
        if (_defaultExtension is not null && IsSave && !name.Contains('.'))
            name += "." + _defaultExtension;
        Result = ServerPath.Combine(CurrentPath, name, _windows);
        Chosen?.Invoke();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
