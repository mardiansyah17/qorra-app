using DbClient.Core;
using DbClient.Core.Models;
using Npgsql;
using CoreSslMode = DbClient.Core.Models.SslMode;

namespace DbClient.Drivers.Postgres;

public sealed class PostgresDriver : IDatabaseDriver
{
    public DatabaseKind Kind => DatabaseKind.PostgreSql;
    public string DisplayName => "PostgreSQL";
    public int DefaultPort => 5432;

    private static readonly SqlDialect Dialect = new(
        Schemas: """
            SELECT nspname FROM pg_namespace
            WHERE nspname NOT IN ('pg_catalog', 'information_schema', 'pg_toast')
              AND nspname NOT LIKE 'pg\_temp\_%' AND nspname NOT LIKE 'pg\_toast\_temp\_%'
            ORDER BY nspname
            """,
        Objects: """
            SELECT c.relname, CASE WHEN c.relkind IN ('v', 'm') THEN 'view' ELSE 'table' END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relkind IN ('r', 'p', 'v', 'm', 'f') AND NOT c.relispartition
            ORDER BY c.relname
            """,
        Columns: """
            SELECT a.attname, format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                   COALESCE(a.attnum = ANY (i.indkey), false), pg_get_expr(d.adbin, d.adrelid)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_index i ON i.indrelid = c.oid AND i.indisprimary
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """,
        ForeignKeys: """
            SELECT k.conname, a.attname, rn.nspname, rc.relname, ra.attname,
                   CASE k.confupdtype WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' WHEN 'r' THEN 'RESTRICT' ELSE 'NO ACTION' END,
                   CASE k.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT' WHEN 'r' THEN 'RESTRICT' ELSE 'NO ACTION' END
            FROM pg_constraint k
            JOIN pg_class c ON c.oid = k.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_class rc ON rc.oid = k.confrelid
            JOIN pg_namespace rn ON rn.oid = rc.relnamespace
            CROSS JOIN LATERAL unnest(k.conkey, k.confkey) WITH ORDINALITY AS u(col, refcol, pos)
            JOIN pg_attribute a ON a.attrelid = k.conrelid AND a.attnum = u.col
            JOIN pg_attribute ra ON ra.attrelid = k.confrelid AND ra.attnum = u.refcol
            WHERE k.contype = 'f' AND n.nspname = @schema AND c.relname = @table
            ORDER BY k.conname, u.pos
            """,
        Indexes: """
            SELECT ic.relname, COALESCE(a.attname, pg_get_indexdef(i.indexrelid, u.pos::int, true)), i.indisunique, i.indisprimary,
                   EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.conrelid = c.oid AND k.contype IN ('p', 'u')),
                   (i.indoption[(u.pos - 1)::int] & 1) = 1, am.amname, pg_get_expr(i.indpred, i.indrelid, true)
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_class ic ON ic.oid = i.indexrelid
            JOIN pg_am am ON am.oid = ic.relam
            CROSS JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS u(col, pos)
            LEFT JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = u.col AND u.col > 0
            WHERE n.nspname = @schema AND c.relname = @table AND u.pos <= i.indnkeyatts
            ORDER BY ic.relname, u.pos
            """,
        Checks: """
            SELECT k.conname, pg_get_constraintdef(k.oid, true)
            FROM pg_constraint k
            JOIN pg_class c ON c.oid = k.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE k.contype = 'c' AND n.nspname = @schema AND c.relname = @table
            ORDER BY k.conname
            """,
        Quote: name => "\"" + name.Replace("\"", "\"\"") + "\"",
        Page: (from, order, limit, offset) => $"SELECT * FROM {from}{(order is null ? "" : " ORDER BY " + order)} LIMIT {limit} OFFSET {offset}",
        Param: (p, type) => $"CAST({p} AS {type})")
    {
        TableComment = """
            SELECT obj_description(c.oid, 'pg_class')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """,
        ColumnDesign = """
            SELECT a.attname, NULL,
                   CASE WHEN a.attgenerated = '' THEN pg_get_expr(d.adbin, d.adrelid) END,
                   col_description(c.oid, a.attnum),
                   a.attidentity <> '',
                   CASE WHEN a.attcollation <> 0 AND a.attcollation <> t.typcollation THEN co.collname END,
                   CASE WHEN a.attgenerated <> '' THEN pg_get_expr(d.adbin, d.adrelid) END,
                   a.attgenerated = 's'
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_type t ON t.oid = a.atttypid
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation
            WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
            """,
        Triggers = """
            SELECT t.tgname, pg_get_triggerdef(t.oid, true)
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table AND NOT t.tgisinternal
            ORDER BY t.tgname
            """,
        LowercasesIdentifiers = true,
        Check = CheckAsync,
        IsolationPrefix = "SET SESSION CHARACTERISTICS AS TRANSACTION ISOLATION LEVEL ",
        EstimatedRows = """
            SELECT CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """,
        Locate = (error, sql) => error is PostgresException { Position: > 0 } p ? SqlProblem.From(sql, p.MessageText, p.Position - 1) : null,
        Databases = """
            SELECT datname FROM pg_database
            WHERE datallowconn AND NOT datistemplate AND has_database_privilege(datname, 'CONNECT')
            ORDER BY datname
            """,
        Routines = """
            SELECT p.oid::text, p.proname, CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END,
                   pg_get_function_identity_arguments(p.oid),
                   CASE WHEN p.prokind = 'p' THEN NULL ELSE pg_get_function_result(p.oid) END
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.prokind IN ('f', 'p')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.deptype = 'e')
            ORDER BY p.proname, 4
            """,
        RoutineDefinition = "SELECT pg_get_functiondef(CAST(@id AS oid)) WHERE @schema IS NOT NULL",
        Enums = """
            SELECT t.typname, string_agg('''' || replace(e.enumlabel, '''', '''''') || '''', ',' ORDER BY e.enumsortorder)
            FROM pg_type t
            JOIN pg_enum e ON e.enumtypid = t.oid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname = @schema
            GROUP BY t.typname
            ORDER BY t.typname
            """,
        Sequences = """
            SELECT sequencename::text, format_type(data_type, NULL), start_value, increment_by, last_value
            FROM pg_sequences
            WHERE schemaname = @schema
            ORDER BY sequencename
            """,
    };

