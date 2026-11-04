using System.Text;

namespace SqlStudio.Services;

public enum BackupKind { Full, Differential, Log }

public sealed record BackupOptions(
    string Database,
    BackupKind Kind,
    string Path,
    bool Compression,
    bool Checksum,
    bool CopyOnly,
    bool Overwrite,
    bool Verify,
    string? Name = null);

public sealed record RestoreFileMove(string LogicalName, string TargetPath);

public sealed record RestoreOptions(
    string TargetDatabase,
    string SourcePath,
    int FilePosition,
    IReadOnlyList<RestoreFileMove> Moves,
    bool Replace,
    bool LeaveRestoring,
    bool CloseConnections,
    bool TargetExists);

/// <summary>Builds the same T-SQL SSMS would, so the dialogs can show it, run it, or hand it to a query tab.</summary>
public static class BackupScripts
{
    public static string Backup(BackupOptions o)
    {
        var command = o.Kind == BackupKind.Log ? "BACKUP LOG" : "BACKUP DATABASE";
        var options = new List<string>
        {
            o.Overwrite ? "INIT" : "NOINIT",
            "NAME = " + SqlText.Literal(string.IsNullOrWhiteSpace(o.Name) ? $"{o.Database}-{o.Kind} Backup" : o.Name),
        };
        if (o.Kind == BackupKind.Differential)
            options.Add("DIFFERENTIAL");
        if (o.CopyOnly && o.Kind != BackupKind.Differential) // COPY_ONLY is not allowed with DIFFERENTIAL
            options.Add("COPY_ONLY");
        if (o.Compression)
            options.Add("COMPRESSION");
        if (o.Checksum)
            options.Add("CHECKSUM");
        options.Add("STATS = 5");

        var sb = new StringBuilder();
        sb.AppendLine($"{command} {SqlText.Quote(o.Database)}")
          .AppendLine($"TO DISK = {SqlText.Literal(o.Path)}")
          .AppendLine($"WITH {string.Join(", ", options)};");

        if (o.Verify)
        {
            // The file may hold several backup sets, so verify the one just written.
            sb.AppendLine("GO")
              .AppendLine("DECLARE @position int = (SELECT TOP 1 position FROM msdb.dbo.backupset")
              .AppendLine($"    WHERE database_name = {SqlText.Literal(o.Database)} ORDER BY backup_set_id DESC);")
              .AppendLine($"RESTORE VERIFYONLY FROM DISK = {SqlText.Literal(o.Path)}")
              .AppendLine($"WITH FILE = @position{(o.Checksum ? ", CHECKSUM" : "")};");
        }

        return sb.ToString().TrimEnd();
    }

    public static string Restore(RestoreOptions o)
    {
        var target = SqlText.Quote(o.TargetDatabase);
        var sb = new StringBuilder();
        var closed = o.CloseConnections && o.TargetExists;

        if (closed)
            sb.AppendLine($"ALTER DATABASE {target} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;").AppendLine("GO");

        var options = new List<string> { $"FILE = {o.FilePosition}" };
        options.AddRange(o.Moves.Select(m => $"MOVE {SqlText.Literal(m.LogicalName)} TO {SqlText.Literal(m.TargetPath)}"));
        if (o.Replace)
            options.Add("REPLACE");
        options.Add(o.LeaveRestoring ? "NORECOVERY" : "RECOVERY");
        options.Add("STATS = 5");

        sb.AppendLine($"RESTORE DATABASE {target}")
          .AppendLine($"FROM DISK = {SqlText.Literal(o.SourcePath)}")
          .AppendLine($"WITH {string.Join(",\n     ", options)};");

        // A database left restoring cannot be switched back to multi-user.
        if (closed && !o.LeaveRestoring)
            sb.AppendLine("GO").AppendLine($"ALTER DATABASE {target} SET MULTI_USER;");

        return sb.ToString().TrimEnd();
    }
}
