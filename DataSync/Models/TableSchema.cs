namespace DataSync.Models;

public record ColumnSchema(
    string ColumnName,
    string DataType,
    int OrdinalPosition,
    bool IsNullable,
    int? CharacterMaximumLength,
    byte? NumericPrecision,
    int? NumericScale,
    short? DateTimePrecision
);

public record TableSchema(
    string SchemaName,
    string ObjectName,
    SourceType SourceType,
    IReadOnlyList<ColumnSchema> Columns
)
{
    public string QualifiedName => $"[{SchemaName}].[{ObjectName}]";
    public string DisplayName => $"{SchemaName}.{ObjectName}";
}
