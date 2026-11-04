using System.Data;
using System.IO.Compression;
using Microsoft.Data.SqlClient;

namespace SqlStudio.Services;

/// <summary>
/// Brings a backup file off the server through the SQL connection itself (no SSH, no file share) and stores it,
/// zipped, on this machine. SQL Server reads the file with OPENROWSET(BULK ... SINGLE_BLOB), which caps one file at 2 GB.
/// </summary>
public static class BackupDownloader
{
    /// <summary>The largest value OPENROWSET can return as a single varbinary(max).</summary>
    public const long MaxBytes = int.MaxValue;

    /// <summary>The file name inside the zip, and the zip's own name, come from the backup file name.</summary>
    public static string FileNameOf(string serverPath) => serverPath.Trim().Split('/', '\\').Last();

    /// <summary>A zip path in the folder that does not overwrite an earlier download.</summary>
    public static string UniqueZipPath(string folder, string backupFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(backupFileName);
        var path = Path.Combine(folder, stem + ".zip");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem} ({n}).zip");
        return path;
    }

    /// <summary>Writes one entry into a new zip. The zip appears at its final path only when everything succeeded.</summary>
    public static void WriteZip(string zipPath, string entryName, Action<Stream> fill)
    {
        var partial = zipPath + ".part";
        try
        {
            using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                // A .bak is already compressed or close to it, so favour speed over a few more percent.
                var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
                using var stream = entry.Open();
                fill(stream);
            }
            File.Move(partial, zipPath, overwrite: false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>The size of the newest backup of the database, from msdb. Null when msdb has no record.</summary>
    public static async Task<long?> LastBackupSizeAsync(SqlConnection connection, string database, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT TOP 1 COALESCE(compressed_backup_size, backup_size) FROM msdb.dbo.backupset " +
            "WHERE database_name = @db ORDER BY backup_set_id DESC", connection);
        command.Parameters.AddWithValue("@db", database);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>Reads the backup file through the connection into a zip. Returns the zip path and the file's size in bytes.</summary>
    public static async Task<(string ZipPath, long Bytes)> DownloadAsZipAsync(
        SqlConnection connection, string database, string serverPath, string localFolder,
        Action<double> progress, CancellationToken ct)
    {
        var expected = await LastBackupSizeAsync(connection, database, ct);
        if (expected > MaxBytes)
            throw new InvalidOperationException(
                $"The backup is {expected / 1073741824.0:N1} GB. Reading it through the SQL connection is limited to 2 GB per file. " +
                "Copy it off the server another way, or back up to several files.");

        Directory.CreateDirectory(localFolder);
        var name = FileNameOf(serverPath);
        var zipPath = UniqueZipPath(localFolder, name);

        // The path must be a literal in OPENROWSET, so it is quoted rather than passed as a parameter.
        await using var command = new SqlCommand(
            $"SELECT BulkColumn FROM OPENROWSET(BULK {SqlText.Literal(serverPath.Trim())}, SINGLE_BLOB) AS f", connection)
        { CommandTimeout = 0 };
        await using var reader = await ExecuteAsync(command, ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("The server returned no data for the backup file.");

        await using var source = reader.GetStream(0);
        var buffer = new byte[81920];
        long done = 0;
        WriteZip(zipPath, name, target =>
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                target.Write(buffer, 0, read);
                done += read;
                if (expected is > 0)
                    progress(Math.Min(99, done * 100.0 / expected.Value));
            }
        });
        return (zipPath, done);
    }

    static async Task<SqlDataReader> ExecuteAsync(SqlCommand command, CancellationToken ct)
    {
        try
        {
            return await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        }
        catch (SqlException ex) when (ex.Number == 4834)
        {
            throw new InvalidOperationException(
                "This login needs the ADMINISTER BULK OPERATIONS permission (or the sysadmin role) to read the backup file.", ex);
        }
        catch (SqlException ex) when (ex.Number == 4860)
        {
            throw new InvalidOperationException(
                "SQL Server could not open the backup file to read it back: " + ex.Message, ex);
        }
    }
}
