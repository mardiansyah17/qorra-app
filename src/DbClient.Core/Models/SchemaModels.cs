namespace DbClient.Core.Models;

public enum DbObjectKind { Table, View }

public sealed record DbObject(string Name, DbObjectKind Kind);

public sealed record ColumnInfo(string Name, string DataType, bool IsNullable, bool IsPrimaryKey, string? Default);

public sealed record ForeignKey(string Name, IReadOnlyList<string> Columns, string RefSchema, string RefTable, IReadOnlyList<string?> RefColumns)
{
    public string? OnUpdate { get; init; }
    public string? OnDelete { get; init; }
}

public sealed record IndexInfo(string Name, IReadOnlyList<string> Columns, bool IsUnique, bool IsPrimaryKey, bool IsConstraint)
{
    public IReadOnlyList<bool> Descending { get; init; } = [];
    public string? Method { get; init; }
    public string? Where { get; init; }
}

public sealed record CheckConstraint(string Name, string Definition);

public sealed record TriggerInfo(string Name, string Definition);

public sealed record ColumnDesign(string? Extra, string? Default)
{
    public string? Comment { get; init; }
    public bool AutoIncrement { get; init; }
    public string? Collation { get; init; }
    public string? Generated { get; init; }
    public bool Stored { get; init; }
}

public sealed record RoutineInfo(string Id, string Name, bool IsProcedure, string Arguments, string? Returns);

public sealed record EnumInfo(string Name, IReadOnlyList<string> Values);

public sealed record SequenceInfo(string Name, string DataType, long? Start, long? Increment, long? LastValue);
