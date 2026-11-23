using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using DbClient.Core.Models;

namespace DbClient.Core;

public sealed record SqlDialect(
    string Schemas, string Objects, string Columns, string ForeignKeys, string Indexes, string Checks,
    Func<string, string> Quote,
    Func<string, string?, int, long, string> Page,
    Func<string, string, string> Param)
{
    public bool LowercasesIdentifiers { get; init; }

    public string? Databases { get; init; }

    public Func<string, IReadOnlyList<CheckConstraint>>? ParseChecks { get; init; }

    public string? Explain { get; init; } = "EXPLAIN ";

    public string? Routines { get; init; }

    public string? RoutineDefinition { get; init; }

    public string? Enums { get; init; }

    public string? Sequences { get; init; }

    public string? ColumnDesign { get; init; }

    public string? Triggers { get; init; }

    public string? TableOptions { get; init; }

    public string? TableComment { get; init; }

    public string? TableSql { get; init; }

    public Func<DbConnection, string, CancellationToken, Task>? Check { get; init; }

    public Func<Exception, string, SqlProblem?>? Locate { get; init; }

    public Func<object, object>? Display { get; init; }

    public Func<DbDataReader, int, object>? Read { get; init; }

    public (string Before, string After)? SaveSession { get; init; }

    public string? Warnings { get; init; }

    public string Begin { get; init; } = "BEGIN";

    public string? IsolationPrefix { get; init; }

    public string? EstimatedRows { get; init; }
}

public sealed record ResultChunk(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows)
{
    public int RecordsAffected { get; init; } = -1;
    public int Set { get; init; }
}

public abstract record PendingChange;
public sealed record RowUpdate(IReadOnlyDictionary<string, object> Key, IReadOnlyDictionary<string, object?> Values) : PendingChange;
public sealed record RowInsert(IReadOnlyDictionary<string, object?> Values) : PendingChange;
public sealed record RowDelete(IReadOnlyDictionary<string, object> Key) : PendingChange;

public sealed class DatabaseConnection : IAsyncDisposable
{
    public const int ChunkSize = 500;

    private readonly DbConnection _connection;
    private readonly SqlDialect _dialect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();

    public DatabaseConnection(DbConnection connection, string serverVersion, SqlDialect dialect)
    {
        _connection = connection;
        _dialect = dialect;
        ServerVersion = serverVersion;
    }

    public string ServerVersion { get; }

    public string? SetupSql { get; set; }

    public int Reopens { get; private set; }

    private async Task EnterAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        if (_connection.State == ConnectionState.Open) return;
        if (!SessionOptions.Reconnect)
        {
            _gate.Release();
            throw new InvalidOperationException("The connection was closed. Reconnect to continue.");
        }
        Reopens++;
        try
        {
            await Task.Run(async () =>
            {
                await _connection.CloseAsync();
                await _connection.OpenAsync(token);
                if (SetupSql is not null)
                {
                    await using var setup = CreateCommand(SetupSql, []);
                    await setup.ExecuteNonQueryAsync(token);
                }
            }, token);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public bool IsReadOnly { get; internal set; }

    public DatabaseKind Kind { get; internal set; }

    internal IDisposable? Tunnel { get; set; }

    public string Database => _connection.Database;

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken ct) => _dialect.Databases is null
        ? []
        : (await QueryAsync(_dialect.Databases, [], ct)).Select(r => (string)r[0]!).ToList();

    public string Identifier(string name)
    {
        var bare = name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_') &&
                   name.All(char.IsAscii) && !(_dialect.LowercasesIdentifiers && name.Any(char.IsUpper));
        return bare ? name : _dialect.Quote(name);
    }

