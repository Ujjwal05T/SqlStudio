using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlStudio.Models;
using SqlStudio.Services;

namespace SqlStudio.ViewModels;

public sealed partial class RestoreFileRow : ObservableObject
{
    bool _programmatic;

    public RestoreFileRow(string logicalName, string type, string originalPath)
    {
        LogicalName = logicalName;
        Type = type;
        OriginalPath = originalPath;
        TargetPath = originalPath;
    }

    public string LogicalName { get; }
    public string Type { get; }
    public string OriginalPath { get; }
    public string TypeLabel => Type == "L" ? "Log" : "Data";
    public bool Edited { get; private set; }

    [ObservableProperty]
    public partial string TargetPath { get; set; }

    partial void OnTargetPathChanged(string value)
    {
        if (!_programmatic)
            Edited = true;
    }

    public void SetDefault(string path)
    {
        _programmatic = true;
        TargetPath = path;
        _programmatic = false;
    }
}

public sealed partial class RestoreViewModel : ServerJobViewModel
{
    readonly Action<string> _openScript;
    ServerPaths _paths = new(null, null, null);
    string _sourceDatabase = "";

    public RestoreViewModel(ConnectionProfile profile, string? database, ICredentialStore credentials, Action<string> openScript)
        : base(profile, credentials)
    {
        _openScript = openScript;
        TargetDatabase = database ?? "";
        _ = LoadAsync();
    }

    public string Heading => "Restore a database";
    public string ServerName => Profile.DisplayName;

