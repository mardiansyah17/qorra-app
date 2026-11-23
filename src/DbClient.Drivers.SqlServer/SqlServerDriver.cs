using DbClient.Core;
using DbClient.Core.Models;
using Microsoft.Data.SqlClient;

namespace DbClient.Drivers.SqlServer;

public sealed class SqlServerDriver : IDatabaseDriver
{
    public DatabaseKind Kind => DatabaseKind.SqlServer;
    public string DisplayName => "SQL Server";
    public int DefaultPort => 1433;

    private static readonly SqlDialect Dialect = new(
        Schemas: """
            SELECT name FROM sys.schemas
            WHERE schema_id < 16384 AND name NOT IN ('sys', 'INFORMATION_SCHEMA', 'guest')
            ORDER BY name
            """,
        Objects: """
            SELECT o.name, CASE o.type WHEN 'V' THEN 'view' ELSE 'table' END
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE s.name = @schema AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
            ORDER BY o.name
            """,
        Columns: """
            SELECT c.name,
                   TYPE_NAME(c.user_type_id) + CASE
                       WHEN c.user_type_id <> c.system_type_id THEN ''
                       WHEN TYPE_NAME(c.system_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
                           THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length AS varchar(10))) + ')'
                       WHEN TYPE_NAME(c.system_type_id) IN ('nvarchar', 'nchar')
                           THEN '(' + IIF(c.max_length = -1, 'max', CAST(c.max_length / 2 AS varchar(10))) + ')'
                       WHEN TYPE_NAME(c.system_type_id) IN ('decimal', 'numeric')
                           THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
                       ELSE '' END,
                   c.is_nullable,
                   CAST(CASE WHEN EXISTS (
                       SELECT 1 FROM sys.index_columns ic
                       JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                       WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id)
                   THEN 1 ELSE 0 END AS bit),
                   OBJECT_DEFINITION(c.default_object_id)
            FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY c.column_id
            """,
        ForeignKeys: """
            SELECT OBJECT_NAME(f.constraint_object_id), pc.name, rs.name, rt.name, rc.name,
                   REPLACE(fk.update_referential_action_desc, '_', ' '), REPLACE(fk.delete_referential_action_desc, '_', ' ')
            FROM sys.foreign_key_columns f
            JOIN sys.foreign_keys fk ON fk.object_id = f.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = f.parent_object_id AND pc.column_id = f.parent_column_id
            JOIN sys.objects rt ON rt.object_id = f.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = f.referenced_object_id AND rc.column_id = f.referenced_column_id
            WHERE f.parent_object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY f.constraint_object_id, f.constraint_column_id
            """,
        Indexes: """
            SELECT i.name, c.name, i.is_unique, i.is_primary_key, CAST(i.is_primary_key | i.is_unique_constraint AS bit),
                   ic.is_descending_key, i.type_desc, i.filter_definition
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) AND i.name IS NOT NULL AND ic.is_included_column = 0
            ORDER BY i.name, ic.key_ordinal
            """,
        Checks: """
            SELECT name, definition FROM sys.check_constraints
            WHERE parent_object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY name
            """,
        Quote: name => "[" + name.Replace("]", "]]") + "]",
        Page: (from, order, limit, offset) => $"SELECT * FROM {from} ORDER BY {order ?? "(SELECT NULL)"} OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY",
        Param: (p, _) => p)
    {
        TableComment = """
            SELECT CAST(value AS nvarchar(max)) FROM sys.extended_properties
            WHERE class = 1 AND major_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table)) AND minor_id = 0 AND name = 'MS_Description'
            """,
        ColumnDesign = """
            SELECT c.name, NULL, OBJECT_DEFINITION(c.default_object_id), CAST(ep.value AS nvarchar(max)), c.is_identity, c.collation_name,
                   cc.definition, ISNULL(cc.is_persisted, 0)
            FROM sys.columns c
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
            WHERE c.object_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            """,
        Triggers = """
            SELECT t.name, OBJECT_DEFINITION(t.object_id) FROM sys.triggers t
            WHERE t.parent_id = OBJECT_ID(QUOTENAME(@schema) + '.' + QUOTENAME(@table))
            ORDER BY t.name
            """,
        Databases = """
            SELECT name FROM sys.databases
            WHERE state = 0 AND HAS_DBACCESS(name) = 1 AND (database_id > 4 OR name = DB_NAME())
            ORDER BY name
            """,
        Explain = null,
        Begin = "BEGIN TRANSACTION",
        IsolationPrefix = "SET TRANSACTION ISOLATION LEVEL ",
        EstimatedRows = """
            SELECT SUM(p.rows) FROM sys.partitions p
            JOIN sys.tables t ON t.object_id = p.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table AND p.index_id IN (0, 1)
            """,
        Check = CheckAsync,
        Routines = """
            SELECT CAST(o.object_id AS nvarchar(20)), o.name, CASE WHEN o.type = 'P' THEN 'procedure' ELSE 'function' END,
                   ISNULL((SELECT STRING_AGG(CAST(p.name + ' ' + TYPE_NAME(p.user_type_id) AS nvarchar(max)), ', ') WITHIN GROUP (ORDER BY p.parameter_id)
                           FROM sys.parameters p WHERE p.object_id = o.object_id AND p.parameter_id > 0), ''),
                   CASE o.type
                       WHEN 'FN' THEN (SELECT TYPE_NAME(p.user_type_id) FROM sys.parameters p WHERE p.object_id = o.object_id AND p.parameter_id = 0)
                       WHEN 'P' THEN NULL
                       ELSE 'TABLE' END
            FROM sys.objects o
            WHERE o.schema_id = SCHEMA_ID(@schema) AND o.type IN ('FN', 'IF', 'TF', 'P') AND o.is_ms_shipped = 0
            ORDER BY o.name
            """,
        RoutineDefinition = "SELECT OBJECT_DEFINITION(CAST(@id AS int)) WHERE @schema IS NOT NULL",
        Sequences = """
            SELECT s.name, TYPE_NAME(s.user_type_id), CAST(s.start_value AS bigint), CAST(s.increment AS bigint), CAST(s.current_value AS bigint)
            FROM sys.sequences s
            WHERE s.schema_id = SCHEMA_ID(@schema)
            ORDER BY s.name
            """,
        Locate = (error, sql) => error is SqlException { Errors.Count: > 0 } e ? SqlProblem.From(sql, e.Errors[0].Message, line: e.LineNumber) : null,
        Display = Display,
    };