    public static async Task OpenAsync(DbConnection connection, CancellationToken ct)
    {
        var open = connection.OpenAsync(ct);
        try
        {
            await open.WaitAsync(ct);
        }
        catch
        {
            _ = open.ContinueWith(t => { _ = t.Exception; connection.Dispose(); }, TaskScheduler.Default);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetSchemasAsync(CancellationToken ct) =>
        (await QueryAsync(_dialect.Schemas, [], ct)).Select(r => (string)r[0]!).ToList();

    public async Task<IReadOnlyList<DbObject>> GetObjectsAsync(string schema, CancellationToken ct) =>
        (await QueryAsync(_dialect.Objects, [("schema", schema)], ct))
            .Select(r => new DbObject((string)r[0]!, Equals(r[1], "view") ? DbObjectKind.View : DbObjectKind.Table))
            .ToList();

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(string schema, string table, CancellationToken ct) =>
        (await QueryAsync(_dialect.Columns, [("schema", schema), ("table", table)], ct))
            .Select(r => new ColumnInfo((string)r[0]!, r[1] as string ?? "", Convert.ToBoolean(r[2]), Convert.ToBoolean(r[3]), r[4] as string))
            .ToList();

    public async Task<IReadOnlyList<ForeignKey>> GetForeignKeysAsync(string schema, string table, CancellationToken ct) =>
        (await QueryAsync(_dialect.ForeignKeys, [("schema", schema), ("table", table)], ct))
            .GroupBy(r => Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture) ?? "")
            .Select(g => new ForeignKey(g.Key, g.Select(r => (string)r[1]!).ToList(), (string)g.First()[2]!, (string)g.First()[3]!,
                g.Select(r => r[4] as string).ToList())
            {
                OnUpdate = g.First() is { Length: > 6 } r ? r[5] as string : null,
                OnDelete = g.First() is { Length: > 6 } d ? d[6] as string : null,
            })
            .ToList();

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string schema, string table, CancellationToken ct) =>
        (await QueryAsync(_dialect.Indexes, [("schema", schema), ("table", table)], ct))
            .GroupBy(r => (string)r[0]!)
            .Select(g => new IndexInfo(g.Key, g.Select(r => r[1] as string ?? "(expression)").ToList(),
                Convert.ToBoolean(g.First()[2]), Convert.ToBoolean(g.First()[3]), Convert.ToBoolean(g.First()[4]))
            {
                Descending = g.First().Length > 5 ? g.Select(r => r[5] is not null and not DBNull && Convert.ToBoolean(r[5])).ToList() : [],
                Method = g.First().Length > 6 ? g.First()[6] as string : null,
                Where = g.First().Length > 7 && g.First()[7] is string { Length: > 0 } w ? w : null,
            })
            .ToList();

    public bool HasTriggers => _dialect.Triggers is not null;

    public async Task<IReadOnlyList<TriggerInfo>> GetTriggersAsync(string schema, string table, CancellationToken ct) => _dialect.Triggers is null ? [] :
        (await QueryAsync(_dialect.Triggers, [("schema", schema), ("table", table)], ct))
            .Select(r => new TriggerInfo((string)r[0]!, r[1] as string ?? "")).ToList();

    public async Task<(string? Engine, string? Collation)> GetTableOptionsAsync(string schema, string table, CancellationToken ct) =>
        _dialect.TableOptions is not null && await QueryAsync(_dialect.TableOptions, [("schema", schema), ("table", table)], ct) is [var row, ..]
            ? (row[0] as string, row[1] as string) : (null, null);

    public bool HasTableOptions => _dialect.TableOptions is not null;

    public async Task<TableDefinition> GetTableDefinitionAsync(string schema, string table, CancellationToken ct)
    {
        var columns = await GetColumnsAsync(schema, table, ct);
        var design = await GetColumnDesignAsync(schema, table, ct);
        var indexes = await GetIndexesAsync(schema, table, ct);
        var foreignKeys = await GetForeignKeysAsync(schema, table, ct);
        var checks = await GetChecksAsync(schema, table, ct);
        var triggers = await GetTriggersAsync(schema, table, ct);
        var comment = await GetTableCommentAsync(schema, table, ct);
        var sql = await GetTableSqlAsync(schema, table, ct);
        var (engine, collation) = await GetTableOptionsAsync(schema, table, ct);
        var defs = columns.Select(c => design.TryGetValue(c.Name, out var d)
            ? new ColumnDef(c.Name, c.DataType, c.IsNullable, d.Default)
            {
                Extra = d.Extra, Comment = d.Comment, AutoIncrement = d.AutoIncrement, Collation = d.Collation, Generated = d.Generated, Stored = d.Stored,
            }
            : new ColumnDef(c.Name, c.DataType, c.IsNullable, c.Default)).ToList();
        var primaryKey = indexes.FirstOrDefault(i => i.IsPrimaryKey)?.Columns ?? columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
        return new TableDefinition(defs, primaryKey, indexes, foreignKeys, checks, triggers, comment, sql) { Engine = engine, Collation = collation };
    }