    public ObservableCollection<BackupHistoryItem> History { get; } = [];
    public ObservableCollection<BackupSetInfo> Sets { get; } = [];
    public ObservableCollection<RestoreFileRow> Files { get; } = [];

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(ScriptCommand))] public partial string TargetDatabase { get; set; }
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ReadCommand), nameof(RunCommand), nameof(ScriptCommand))] public partial string SourcePath { get; set; } = "";
    [ObservableProperty] public partial BackupHistoryItem? SelectedHistory { get; set; }
    [ObservableProperty] public partial BackupSetInfo? SelectedSet { get; set; }
    [ObservableProperty] public partial bool Replace { get; set; }
    [ObservableProperty] public partial bool LeaveRestoring { get; set; }
    [ObservableProperty] public partial bool CloseConnections { get; set; } = true;
    [ObservableProperty] public partial bool TargetExists { get; set; }
    [ObservableProperty] public partial string ReadStatus { get; set; } = "";

    async Task LoadAsync()
    {
        try
        {
            _paths = await WithConnectionAsync(CatalogService.GetServerPathsAsync);
            if (TargetDatabase.Length > 0)
            {
                var history = await WithConnectionAsync(c => CatalogService.GetBackupHistoryAsync(c, TargetDatabase));
                foreach (var item in history)
                    History.Add(item);
                TargetExists = await WithConnectionAsync(c => CatalogService.DatabaseExistsAsync(c, TargetDatabase));
                Replace = TargetExists;
            }
        }
        catch (Exception ex)
        {
            ReadStatus = "Could not read server info: " + ex.Message;
        }
    }

    partial void OnSelectedHistoryChanged(BackupHistoryItem? value)
    {
        if (value is null)
            return;
        SourcePath = value.Path;
        _ = ReadAsync(value.Position);
    }

    partial void OnSelectedSetChanged(BackupSetInfo? value)
    {
        if (value is not null)
            _ = LoadFilesAsync(value.Position);
    }

    partial void OnTargetDatabaseChanged(string value) => ApplyDefaultPaths();

    /// <summary>The server's file browser for finding a backup file, opened at the current path or the server's backup folder.</summary>
    public ServerFileBrowserViewModel CreateBrowser() => new(
        Profile, Credentials, FileBrowseMode.Open, string.IsNullOrWhiteSpace(SourcePath) ? _paths.BackupDir : SourcePath,
        [FileFilterChoice.Backups, FileFilterChoice.All]);

    /// <summary>The file browser for choosing where one of the restored files goes.</summary>
    public ServerFileBrowserViewModel CreateFileBrowser(RestoreFileRow row) => new(
        Profile, Credentials, FileBrowseMode.Save, row.TargetPath, [FileFilterChoice.DatabaseFiles, FileFilterChoice.All]);

    bool CanRead() => !IsRunning && !string.IsNullOrWhiteSpace(SourcePath);

    [RelayCommand(CanExecute = nameof(CanRead))]
    async Task ReadAsync(int? preferredPosition = null)
    {
        ReadStatus = "Reading backup…";
        Sets.Clear();
        Files.Clear();
        try
        {
            var path = SourcePath.Trim();
            var sets = await WithConnectionAsync(c => CatalogService.ReadBackupSetsAsync(c, path));
            foreach (var set in sets)
                Sets.Add(set);
            ReadStatus = sets.Count == 0 ? "No backup sets found in that file." : $"{sets.Count} backup set(s) found.";

            // Default to the preferred set, otherwise the latest one in the file.
            SelectedSet = Sets.FirstOrDefault(s => s.Position == preferredPosition) ?? Sets.LastOrDefault();
            if (TargetDatabase.Length == 0 && SelectedSet is { } chosen)
                TargetDatabase = chosen.Database;
        }
        catch (Exception ex)
        {
            ReadStatus = ex.Message;
        }
    }

    async Task LoadFilesAsync(int position)
    {
        try
        {
            var path = SourcePath.Trim();
            var files = await WithConnectionAsync(c => CatalogService.ReadBackupFilesAsync(c, path, position));
            _sourceDatabase = SelectedSet?.Database ?? "";
            Files.Clear();
            foreach (var f in files)
                Files.Add(new RestoreFileRow(f.LogicalName, f.Type, f.PhysicalName));
            ApplyDefaultPaths();
            if (TargetDatabase.Length > 0)
                TargetExists = await WithConnectionAsync(c => CatalogService.DatabaseExistsAsync(c, TargetDatabase));
        }
        catch (Exception ex)
        {
            ReadStatus = ex.Message;
        }
    }

    /// <summary>
    /// Restoring over the same database keeps the backup's own paths (what SSMS does). A new name gets fresh
    /// file names in the server's default data/log folders so it cannot collide with the original.
    /// </summary>
    void ApplyDefaultPaths()
    {
        var sameName = string.Equals(TargetDatabase, _sourceDatabase, StringComparison.OrdinalIgnoreCase);
        var dataIndex = 0;
        foreach (var row in Files)
        {
            var isLog = row.Type == "L";
            if (!isLog)
                dataIndex++;
            if (row.Edited)
                continue;

            if (sameName || TargetDatabase.Length == 0)
            {
                row.SetDefault(row.OriginalPath);
                continue;
            }

            var folder = (isLog ? _paths.LogDir : _paths.DataDir) ?? CatalogService.FolderOf(row.OriginalPath) ?? "";
            var file = isLog ? $"{TargetDatabase}_log.ldf" : dataIndex == 1 ? $"{TargetDatabase}.mdf" : $"{TargetDatabase}_{dataIndex}.ndf";
            row.SetDefault(folder.Length == 0 ? file : SqlText.JoinPath(folder, file));
        }
    }

    bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(SourcePath) && !string.IsNullOrWhiteSpace(TargetDatabase) && Files.Count > 0;

    protected override void OnRunningChanged()
    {
        RunCommand.NotifyCanExecuteChanged();
        ScriptCommand.NotifyCanExecuteChanged();
        ReadCommand.NotifyCanExecuteChanged();
    }

    async Task<RestoreOptions> OptionsAsync()
    {
        var exists = await WithConnectionAsync(c => CatalogService.DatabaseExistsAsync(c, TargetDatabase.Trim()));
        TargetExists = exists;
        return new RestoreOptions(
            TargetDatabase.Trim(), SourcePath.Trim(), SelectedSet?.Position ?? 1,
            Files.Select(f => new RestoreFileMove(f.LogicalName, f.TargetPath.Trim())).ToList(),
            Replace, LeaveRestoring, CloseConnections, exists);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task RunAsync()
    {
        var options = await OptionsAsync();
        if (options.Replace && options.TargetExists && !await ConfirmedAsync($"Replace “{options.TargetDatabase}”?",
                $"The database “{options.TargetDatabase}” on {ServerName} will be overwritten with the backup. Everything currently in it will be lost. This can't be undone.",
                "Replace database"))
            return;
        var ok = await RunScriptAsync(BackupScripts.Restore(options), "Restore completed");
        if (!ok && options is { CloseConnections: true, TargetExists: true, LeaveRestoring: false })
            await TryRestoreMultiUserAsync(options.TargetDatabase);
    }

    /// <summary>A failed restore can leave the database single-user; put it back so people are not locked out.</summary>
    async Task TryRestoreMultiUserAsync(string database)
    {
        try
        {
            await WithConnectionAsync(async c =>
            {
                await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                    $"IF EXISTS (SELECT 1 FROM sys.databases WHERE name = {SqlText.Literal(database)} AND state = 0 AND user_access = 1) " +
                    $"ALTER DATABASE {SqlText.Quote(database)} SET MULTI_USER;", c);
                await cmd.ExecuteNonQueryAsync();
                return 0;
            });
            Log.Add(new MessageLine($"Database {database} was set back to MULTI_USER after the failure.", false));
        }
        catch (Exception ex)
        {
            Log.Add(new MessageLine($"Could not reset {database} to MULTI_USER: {ex.Message}", true));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task ScriptAsync() => _openScript(BackupScripts.Restore(await OptionsAsync()));
}
