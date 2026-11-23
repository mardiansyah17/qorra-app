using DbClient.Core;
using DbClient.Core.Models;
using MySqlConnector;

namespace DbClient.Drivers.MySql;

public sealed class MySqlDriver : IDatabaseDriver
{
    public DatabaseKind Kind => DatabaseKind.MySql;
    public string DisplayName => "MySQL / MariaDB";
    public int DefaultPort => 3306;

    private static readonly SqlDialect Dialect = new(
        Schemas: """
            SELECT SCHEMA_NAME FROM information_schema.SCHEMATA
            WHERE SCHEMA_NAME NOT IN ('information_schema', 'performance_schema', 'mysql', 'sys') OR SCHEMA_NAME = DATABASE()
            ORDER BY SCHEMA_NAME
            """,
        Objects: """
            SELECT TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END
            FROM information_schema.TABLES WHERE TABLE_SCHEMA = @schema
            ORDER BY TABLE_NAME
            """,
        Columns: """
            SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE = 'YES', COLUMN_KEY = 'PRI', COLUMN_DEFAULT
            FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """,
        ForeignKeys: """
            SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
                   r.UPDATE_RULE, r.DELETE_RULE
            FROM information_schema.KEY_COLUMN_USAGE k
            LEFT JOIN information_schema.REFERENTIAL_CONSTRAINTS r
              ON r.CONSTRAINT_SCHEMA = k.TABLE_SCHEMA AND r.TABLE_NAME = k.TABLE_NAME AND r.CONSTRAINT_NAME = k.CONSTRAINT_NAME
            WHERE k.TABLE_SCHEMA = @schema AND k.TABLE_NAME = @table AND k.REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION
            """,
        Indexes: """
            SELECT INDEX_NAME, COLUMN_NAME, NON_UNIQUE = 0, INDEX_NAME = 'PRIMARY', NON_UNIQUE = 0, COLLATION = 'D', INDEX_TYPE, NULL
            FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """,
        Checks: """
            SELECT tc.CONSTRAINT_NAME, cc.CHECK_CLAUSE
            FROM information_schema.TABLE_CONSTRAINTS tc
            JOIN information_schema.CHECK_CONSTRAINTS cc
              ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
            WHERE tc.TABLE_SCHEMA = @schema AND tc.TABLE_NAME = @table AND tc.CONSTRAINT_TYPE = 'CHECK'
            ORDER BY tc.CONSTRAINT_NAME
            """,
        Quote: name => "`" + name.Replace("`", "``") + "`",
        Page: (from, order, limit, offset) => $"SELECT * FROM {from}{(order is null ? "" : " ORDER BY " + order)} LIMIT {limit} OFFSET {offset}",
        Param: (p, _) => p)
    {
        Check = CheckAsync,
        Begin = "START TRANSACTION",
        IsolationPrefix = "SET SESSION TRANSACTION ISOLATION LEVEL ",
        EstimatedRows = "SELECT TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
        ColumnDesign = ColumnDesign("""
            CASE WHEN COLUMN_DEFAULT IS NULL THEN NULL
                 WHEN COLUMN_DEFAULT REGEXP '^(CURRENT_TIMESTAMP|NOW|LOCALTIME)' THEN COLUMN_DEFAULT
                 WHEN EXTRA LIKE '%DEFAULT_GENERATED%' THEN CONCAT('(', COLUMN_DEFAULT, ')')
                 WHEN DATA_TYPE IN ('tinyint', 'smallint', 'mediumint', 'int', 'bigint', 'decimal', 'float', 'double', 'bit', 'year') THEN COLUMN_DEFAULT
                 ELSE QUOTE(COLUMN_DEFAULT) END
            """),
        TableComment = "SELECT TABLE_COMMENT FROM information_schema.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
        TableOptions = "SELECT ENGINE, TABLE_COLLATION FROM information_schema.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
        Triggers = """
            SELECT TRIGGER_NAME,
                   CONCAT('CREATE TRIGGER `', REPLACE(TRIGGER_NAME, '`', '``'), '` ', ACTION_TIMING, ' ', EVENT_MANIPULATION,
                          ' ON `', REPLACE(EVENT_OBJECT_TABLE, '`', '``'), '` FOR EACH ROW ', ACTION_STATEMENT)
            FROM information_schema.TRIGGERS
            WHERE EVENT_OBJECT_SCHEMA = @schema AND EVENT_OBJECT_TABLE = @table
            ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER
            """,
        Routines = $"""
            SELECT CONCAT(r.ROUTINE_TYPE, ' ', r.ROUTINE_NAME), r.ROUTINE_NAME, LOWER(r.ROUTINE_TYPE), {RoutineParameters},
                   CASE WHEN r.ROUTINE_TYPE = 'FUNCTION' THEN r.DTD_IDENTIFIER END
            FROM information_schema.ROUTINES r
            WHERE r.ROUTINE_SCHEMA = @schema
            ORDER BY r.ROUTINE_NAME, r.ROUTINE_TYPE
            """,
        RoutineDefinition = $"""
            SELECT CONCAT('CREATE ', r.ROUTINE_TYPE, ' `', REPLACE(r.ROUTINE_NAME, '`', '``'), '`(', {RoutineParameters}, ')',
                          CASE WHEN r.ROUTINE_TYPE = 'FUNCTION' THEN CONCAT(' RETURNS ', r.DTD_IDENTIFIER) ELSE '' END, '\n', r.ROUTINE_DEFINITION)
            FROM information_schema.ROUTINES r
            WHERE r.ROUTINE_SCHEMA = @schema AND CONCAT(r.ROUTINE_TYPE, ' ', r.ROUTINE_NAME) = @id
            """,
        Enums = """
            SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME), SUBSTRING(COLUMN_TYPE, 6, CHAR_LENGTH(COLUMN_TYPE) - 6)
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @schema AND DATA_TYPE = 'enum'
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """,
        Read = Read,
        Display = Display,
        SaveSession = (
            "SET @dbclient_sql_mode = @@SESSION.sql_mode, SESSION sql_mode = CONCAT_WS(',', NULLIF(@@SESSION.sql_mode, ''), 'STRICT_ALL_TABLES')",
            "SET SESSION sql_mode = @dbclient_sql_mode"),
        Warnings = "SHOW WARNINGS",
    };