    public async Task<IReadOnlyList<CheckConstraint>> GetChecksAsync(string schema, string table, CancellationToken ct)
    {
        var rows = await QueryAsync(_dialect.Checks, [("schema", schema), ("table", table)], ct);
        if (_dialect.ParseChecks is { } parse) return rows is [[string ddl, ..]] ? parse(ddl) : [];
        return rows.Select(r => new CheckConstraint((string)r[0]!, r[1] as string ?? "")).ToList();
    }

    public async Task<IReadOnlyDictionary<string, ColumnDesign>> GetColumnDesignAsync(string schema, string table, CancellationToken ct)
    {
        if (_dialect.ColumnDesign is null) return new Dictionary<string, ColumnDesign>();
        static string? Text(object?[] r, int i) => r.Length > i && r[i] is string { Length: > 0 } s ? s : null;
        static bool Flag(object?[] r, int i) => r.Length > i && r[i] is not null and not DBNull && Convert.ToBoolean(r[i]);
        return (await QueryAsync(_dialect.ColumnDesign, [("schema", schema), ("table", table)], ct))
            .ToDictionary(r => (string)r[0]!, r => new ColumnDesign(Text(r, 1), r[2] as string)
            {
                Comment = Text(r, 3), AutoIncrement = Flag(r, 4), Collation = Text(r, 5), Generated = Text(r, 6), Stored = Flag(r, 7),
            });
    }

    public bool HasTableComments => _dialect.TableComment is not null;

    public async Task<string?> GetTableCommentAsync(string schema, string table, CancellationToken ct) =>
        _dialect.TableComment is null ? null
            : await QueryAsync(_dialect.TableComment, [("schema", schema), ("table", table)], ct) is [[string { Length: > 0 } c, ..], ..] ? c : null;

    public async Task<IReadOnlyList<TableObjectSql>> GetTableSqlAsync(string schema, string table, CancellationToken ct) =>
        _dialect.TableSql is null ? []
            : (await QueryAsync(_dialect.TableSql, [("schema", schema), ("table", table)], ct))
                .Select(r => new TableObjectSql((string)r[0]!, (string)r[1]!, (string)r[2]!)).ToList();

    public bool HasRoutines => _dialect.Routines is not null;
    public bool HasEnums => _dialect.Enums is not null;
    public bool HasSequences => _dialect.Sequences is not null;

