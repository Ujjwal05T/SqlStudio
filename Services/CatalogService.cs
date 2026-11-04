using Microsoft.Data.SqlClient;
using SqlStudio.Services.Completion;

namespace SqlStudio.Services;

public sealed record DatabaseInfo(string Name, int Id, string State)
{
    public bool IsOnline => State == "ONLINE";
    public bool IsSystem => Id <= 4;
}

public enum ObjectKind { Table, View, Procedure, Function }

public sealed record SchemaObject(string Schema, string Name)
{
    public string FullName => $"{SqlText.Quote(Schema)}.{SqlText.Quote(Name)}";
}

public sealed record ColumnInfo(string Name, string Type, bool Nullable);

public sealed record ServerPaths(string? BackupDir, string? DataDir, string? LogDir);

public sealed record BackupHistoryItem(DateTime Finished, string Type, long SizeBytes, string Path, int Position)
{
    public string TypeLabel => Type switch { "D" => "Full", "I" => "Differential", "L" => "Log", _ => Type };
    public string Display => $"{Finished:yyyy-MM-dd HH:mm}  ·  {TypeLabel}  ·  {SizeBytes / 1024.0 / 1024.0:N1} MB  ·  {Path}";
}

public sealed record BackupSetInfo(int Position, string Database, string TypeLabel, DateTime Started, string? Name)
{
    public string Display => $"#{Position}  ·  {Database}  ·  {TypeLabel}  ·  {Started:yyyy-MM-dd HH:mm}{(string.IsNullOrEmpty(Name) ? "" : "  ·  " + Name)}";
}

public sealed record BackupFileInfo(string LogicalName, string PhysicalName, string Type);

public static class CatalogService
{
    public static async Task<List<DatabaseInfo>> ListDatabasesAsync(SqlConnection c)
    {
        var list = new List<DatabaseInfo>();
        await using var cmd = new SqlCommand("SELECT name, database_id, state_desc FROM sys.databases ORDER BY name", c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new DatabaseInfo(r.GetString(0), r.GetInt32(1), r.GetString(2)));
        return list;
    }

    public static async Task<List<SchemaObject>> ListObjectsAsync(SqlConnection c, string database, ObjectKind kind)
    {
        // Type codes come from this fixed map, never from user input.
        var types = kind switch
        {
            ObjectKind.Table => "'U'",
            ObjectKind.View => "'V'",
            ObjectKind.Procedure => "'P'",
            _ => "'FN','IF','TF'",
        };
        var db = SqlText.Quote(database);
        var sql = $"""
            SELECT s.name, o.name
            FROM {db}.sys.objects o
            JOIN {db}.sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ({types}) AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name
            """;
        var list = new List<SchemaObject>();
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new SchemaObject(r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Every user schema and object in a database in one round trip, for autocomplete.</summary>
    public static async Task<DbObjects> ListSchemaAsync(SqlConnection c, string database)
    {
        var db = SqlText.Quote(database);
        var sql = $"""
            SELECT s.name FROM {db}.sys.schemas s
            WHERE s.schema_id < 16384 AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
            ORDER BY s.name;
            SELECT s.name, o.name, RTRIM(o.type)
            FROM {db}.sys.objects o
            JOIN {db}.sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.is_ms_shipped = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
            ORDER BY s.name, o.name;
            """;
        var schemas = new List<string>();
        var objects = new List<DbObject>();
        await using var cmd = new SqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            schemas.Add(r.GetString(0));
        await r.NextResultAsync();
        while (await r.ReadAsync())
            objects.Add(new DbObject(r.GetString(0), r.GetString(1), r.GetString(2)));
        return new DbObjects(schemas, objects);
    }

    public static async Task<List<ColumnInfo>> ListColumnsAsync(SqlConnection c, string database, SchemaObject obj)
    {
        var db = SqlText.Quote(database);
        var sql = $"""
            SELECT col.name, ty.name, col.max_length, col.precision, col.scale, col.is_nullable
            FROM {db}.sys.columns col
            JOIN {db}.sys.types ty ON ty.user_type_id = col.user_type_id
            WHERE col.object_id = OBJECT_ID(@full)
            ORDER BY col.column_id
            """;
        var list = new List<ColumnInfo>();
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@full", $"{db}.{obj.FullName}");
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var type = SqlText.FormatType(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4));
            list.Add(new ColumnInfo(r.GetString(0), type, r.GetBoolean(5)));
        }
        return list;
    }

