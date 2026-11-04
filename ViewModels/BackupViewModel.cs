using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public sealed record BackupKindChoice(BackupKind Kind, string Label);

public sealed partial class BackupViewModel : ServerJobViewModel
{
    readonly Action<string> _openScript;
    string? _backupDir;
    bool _pathEdited;
    bool _settingPath;

    public BackupViewModel(ConnectionProfile profile, string database, ICredentialStore credentials, Action<string> openScript)
        : base(profile, credentials)
    {
        Database = database;
        _openScript = openScript;
        SelectedKind = Kinds[0];
        _ = LoadDefaultsAsync();
    }

    public string Database { get; }
    public string Heading => $"Back up {Database}";
    public string ServerName => Profile.DisplayName;

    public IReadOnlyList<BackupKindChoice> Kinds { get; } =
    [
        new(BackupKind.Full, "Full"),
        new(BackupKind.Differential, "Differential (changes since last full)"),
        new(BackupKind.Log, "Transaction log"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyOnly))]
    public partial BackupKindChoice SelectedKind { get; set; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ScriptCommand))] public partial string Path { get; set; } = "";
    [ObservableProperty] public partial bool Compression { get; set; } = true;
    [ObservableProperty] public partial bool Checksum { get; set; } = true;
    [ObservableProperty] public partial bool CopyOnly { get; set; }
    [ObservableProperty] public partial bool Overwrite { get; set; }
    [ObservableProperty] public partial bool Verify { get; set; } = true;
    [ObservableProperty] public partial string PathHint { get; set; } = "Folder on the server, e.g. /var/opt/mssql/backup/file.bak";

    // ---- download as zip (read back over the SQL connection) ----

    [ObservableProperty] public partial bool DownloadZip { get; set; }
    [ObservableProperty] public partial string LocalFolder { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDownload))] public partial string DownloadedZip { get; set; } = "";

    public bool HasDownload => DownloadedZip.Length > 0;

    /// <summary>The server's file browser, opened where the current path points (or the server's backup folder).</summary>
    public ServerFileBrowserViewModel CreateBrowser() => new(
        Profile, Credentials, FileBrowseMode.Save, string.IsNullOrWhiteSpace(Path) ? _backupDir : Path,
        [FileFilterChoice.Backups, FileFilterChoice.All], SelectedKind.Kind == BackupKind.Log ? "trn" : "bak");

    public bool CanCopyOnly => SelectedKind.Kind != BackupKind.Differential;

    partial void OnPathChanged(string value)
    {
        if (!_settingPath)
            _pathEdited = true;
    }

    partial void OnSelectedKindChanged(BackupKindChoice value)
    {
        if (value.Kind == BackupKind.Differential)
            CopyOnly = false;
        if (!_pathEdited && _backupDir is not null)
            SetDefaultPath();
    }

    void SetDefaultPath()
    {
        var ext = SelectedKind.Kind == BackupKind.Log ? "trn" : "bak";
        var suffix = SelectedKind.Kind switch { BackupKind.Differential => "_diff", BackupKind.Log => "_log", _ => "" };
        _settingPath = true;
        Path = SqlText.JoinPath(_backupDir!, $"{Database}{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
        _settingPath = false;
    }

    async Task LoadDefaultsAsync()
    {
        try
        {
            var paths = await WithConnectionAsync(CatalogService.GetServerPathsAsync);
            _backupDir = paths.BackupDir;
            if (_backupDir is null)
                PathHint = "Could not detect the server's backup folder. Enter the full path on the server.";
            else if (!_pathEdited)
                SetDefaultPath();
        }
        catch (Exception ex)
        {
            PathHint = "Could not read server defaults: " + ex.Message;
        }
    }

    BackupOptions Options() => new(
        Database, SelectedKind.Kind, Path.Trim(), Compression, Checksum, CopyOnly && CanCopyOnly, Overwrite, Verify);

    bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(Path);

    protected override void OnRunningChanged()
    {
        RunCommand.NotifyCanExecuteChanged();
        ScriptCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task RunAsync()
    {
        if (Overwrite && !await ConfirmedAsync("Overwrite the backups in this file?",
                "Every backup already stored in this file will be erased before the new one is written. This can't be undone.",
                "Overwrite backups", "Keep backups"))
            return;

        if (DownloadZip && string.IsNullOrWhiteSpace(LocalFolder))
        {
            // Checked before the backup starts, so it does not cost a long backup.
            LastRunFailed = true;
            StatusText = "Choose the folder on this Mac to save the zip in.";
            return;
        }

        DownloadedZip = "";
        if (await RunScriptAsync(BackupScripts.Backup(Options()), "Backup completed") && DownloadZip)
            await DownloadAsync();
    }

    async Task DownloadAsync()
    {
        var serverPath = Path.Trim();
        var folder = LocalFolder.Trim();
        var database = Database;
        await RunStepAsync(async ct =>
        {
            AddLog($"Reading {serverPath} over the SQL connection into a zip…");
            await using var connection = await ServerSession.OpenAsync(Profile, Credentials, "master", ct);
            var (zip, bytes) = await BackupDownloader.DownloadAsZipAsync(connection, database, serverPath, folder, SetProgress, ct);
            Dispatcher.UIThread.Post(() => DownloadedZip = zip);
            AddLog($"Saved {zip} (backup file {bytes / 1048576.0:N1} MB). The backup file is still on the server.");
            return $"Backup downloaded to {zip}";
        }, "Downloading…");
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    void Script() => _openScript(BackupScripts.Backup(Options()));
}