    public async Task<IReadOnlyList<RoutineInfo>> GetRoutinesAsync(string schema, CancellationToken ct) => _dialect.Routines is null ? [] :
        (await QueryAsync(_dialect.Routines, [("schema", schema)], ct))
            .Select(r => new RoutineInfo(Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture)!, (string)r[1]!,
                Equals(r[2], "procedure"), r[3] as string ?? "", r[4] as string))
            .ToList();

    public async Task<string?> GetRoutineDefinitionAsync(string schema, string id, CancellationToken ct) => _dialect.RoutineDefinition is null ? null :
        (await QueryAsync(_dialect.RoutineDefinition, [("schema", schema), ("id", id)], ct)) is [[string text, ..], ..] ? text : null;

    public async Task<IReadOnlyList<EnumInfo>> GetEnumsAsync(string schema, CancellationToken ct) => _dialect.Enums is null ? [] :
        (await QueryAsync(_dialect.Enums, [("schema", schema)], ct))
            .Select(r => new EnumInfo((string)r[0]!, SplitStringList(r[1] as string ?? "")))
            .ToList();

    public async Task<IReadOnlyList<SequenceInfo>> GetSequencesAsync(string schema, CancellationToken ct)
    {
        if (_dialect.Sequences is null) return [];
        static long? Long(object? v) => v is null ? null : Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
        return (await QueryAsync(_dialect.Sequences, [("schema", schema)], ct))
            .Select(r => new SequenceInfo((string)r[0]!, r[1] as string ?? "", Long(r[2]), Long(r[3]), Long(r[4])))
            .ToList();
    }

    public static IReadOnlyList<string> SplitStringList(string list)
    {
        var values = new List<string>();
        var i = 0;
        while ((i = list.IndexOf('\'', i)) >= 0)
        {
            var value = new System.Text.StringBuilder();
            for (i++; i < list.Length; i++)
            {
                if (list[i] != '\'') value.Append(list[i]);
                else if (i + 1 < list.Length && list[i + 1] == '\'') value.Append(list[++i]);
                else break;
            }
            values.Add(value.ToString());
            i++;
        }
        return values;
    }

    public string QualifiedName(string schema, string table) => _dialect.Quote(schema) + "." + _dialect.Quote(table);
    public string Quote(string name) => _dialect.Quote(name);

    public string? ExplainSql(string sql) => _dialect.Explain is { } prefix ? prefix + sql : null;

    public IAsyncEnumerable<ResultChunk> ReadTableAsync(string schema, string table, int limit, long offset, CancellationToken ct,
        string? where = null, string? orderBy = null)
    {
        var source = QualifiedName(schema, table);
        if (Clause(where, "WHERE") is { } condition) source += $" WHERE (\n{condition}\n)";
        var order = Clause(orderBy, "ORDER BY") is { } o ? o + "\n" : null;
        return QueryChunksAsync(_dialect.Page(source, order, limit, offset), [], ct);
    }

    public async Task<IReadOnlyList<(object? Key, object? Label)>> LookupAsync(string schema, string table, string key, string? label,
        string? search, string? keyType, int limit, CancellationToken ct)
    {
        var q = _dialect.Quote;
        var columns = q(key) + (label is null ? "" : ", " + q(label));
        var source = $"(SELECT {columns} FROM {QualifiedName(schema, table)}";
        var parameters = new List<(string, object)>();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var conditions = new List<string>();
            if (label is not null)
            {
                parameters.Add(("s", "%" + search.Trim().ToLowerInvariant() + "%"));
                conditions.Add($"LOWER({q(label)}) LIKE @s");
            }
            if (keyType is not null)
            {
                parameters.Add(("k", search.Trim()));
                conditions.Add($"{q(key)} = {_dialect.Param("@k", keyType)}");
            }
            if (conditions.Count == 0) return [];
            source += " WHERE " + string.Join(" OR ", conditions);
        }
        source += ") lookup_rows";
        var result = new List<(object?, object?)>();
        await foreach (var chunk in QueryChunksAsync(_dialect.Page(source, q(label ?? key) + "\n", limit, 0), [.. parameters], ct))
            result.AddRange(chunk.Rows.Select(r => (r[0], r.Length > 1 ? r[1] : null)));
        return result;
    }

    public static string? Clause(string? text, string keyword)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) && (t.Length == keyword.Length || char.IsWhiteSpace(t[keyword.Length])))
            t = t[keyword.Length..].Trim();
        return t.Length == 0 ? null : t;
    }

    public SqlProblem Locate(Exception error, string sql) =>
        _dialect.Locate?.Invoke(error, sql) ?? SqlProblem.From(sql, error.Message);

    public async Task<SqlProblem?> CheckAsync(string sql, CancellationToken ct)
    {
        if (_dialect.Check is not { } check || string.IsNullOrWhiteSpace(sql)) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = linked.Token;
        if (!await _gate.WaitAsync(0, token)) return null;
        try
        {
            if (_connection.State != ConnectionState.Open) return null;
            await Task.Run(() => check(_connection, sql, token), token);
            return null;
        }
        catch (DbException ex) when (!token.IsCancellationRequested)
        {
            return Locate(ex, sql);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SqlProblem?> CheckClauseAsync(string keyword, string schema, string table, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var head = $"SELECT * FROM {QualifiedName(schema, table)} {keyword} ";
        if (await CheckAsync(head + text, ct) is not { } p) return null;
        var start = p.Start - head.Length;
        return start < 0 || start >= text.Length ? p with { Start = 0, Length = text.Length } : p with { Start = start, Length = Math.Min(p.Length, text.Length - start) };
    }

    public string BeginSql => _dialect.Begin;

    public string? IsolationSql(string level) => _dialect.IsolationPrefix is not { } prefix ? null : level switch
    {
        "ReadCommitted" => prefix + "READ COMMITTED",
        "RepeatableRead" => prefix + "REPEATABLE READ",
        "Serializable" => prefix + "SERIALIZABLE",
        _ => null,
    };

    public async Task<long?> EstimateRowsAsync(string schema, string table, CancellationToken ct)
    {
        if (_dialect.EstimatedRows is null) return null;
        var rows = await QueryAsync(_dialect.EstimatedRows, [("schema", schema), ("table", table)], ct);
        return rows is [[{ } v, ..], ..] && v is not DBNull ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public async Task<int> ExecuteAsync(string sql, CancellationToken ct)
    {
        var affected = -1;
        await foreach (var chunk in QueryChunksAsync(sql, [], ct)) affected = chunk.RecordsAffected;
        return affected;
    }

    public async Task<IReadOnlyList<string>> WarningsAsync(CancellationToken ct) => _dialect.Warnings is not { } sql ? [] :
        (await QueryAsync(sql, [], ct)).Select(r => $"{r[0]} {r[1]}: {r[2]}").ToList();

    public async IAsyncEnumerable<ResultChunk> QueryChunksAsync(string sql, (string Name, object Value)[] parameters,
        [EnumeratorCancellation] CancellationToken ct, bool everyResult = false)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = linked.Token;
        await EnterAsync(token);
        DbCommand? cmd = null;
        DbDataReader? reader = null;
        var finished = false;
        try
        {
            var command = cmd = CreateCommand(sql, parameters.Select(p => (p.Name, (object?)p.Value)));

            reader = await Task.Run(() => command.ExecuteReaderAsync(token), token);
            while (reader.FieldCount == 0 && await Task.Run(() => reader.NextResultAsync(token), token)) { }

            if (reader.FieldCount == 0)
            {
                finished = true;
                yield return new ResultChunk([], []) { RecordsAffected = reader.RecordsAffected };
                yield break;
            }

            for (var set = 0; ; set++)
            {
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                while (true)
                {
                    var rows = await Task.Run(() => ReadChunkAsync(reader, _dialect.Read, _dialect.Display, token), token);
                    var last = rows.Count < ChunkSize;
                    finished = last && !everyResult;
                    yield return new ResultChunk(columns, rows) { Set = set };
                    if (last) break;
                }
                if (!everyResult) break;
                var next = false;
                while (!next && await Task.Run(() => reader.NextResultAsync(token), token)) next = reader.FieldCount > 0;
                if (!next)
                {
                    finished = true;
                    break;
                }
            }
        }
        finally
        {
            try
            {
                if (!finished)
                {
                    try { cmd?.Cancel(); } catch (Exception ex) when (ex is DbException or InvalidOperationException) { }
                    try { if (reader is not null) await reader.DisposeAsync(); }
                    catch (Exception ex) when (ex is DbException or InvalidOperationException or OperationCanceledException) { }
                }
                else if (reader is not null)
                {
                    await reader.DisposeAsync();
                }
                if (cmd is not null) await cmd.DisposeAsync();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async Task ApplyChangesAsync(string schema, string table, IReadOnlyDictionary<string, string> columnTypes,
        IReadOnlyList<PendingChange> changes, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = linked.Token;
        var q = _dialect.Quote;
        var target = q(schema) + "." + q(table);
        if (IsReadOnly) throw new InvalidOperationException("This connection is read-only.");
        await EnterAsync(token);
        try
        {
            await Task.Run(async () =>
            {
                if (_dialect.SaveSession is not { } setup)
                {
                    await SaveAsync();
                    return;
                }
                await RunAsync(setup.Before, token);
                try { await SaveAsync(); }
                finally
                {
                    try { await RunAsync(setup.After, CancellationToken.None); }
                    catch (Exception ex) when (ex is DbException or InvalidOperationException) { }
                }
            }, token);
        }
        finally
        {
            _gate.Release();
        }

        async Task RunAsync(string sql, CancellationToken t)
        {
            await using var cmd = CreateCommand(sql, Array.Empty<(string, object?)>());
            await cmd.ExecuteNonQueryAsync(t);
        }

        async Task SaveAsync()
        {
            await using var tx = await _connection.BeginTransactionAsync(token);
            foreach (var change in changes)
            {
                var parameters = new List<(string, object?)>();
                string Value(string column, object? value)
                {
                    parameters.Add(("v" + parameters.Count, value));
                    return _dialect.Param("@v" + (parameters.Count - 1), columnTypes[column]);
                }
                string Where(IReadOnlyDictionary<string, object> key) => string.Join(" AND ", key.Select(k =>
                {
                    parameters.Add(("k" + parameters.Count, k.Value));
                    return $"{q(k.Key)} = @k{parameters.Count - 1}";
                }));

                var (sql, expectOne) = change switch
                {
                    RowUpdate u => ($"UPDATE {target} SET {string.Join(", ", u.Values.Select(v => $"{q(v.Key)} = {Value(v.Key, v.Value)}"))} WHERE {Where(u.Key)}", true),
                    RowDelete d => ($"DELETE FROM {target} WHERE {Where(d.Key)}", true),
                    RowInsert { Values.Count: > 0 } i => ($"INSERT INTO {target} ({string.Join(", ", i.Values.Keys.Select(q))}) VALUES ({string.Join(", ", i.Values.Select(v => Value(v.Key, v.Value)))})", false),
                    _ => throw new ArgumentException($"Unknown change {change}"),
                };

                await using var cmd = CreateCommand(sql, parameters);
                cmd.Transaction = tx;
                var affected = await cmd.ExecuteNonQueryAsync(token);
                if (expectOne && affected != 1)
                    throw new InvalidOperationException(affected == 0
                        ? $"A row in {schema}.{table} was changed or deleted by someone else. Nothing was saved; reload and try again."
                        : $"The primary key matched {affected} rows in {schema}.{table}. Nothing was saved.");
            }
            await tx.CommitAsync(token);
        }
    }

    public async Task ExecuteScriptAsync(IReadOnlyList<string> statements, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        var token = linked.Token;
        if (IsReadOnly) throw new InvalidOperationException("This connection is read-only.");
        await EnterAsync(token);
        try
        {
            await Task.Run(async () =>
            {
                await using var tx = await _connection.BeginTransactionAsync(token);
                for (var i = 0; i < statements.Count; i++)
                {
                    await using var cmd = CreateCommand(statements[i], []);
                    cmd.Transaction = tx;
                    try { await cmd.ExecuteNonQueryAsync(token); }
                    catch (Exception ex) when (ex is DbException)
                    {
                        ex.Data["Statement"] = i;
                        throw;
                    }
                }
                await tx.CommitAsync(token);
            }, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DbCommand CreateCommand(string sql, IEnumerable<(string Name, object? Value)> parameters)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = SessionOptions.CommandTimeout;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@" + name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static async Task<List<object?[]>> ReadChunkAsync(DbDataReader reader, Func<DbDataReader, int, object>? read,
        Func<object, object>? display, CancellationToken ct)
    {
        var raw = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetFieldType(i) is null).ToArray();
        var rows = new List<object?[]>(ChunkSize);
        while (rows.Count < ChunkSize && await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                try
                {
                    if (reader.IsDBNull(i)) continue;
                    var value = raw[i] ? Bytes(reader, i) : read is null ? reader.GetValue(i) : read(reader, i);
                    row[i] = display is null ? value : display(value);
                }
                catch (OverflowException) when (ProviderText(reader, i) is { } text)
                {
                    row[i] = text;
                }
                catch (Exception ex) when (ex is not (DbException or OperationCanceledException))
                {
                    row[i] = $"<{reader.GetDataTypeName(i)}: {ex.Message}>";
                }
            }
            rows.Add(row);
        }
        return rows;
    }

    private static string? ProviderText(DbDataReader reader, int column)
    {
        try { return Convert.ToString(reader.GetProviderSpecificValue(column), System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is not (DbException or OperationCanceledException)) { return null; }
    }

    private static byte[] Bytes(DbDataReader reader, int column)
    {
        var bytes = new byte[reader.GetBytes(column, 0, null, 0, 0)];
        reader.GetBytes(column, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private async Task<List<object?[]>> QueryAsync(string sql, (string Name, object Value)[] parameters, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await foreach (var chunk in QueryChunksAsync(sql, parameters, ct))
            rows.AddRange(chunk.Rows);
        return rows;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed.IsCancellationRequested) return;
        await _disposed.CancelAsync();
        await _gate.WaitAsync();
        try { await _connection.DisposeAsync(); }
        finally
        {
            _gate.Release();
            Tunnel?.Dispose();
        }
    }
}