    private static async Task CheckAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken ct)
    {
        var c = (NpgsqlConnection)connection;
        bool inTransaction;
        try
        {
            await ExecuteAsync(c, "SAVEPOINT dbclient_check", ct);
            inTransaction = true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.NoActiveSqlTransaction)
        {
            inTransaction = false;
        }
        catch (PostgresException)
        {
            return;
        }
        try
        {
            await using var cmd = new NpgsqlCommand(sql, c);
            await cmd.PrepareAsync(ct);
            await cmd.UnprepareAsync(ct);
        }
        finally
        {
            if (inTransaction) await ExecuteAsync(c, "ROLLBACK TO SAVEPOINT dbclient_check; RELEASE SAVEPOINT dbclient_check", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DatabaseConnection> OpenAsync(ConnectionProfile profile, string? password, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(BuildConnectionString(profile, password));
        await DatabaseConnection.OpenAsync(conn, ct);
        return new DatabaseConnection(conn, "PostgreSQL " + conn.ServerVersion, Dialect);
    }

    public static string BuildConnectionString(ConnectionProfile profile, string? password)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = profile.Host,
            Port = profile.Port > 0 ? profile.Port : 5432,
            Username = profile.User,
            Password = password,
            Database = string.IsNullOrWhiteSpace(profile.Database) ? "postgres" : profile.Database,
            SslMode = profile.SslMode switch
            {
                CoreSslMode.Disable => Npgsql.SslMode.Disable,
                CoreSslMode.Require => Npgsql.SslMode.Require,
                CoreSslMode.VerifyCA => Npgsql.SslMode.VerifyCA,
                CoreSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
                _ => Npgsql.SslMode.Prefer,
            },
            Timeout = SessionOptions.ConnectTimeout,
            KeepAlive = SessionOptions.KeepAlive,
            Pooling = false,
            ApplicationName = "Qorra",
            Options = profile.ReadOnly ? "-c default_transaction_read_only=on" : null,
        };
        if (profile.SslCaFile.Length > 0) b.RootCertificate = profile.SslCaFile;
        if (profile.SslCertFile.Length > 0) b.SslCertificate = profile.SslCertFile;
        if (profile.SslKeyFile.Length > 0) b.SslKey = profile.SslKeyFile;
        return b.ConnectionString;
    }
}
