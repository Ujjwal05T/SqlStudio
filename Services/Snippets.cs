namespace SqlStudio.Services;

public sealed record Snippet(string Category, string Name, string Sql);

/// <summary>Everyday DBA queries, one click away. Each is a single batch that only reads.</summary>
public static class Snippets
{
    public static readonly IReadOnlyList<Snippet> All =
    [
        new("Databases", "Database sizes", """
            SELECT d.name, d.state_desc, d.recovery_model_desc,
                   CAST(SUM(mf.size) * 8.0 / 1024 AS decimal(18, 1)) AS size_mb
            FROM sys.databases d
            JOIN sys.master_files mf ON mf.database_id = d.database_id
            GROUP BY d.name, d.state_desc, d.recovery_model_desc
            ORDER BY size_mb DESC;
            """),
        new("Databases", "Free space in this database's files", """
            SELECT name, type_desc,
                   CAST(size * 8.0 / 1024 AS decimal(18, 1)) AS size_mb,
                   CAST(FILEPROPERTY(name, 'SpaceUsed') * 8.0 / 1024 AS decimal(18, 1)) AS used_mb,
                   CAST((size - FILEPROPERTY(name, 'SpaceUsed')) * 8.0 / 1024 AS decimal(18, 1)) AS free_mb
            FROM sys.database_files;
            """),
        new("Databases", "Server version and settings", """
            SELECT SERVERPROPERTY('ProductVersion') AS version,
                   SERVERPROPERTY('ProductLevel') AS level,
                   SERVERPROPERTY('Edition') AS edition,
                   SERVERPROPERTY('MachineName') AS machine,
                   SERVERPROPERTY('Collation') AS collation,
                   (SELECT sqlserver_start_time FROM sys.dm_os_sys_info) AS started;
            """),
        new("Tables", "Row counts and size per table", """
            SELECT s.name AS [schema], t.name AS [table],
                   SUM(CASE WHEN p.index_id IN (0, 1) THEN p.row_count ELSE 0 END) AS row_count,
                   CAST(SUM(p.reserved_page_count) * 8.0 / 1024 AS decimal(18, 1)) AS reserved_mb
            FROM sys.dm_db_partition_stats p
            JOIN sys.tables t ON t.object_id = p.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            GROUP BY s.name, t.name
            ORDER BY reserved_mb DESC;
            """),
        new("Tables", "Index fragmentation (over 30%)", """
            SELECT OBJECT_SCHEMA_NAME(ips.object_id) AS [schema], OBJECT_NAME(ips.object_id) AS [table],
                   i.name AS [index], CAST(ips.avg_fragmentation_in_percent AS decimal(5, 1)) AS fragmentation_pct,
                   ips.page_count
            FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') ips
            JOIN sys.indexes i ON i.object_id = ips.object_id AND i.index_id = ips.index_id
            WHERE ips.avg_fragmentation_in_percent > 30 AND ips.page_count > 1000
            ORDER BY ips.avg_fragmentation_in_percent DESC;
            """),
        new("Tables", "Missing index suggestions", """
            SELECT TOP (20)
                   CAST(gs.avg_total_user_cost * gs.avg_user_impact * (gs.user_seeks + gs.user_scans) AS bigint) AS impact,
                   mid.statement AS [table], mid.equality_columns, mid.inequality_columns, mid.included_columns
            FROM sys.dm_db_missing_index_group_stats gs
            JOIN sys.dm_db_missing_index_groups mig ON mig.index_group_handle = gs.group_handle
            JOIN sys.dm_db_missing_index_details mid ON mid.index_handle = mig.index_handle
            ORDER BY impact DESC;
            """),
        new("Activity", "Who is connected and running what", """
            SELECT s.session_id, s.login_name, s.host_name, s.program_name, DB_NAME(r.database_id) AS [database],
                   r.status, r.command, r.wait_type, r.total_elapsed_time / 1000 AS elapsed_s,
                   SUBSTRING(t.text, 1, 300) AS sql_text
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON r.session_id = s.session_id
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE s.is_user_process = 1
            ORDER BY r.total_elapsed_time DESC;
            """),
        new("Activity", "Blocking sessions", """
            SELECT r.session_id AS blocked_session, r.blocking_session_id AS blocked_by, r.wait_type,
                   r.wait_time / 1000 AS wait_s, DB_NAME(r.database_id) AS [database],
                   SUBSTRING(t.text, 1, 300) AS blocked_sql
            FROM sys.dm_exec_requests r
            OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.blocking_session_id <> 0;
            """),
        new("Activity", "Top 20 queries by CPU", """
            SELECT TOP (20)
                   qs.total_worker_time / 1000 AS total_cpu_ms,
                   qs.execution_count,
                   qs.total_worker_time / qs.execution_count / 1000 AS avg_cpu_ms,
                   qs.total_logical_reads / qs.execution_count AS avg_reads,
                   SUBSTRING(t.text, 1, 300) AS sql_text
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t
            ORDER BY qs.total_worker_time DESC;
            """),
        new("Activity", "Top waits since startup", """
            SELECT TOP (15) wait_type, waiting_tasks_count,
                   wait_time_ms / 1000 AS wait_s,
                   CAST(100.0 * wait_time_ms / SUM(wait_time_ms) OVER () AS decimal(5, 1)) AS pct
            FROM sys.dm_os_wait_stats
            WHERE wait_type NOT LIKE '%SLEEP%' AND wait_type NOT IN ('BROKER_TASK_STOP', 'CLR_AUTO_EVENT', 'LAZYWRITER_SLEEP',
                  'XE_TIMER_EVENT', 'XE_DISPATCHER_WAIT', 'REQUEST_FOR_DEADLOCK_SEARCH', 'DIRTY_PAGE_POLL', 'HADR_FILESTREAM_IOMGR_IOCOMPLETION')
            ORDER BY wait_time_ms DESC;
            """),
        new("Backups", "Recent backups", """
            SELECT TOP (30) bs.database_name,
                   CASE bs.type WHEN 'D' THEN 'Full' WHEN 'I' THEN 'Differential' WHEN 'L' THEN 'Log' ELSE bs.type END AS backup_type,
                   bs.backup_finish_date, CAST(bs.backup_size / 1024.0 / 1024 AS decimal(18, 1)) AS size_mb,
                   bmf.physical_device_name
            FROM msdb.dbo.backupset bs
            JOIN msdb.dbo.backupmediafamily bmf ON bmf.media_set_id = bs.media_set_id
            ORDER BY bs.backup_finish_date DESC;
            """),
        new("Backups", "Last backup per database", """
            SELECT d.name,
                   MAX(CASE WHEN bs.type = 'D' THEN bs.backup_finish_date END) AS last_full,
                   MAX(CASE WHEN bs.type = 'I' THEN bs.backup_finish_date END) AS last_diff,
                   MAX(CASE WHEN bs.type = 'L' THEN bs.backup_finish_date END) AS last_log
            FROM sys.databases d
            LEFT JOIN msdb.dbo.backupset bs ON bs.database_name = d.name
            WHERE d.name <> 'tempdb'
            GROUP BY d.name
            ORDER BY d.name;
            """),
    ];

    public static IEnumerable<IGrouping<string, Snippet>> ByCategory => All.GroupBy(s => s.Category);
}