    private static object Display(object value) => value is Microsoft.Data.SqlTypes.SqlVector<float> vector
        ? "[" + string.Join(", ", vector.Memory.ToArray().Select(f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]"
        : value;

    private static readonly System.Text.RegularExpressions.Regex ParseTimeOptions =
        new(@"(NOEXEC|PARSEONLY|QUOTED_IDENTIFIER|OFFSETS|FIPS_FLAGGER)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static async Task CheckAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken ct)
    {
        if (ParseTimeOptions.IsMatch(sql)) return;
        var c = (SqlConnection)connection;
        try
        {
            await ExecuteAsync(c, "SET NOEXEC ON", ct);
            await ExecuteAsync(c, sql, ct);
        }
        finally
        {
            await ExecuteAsync(c, "SET NOEXEC OFF", CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(SqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DatabaseConnection> OpenAsync(ConnectionProfile profile, string? password, CancellationToken ct)
    {
        var conn = new SqlConnection(BuildConnectionString(profile, password));
        await DatabaseConnection.OpenAsync(conn, ct);
        return new DatabaseConnection(conn, "SQL Server " + conn.ServerVersion, Dialect);
    }

    public static string BuildConnectionString(ConnectionProfile profile, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = profile.Host.Contains('\\') || profile.Port <= 0 ? profile.Host : $"{profile.Host},{profile.Port}",
            ConnectTimeout = SessionOptions.ConnectTimeout,
            Pooling = false,
            ApplicationName = "Qorra",
        };

        if (string.IsNullOrWhiteSpace(profile.User))
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = profile.User;
            b.Password = password ?? "";
        }

        if (!string.IsNullOrWhiteSpace(profile.Database))
            b.InitialCatalog = profile.Database;

        (b.Encrypt, b.TrustServerCertificate) = profile.SslMode switch
        {
            SslMode.Disable => (SqlConnectionEncryptOption.Optional, true),
            SslMode.VerifyCA or SslMode.VerifyFull => (SqlConnectionEncryptOption.Mandatory, false),
            _ => (SqlConnectionEncryptOption.Mandatory, true),
        };
        return b.ConnectionString;
    }
}