    /// <summary>Default backup/data/log folders on the server. Any of them may be unknown on odd setups.</summary>
    public static async Task<ServerPaths> GetServerPathsAsync(SqlConnection c)
    {
        var backup = await ScalarOrNullAsync(c, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))")
            ?? await RegistryBackupDirAsync(c);
        var data = await ScalarOrNullAsync(c, "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000))")
            ?? FolderOf(await ScalarOrNullAsync(c, "SELECT physical_name FROM master.sys.database_files WHERE file_id = 1"));
        var log = await ScalarOrNullAsync(c, "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))")
            ?? FolderOf(await ScalarOrNullAsync(c, "SELECT physical_name FROM master.sys.database_files WHERE file_id = 2"));
        return new ServerPaths(backup, data, log);
    }

    static async Task<string?> RegistryBackupDirAsync(SqlConnection c)
    {
        try
        {
            await using var cmd = new SqlCommand(
                "DECLARE @d nvarchar(4000); EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\\Microsoft\\MSSQLServer\\MSSQLServer', N'BackupDirectory', @d OUTPUT; SELECT @d;", c);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    static async Task<string?> ScalarOrNullAsync(SqlConnection c, string sql)
    {
        try
        {
            await using var cmd = new SqlCommand(sql, c);
            return await cmd.ExecuteScalarAsync() is string s && s.Length > 0 ? s : null;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    public static string? FolderOf(string? filePath)
    {
        if (filePath is null)
            return null;
        var i = filePath.LastIndexOfAny(['\\', '/']);
        return i > 0 ? filePath[..i] : null;
    }

    public static async Task<List<BackupHistoryItem>> GetBackupHistoryAsync(SqlConnection c, string database)
    {
        const string sql = """
            SELECT TOP (30) bs.backup_finish_date, bs.type, bs.backup_size, bmf.physical_device_name, bs.position
            FROM msdb.dbo.backupset bs
            JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
            WHERE bs.database_name = @db AND bmf.device_type = 2
            ORDER BY bs.backup_finish_date DESC
            """;
        var list = new List<BackupHistoryItem>();
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@db", database);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new BackupHistoryItem(r.GetDateTime(0), r.GetString(1), Convert.ToInt64(r.GetValue(2)), r.GetString(3), r.GetInt32(4)));
        return list;
    }

    /// <summary>Reads the backup sets inside a file on the server (RESTORE HEADERONLY).</summary>
    public static async Task<List<BackupSetInfo>> ReadBackupSetsAsync(SqlConnection c, string path)
    {
        var list = new List<BackupSetInfo>();
        await using var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK = {SqlText.Literal(path)}", c) { CommandTimeout = 120 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var type = Convert.ToInt32(r["BackupType"]) switch { 1 => "Full", 2 => "Log", 5 => "Differential", var t => $"Type {t}" };
            list.Add(new BackupSetInfo(
                Convert.ToInt32(r["Position"]), r["DatabaseName"] as string ?? "", type,
                (DateTime)r["BackupStartDate"], r["BackupName"] as string));
        }
        return list;
    }

    /// <summary>Reads the data/log files a backup set contains (RESTORE FILELISTONLY).</summary>
    public static async Task<List<BackupFileInfo>> ReadBackupFilesAsync(SqlConnection c, string path, int position)
    {
        var list = new List<BackupFileInfo>();
        await using var cmd = new SqlCommand(
            $"RESTORE FILELISTONLY FROM DISK = {SqlText.Literal(path)} WITH FILE = {position}", c) { CommandTimeout = 120 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new BackupFileInfo((string)r["LogicalName"], (string)r["PhysicalName"], (string)r["Type"]));
        return list;
    }

    public static async Task<bool> DatabaseExistsAsync(SqlConnection c, string name)
    {
        await using var cmd = new SqlCommand("SELECT CASE WHEN DB_ID(@n) IS NULL THEN 0 ELSE 1 END", c);
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
    }
}
