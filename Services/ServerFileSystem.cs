using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

public sealed record ServerFileEntry(string Name, bool IsDirectory, bool IsDrive = false)
{
    public string IconKind => IsDrive ? "drive" : IsDirectory ? "folder" : "file";
}

/// <summary>Path rules for the server's own file system, which may be Windows (D:\Backups) or Linux (/var/opt/mssql).</summary>
public static class ServerPath
{
    public static bool LooksWindows(string? path) =>
        path is { Length: >= 2 } && ((path[1] == ':' && char.IsLetter(path[0])) || path.StartsWith(@"\\", StringComparison.Ordinal));

    public static char Separator(bool windows) => windows ? '\\' : '/';

    public static string Combine(string folder, string name, bool windows)
    {
        var sep = Separator(windows);
        return folder.EndsWith(sep) ? folder + name : folder + sep + name;
    }

    public static string FileName(string path) => path.Trim().Split('/', '\\').Last();

    /// <summary>True when the last part looks like a file ("Sales.bak") rather than a folder.</summary>
    public static bool LooksLikeFile(string path)
    {
        var name = FileName(path);
        return name.Contains('.') && !name.StartsWith('.') && !name.EndsWith('.');
    }

    /// <summary>Tidies a typed path: right slashes for the platform, and a drive such as "D:" becomes "D:\".</summary>
    public static string Normalize(string path, bool windows)
    {
        var p = path.Trim();
        if (windows)
        {
            p = p.Replace('/', '\\');
            if (p.Length == 2 && p[1] == ':')
                p += "\\";
            return p.Length > 3 ? p.TrimEnd('\\') : p;
        }
        p = p.Replace('\\', '/');
        if (!p.StartsWith('/'))
            p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    /// <summary>The folder above this one, or null when this is a top level (a drive, a share, or "/"), where the drive list takes over.</summary>
    public static string? Parent(string path, bool windows)
    {
        var sep = Separator(windows);
        var p = path.Trim().TrimEnd(sep);
        if (windows)
        {
            if (p.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // \\server\share is the top of a network path
                if (p[2..].Split('\\').Length <= 2)
                    return null;
                return p[..p.LastIndexOf('\\')];
            }
            if (p.Length <= 2)
                return null; // "D:" is a drive root
            var cut = p.LastIndexOf('\\');
            if (cut < 0)
                return null;
            var parent = p[..cut];
            return parent.Length == 2 ? parent + "\\" : parent;
        }
        if (p.Length == 0)
            return null; // "/" has no parent
        var slash = p.LastIndexOf('/');
        return slash <= 0 ? "/" : p[..slash];
    }
}

/// <summary>
/// Lists the server's folders and files through the SQL connection (xp_dirtree), as the SQL Server service account sees them.
/// That is the right view for backups, because that account is the one that writes and reads them.
/// </summary>
public static class ServerFileSystem
{
    public static readonly string[] BackupExtensions = [".bak", ".trn", ".bkp", ".dif", ".bck"];

    public static bool IsBackupFile(string name) => BackupExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Folders first, then files, each A to Z.</summary>
    public static List<ServerFileEntry> Sort(IEnumerable<ServerFileEntry> entries) =>
        entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static async Task<bool> IsWindowsAsync(SqlConnection connection, CancellationToken ct)
    {
        try
        {
            await using var command = new SqlCommand("SELECT host_platform FROM sys.dm_os_host_info", connection);
            return await command.ExecuteScalarAsync(ct) is not string platform || platform.Equals("Windows", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqlException)
        {
            return true; // before SQL Server 2017 there was no Linux version
        }
    }

    public static async Task<List<ServerFileEntry>> ListDrivesAsync(SqlConnection connection, CancellationToken ct)
    {
        var drives = new List<ServerFileEntry>();
        try
        {
            await using var command = new SqlCommand("EXEC master.dbo.xp_fixeddrives", connection);
            await using var r = await command.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                drives.Add(new ServerFileEntry(Convert.ToString(r.GetValue(0))!.Trim() + @":\", true, IsDrive: true));
        }
        catch (SqlException)
        {
            // not allowed for this login: offer C: so there is somewhere to start; any other drive can be typed
        }
        return drives.Count > 0 ? drives.OrderBy(d => d.Name).ToList() : [new ServerFileEntry(@"C:\", true, IsDrive: true)];
    }

    public static async Task<List<ServerFileEntry>> ListAsync(SqlConnection connection, string path, CancellationToken ct)
    {
        var entries = new List<ServerFileEntry>();
        await using var command = new SqlCommand("EXEC master.dbo.xp_dirtree @path, 1, 1", connection);
        command.Parameters.AddWithValue("@path", path);
        await using var r = await command.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            entries.Add(new ServerFileEntry(r.GetString(0), Convert.ToInt32(r.GetValue(2)) == 0));
        return Sort(entries);
    }

    /// <summary>True or false when the server can tell; null when it can't (for example the check isn't permitted).</summary>
    public static async Task<bool?> DirectoryExistsAsync(SqlConnection connection, string path, CancellationToken ct)
    {
        try
        {
            await using var command = new SqlCommand("EXEC master.dbo.xp_fileexist @path", connection);
            command.Parameters.AddWithValue("@path", path);
            await using var r = await command.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? Convert.ToInt32(r.GetValue(1)) == 1 : null;
        }
        catch (SqlException)
        {
            return null;
        }
    }
}
