using DbClient.Core.Models;

namespace DbClient.Core;

public sealed record CatalogObject(string Schema, string Name, DbObjectKind Kind);

public sealed class SchemaCatalog(DatabaseConnection session)
{
    private Task<IReadOnlyList<string>>? _schemas;
    private Task<IReadOnlyList<CatalogObject>>? _objects;
    private Task<IReadOnlyList<(string Schema, RoutineInfo Routine)>>? _routines;
    private readonly Dictionary<(string, string), Task<IReadOnlyList<ColumnInfo>>> _columns = [];
    private readonly Dictionary<(string, string), Task<IReadOnlyList<ForeignKey>>> _foreignKeys = [];

    public DatabaseConnection Session => session;

    public Task<IReadOnlyList<string>> SchemasAsync(CancellationToken ct) =>
        Fresh(ref _schemas, () => session.GetSchemasAsync(CancellationToken.None)).WaitAsync(ct);

    public Task<IReadOnlyList<CatalogObject>> ObjectsAsync(CancellationToken ct) =>
        Fresh(ref _objects, async () =>
        {
            var all = new List<CatalogObject>();
            foreach (var schema in await SchemasAsync(CancellationToken.None))
                all.AddRange((await session.GetObjectsAsync(schema, CancellationToken.None)).Select(o => new CatalogObject(schema, o.Name, o.Kind)));
            return (IReadOnlyList<CatalogObject>)all;
        }).WaitAsync(ct);

    public Task<IReadOnlyList<(string Schema, RoutineInfo Routine)>> RoutinesAsync(CancellationToken ct) =>
        Fresh(ref _routines, async () =>
        {
            var all = new List<(string, RoutineInfo)>();
            if (session.HasRoutines)
                foreach (var schema in await SchemasAsync(CancellationToken.None))
                    all.AddRange((await session.GetRoutinesAsync(schema, CancellationToken.None)).Select(r => (schema, r)));
            return (IReadOnlyList<(string, RoutineInfo)>)all;
        }).WaitAsync(ct);

    public Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(string schema, string table, CancellationToken ct) =>
        Cached(_columns, (schema, table), () => session.GetColumnsAsync(schema, table, CancellationToken.None)).WaitAsync(ct);

    public Task<IReadOnlyList<ForeignKey>> ForeignKeysAsync(string schema, string table, CancellationToken ct) =>
        Cached(_foreignKeys, (schema, table), () => session.GetForeignKeysAsync(schema, table, CancellationToken.None)).WaitAsync(ct);

    public async Task<string?> DefaultSchemaAsync(CancellationToken ct)
    {
        var schemas = await SchemasAsync(ct);
        return schemas.FirstOrDefault(s => s is "public" or "dbo" or "main") ??
               (schemas.Contains(session.Database) ? session.Database : schemas.Count == 1 ? schemas[0] : null);
    }

    public void Clear()
    {
        _schemas = null;
        _objects = null;
        _routines = null;
        _columns.Clear();
        _foreignKeys.Clear();
    }

    private static Task<T> Fresh<T>(ref Task<T>? slot, Func<Task<T>> load)
    {
        if (slot is null || slot.IsFaulted || slot.IsCanceled) slot = load();
        return slot;
    }

    private static Task<T> Cached<T>(Dictionary<(string, string), Task<T>> cache, (string, string) key, Func<Task<T>> load)
    {
        if (!cache.TryGetValue(key, out var task) || task.IsFaulted || task.IsCanceled) cache[key] = task = load();
        return task;
    }
}