    private static object Read(System.Data.Common.DbDataReader reader, int column)
    {
        if (reader.GetDataTypeName(column) == "GEOMETRY" && reader.GetValue(column) is byte[] geometry)
        {
            try { return Wkt(geometry); }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException) { return geometry; }
        }
        if (reader.GetFieldType(column) != typeof(decimal)) return reader.GetValue(column);
        var text = ((MySqlDataReader)reader).GetMySqlDecimal(column).ToString();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            && value.ToString(System.Globalization.CultureInfo.InvariantCulture) == text ? value : text;
    }

    internal static object Display(object value) => value switch
    {
        MySqlDateTime { IsValidDateTime: true } d => d.GetDateTime(),
        MySqlDateTime d => FormattableString.Invariant($"{d.Year:0000}-{d.Month:00}-{d.Day:00}")
            + (d.Hour + d.Minute + d.Second + d.Microsecond == 0 ? "" : " " + Time(new TimeSpan(d.Hour, d.Minute, d.Second).Add(TimeSpan.FromMicroseconds(d.Microsecond)))),
        TimeSpan t => Time(t),
        _ => value,
    };

    internal static string Wkt(byte[] g)
    {
        var at = 4;
        var srid = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(g);
        var text = Geometry();
        return srid == 0 ? text : $"SRID={srid};{text}";

        string Geometry()
        {
            var (type, little) = Header();
            return type switch
            {
                1 => "POINT", 2 => "LINESTRING", 3 => "POLYGON", 4 => "MULTIPOINT", 5 => "MULTILINESTRING", 6 => "MULTIPOLYGON",
                7 => "GEOMETRYCOLLECTION", _ => throw new FormatException($"Unknown geometry type {type}."),
            } + Body(type, little);
        }
        (uint Type, bool Little) Header()
        {
            var little = g[at++] == 1;
            return (Int(little), little);
        }
        string Body(uint type, bool little)
        {
            switch (type)
            {
                case 1: return "(" + Point(little) + ")";
                case 2: return "(" + Many(Int(little), () => Point(little)) + ")";
                case 3: return "(" + Many(Int(little), () => "(" + Many(Int(little), () => Point(little)) + ")") + ")";
                case 4 or 5 or 6:
                    return "(" + Many(Int(little), () =>
                    {
                        var (t, l) = Header();
                        return Body(t, l);
                    }) + ")";
                default:
                    var n = Int(little);
                    return n == 0 ? " EMPTY" : "(" + Many(n, Geometry) + ")";
            }
        }
        static string Many(uint count, Func<string> item) => string.Join(",", Enumerable.Range(0, (int)count).Select(_ => item()));
        string Point(bool little) => Number(little) + " " + Number(little);
        string Number(bool little)
        {
            var s = g.AsSpan(at, 8);
            at += 8;
            var d = little ? System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(s);
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        uint Int(bool little)
        {
            var s = g.AsSpan(at, 4);
            at += 4;
            return little ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(s) : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(s);
        }
    }

    private static string Time(TimeSpan t)
    {
        var a = t.Duration();
        var text = FormattableString.Invariant($"{(t < TimeSpan.Zero ? "-" : "")}{(long)a.TotalHours:00}:{a.Minutes:00}:{a.Seconds:00}");
        var micro = a.Ticks % TimeSpan.TicksPerSecond / 10;
        return micro == 0 ? text : text + "." + micro.ToString("000000", System.Globalization.CultureInfo.InvariantCulture).TrimEnd('0');
    }

    private static string ColumnDesign(string defaultExpression) => $"""
        SELECT COLUMN_NAME,
               IF(EXTRA LIKE '%on update%', SUBSTRING(EXTRA, LOCATE('on update', EXTRA)), NULL),
               IF(GENERATION_EXPRESSION IS NULL OR GENERATION_EXPRESSION = '', {defaultExpression}, NULL),
               COLUMN_COMMENT,
               EXTRA LIKE '%auto_increment%',
               COLLATION_NAME,
               NULLIF(GENERATION_EXPRESSION, ''),
               EXTRA LIKE '%STORED%' OR EXTRA LIKE '%PERSISTENT%'
        FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
        """;

    private const string RoutineParameters = """
        IFNULL((SELECT GROUP_CONCAT(CONCAT_WS(' ', IF(p.ROUTINE_TYPE = 'PROCEDURE', p.PARAMETER_MODE, NULL), p.PARAMETER_NAME, p.DTD_IDENTIFIER) ORDER BY p.ORDINAL_POSITION SEPARATOR ', ')
                FROM information_schema.PARAMETERS p
                WHERE p.SPECIFIC_SCHEMA = r.ROUTINE_SCHEMA AND p.SPECIFIC_NAME = r.SPECIFIC_NAME AND p.ROUTINE_TYPE = r.ROUTINE_TYPE
                  AND p.ORDINAL_POSITION > 0), '')
        """;

    private static async Task CheckAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken ct)
    {
        var c = (MySqlConnection)connection;
        await using var prepare = new MySqlCommand("PREPARE dbclient_check FROM @sql", c);
        prepare.Parameters.AddWithValue("@sql", sql);
        try
        {
            await prepare.ExecuteNonQueryAsync(ct);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.UnsupportedPreparedStatement)
        {
            return;
        }
        await using var deallocate = new MySqlCommand("DEALLOCATE PREPARE dbclient_check", c);
        await deallocate.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static readonly SqlDialect MariaDbDialect = Dialect with
    {
        ColumnDesign = ColumnDesign("NULLIF(COLUMN_DEFAULT, 'NULL')"),
        Checks = """
            SELECT CONSTRAINT_NAME, CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = @schema AND TABLE_NAME = @table
            ORDER BY CONSTRAINT_NAME
            """,
    };

    public async Task<DatabaseConnection> OpenAsync(ConnectionProfile profile, string? password, CancellationToken ct)
    {
        var conn = new MySqlConnection(BuildConnectionString(profile, password));
        await DatabaseConnection.OpenAsync(conn, ct);
        var version = conn.ServerVersion;
        var mariaDb = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase);
        var session = new DatabaseConnection(conn, (mariaDb ? "MariaDB " : "MySQL ") + version, mariaDb ? MariaDbDialect : Dialect)
        {
            SetupSql = profile.ReadOnly ? "SET SESSION TRANSACTION READ ONLY" : null,
        };
        if (session.SetupSql is { } setup)
        {
            try { await session.ExecuteAsync(setup, ct); }
            catch { await session.DisposeAsync(); throw; }
        }
        return session;
    }

    public static string BuildConnectionString(ConnectionProfile profile, string? password)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = profile.Host,
            Port = profile.Port > 0 ? (uint)profile.Port : 3306,
            UserID = profile.User,
            Password = password ?? "",
            SslMode = profile.SslMode switch
            {
                SslMode.Disable => MySqlSslMode.None,
                SslMode.Require => MySqlSslMode.Required,
                SslMode.VerifyCA => MySqlSslMode.VerifyCA,
                SslMode.VerifyFull => MySqlSslMode.VerifyFull,
                _ => MySqlSslMode.Preferred,
            },
            ConnectionTimeout = (uint)SessionOptions.ConnectTimeout,
            Keepalive = (uint)SessionOptions.KeepAlive,
            AllowZeroDateTime = true,
            AllowUserVariables = true,
            TreatTinyAsBoolean = false,
            Pooling = false,
            AllowPublicKeyRetrieval = profile.SslMode == SslMode.Disable,
            ApplicationName = "Qorra",
        };
        if (!string.IsNullOrWhiteSpace(profile.Database))
            b.Database = profile.Database;
        if (profile.SslCaFile.Length > 0) b.SslCa = profile.SslCaFile;
        if (profile.SslCertFile.Length > 0) b.SslCert = profile.SslCertFile;
        if (profile.SslKeyFile.Length > 0) b.SslKey = profile.SslKeyFile;
        return b.ConnectionString;
    }
}
