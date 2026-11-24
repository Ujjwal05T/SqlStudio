using SqlStudio.Models;
using SqlStudio.Services.Completion;

namespace SqlStudio.Services.Guard;

/// <summary>
/// Limits autocomplete on a company server to the databases the user may use. Other databases are never queried,
/// so their object names can't leak through suggestions.
/// </summary>
public sealed class RestrictedSchemaSource(ISchemaSource inner, ManagedServerAccess access) : ISchemaSource
{
    public async Task<IReadOnlyList<string>> GetDatabasesAsync() =>
        (await inner.GetDatabasesAsync()).Where(access.CanUse).ToList();

    public Task<DbObjects> GetObjectsAsync(string database) =>
        access.CanUse(database) ? inner.GetObjectsAsync(database) : Task.FromResult(DbObjects.Empty);

    public Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string database, string schema, string name) =>
        access.CanUse(database) ? inner.GetColumnsAsync(database, schema, name) : Task.FromResult<IReadOnlyList<ColumnInfo>>([]);
}
