using DataSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataSync.Services;

public class SchemaReader
{
    private readonly SyncOptions _options;
    private readonly ILogger<SchemaReader> _logger;

    public SchemaReader(SyncOptions options, ILogger<SchemaReader> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TableSchema>> ReadAsync(CancellationToken ct = default)
    {
        var connStr = BuildConnectionString(_options.SourceConnectionString, _options.SourceDatabase);
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        var objects = await ReadObjectListAsync(conn, ct);
        var results = new List<TableSchema>(objects.Count);

        foreach (var (schemaName, objectName, sourceType) in objects)
        {
            var columns = await ReadColumnsAsync(conn, schemaName, objectName, ct);
            results.Add(new TableSchema(schemaName, objectName, sourceType, columns));
        }

        _logger.LogInformation("Discovered {Count} source objects ({Tables} tables, {Views} views)",
            results.Count,
            results.Count(r => r.SourceType == SourceType.Table),
            results.Count(r => r.SourceType == SourceType.View));

        return results;
    }

    private async Task<List<(string Schema, string Name, SourceType Type)>> ReadObjectListAsync(
        SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE IN ('BASE TABLE', 'VIEW')
ORDER BY TABLE_SCHEMA, TABLE_NAME;";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeout };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var list = new List<(string, string, SourceType)>();
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            var tableType = reader.GetString(2);
            var type = tableType == "VIEW" ? SourceType.View : SourceType.Table;
            list.Add((schema, name, type));
        }
        return list;
    }

    private async Task<IReadOnlyList<ColumnSchema>> ReadColumnsAsync(
        SqlConnection conn, string schemaName, string objectName, CancellationToken ct)
    {
        const string sql = @"
SELECT COLUMN_NAME, DATA_TYPE, ORDINAL_POSITION, IS_NULLABLE,
       CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @name
ORDER BY ORDINAL_POSITION;";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeout };
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@name", objectName);

        var cols = new List<ColumnSchema>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            cols.Add(new ColumnSchema(
                ColumnName: reader.GetString(0),
                DataType: reader.GetString(1),
                OrdinalPosition: reader.GetInt32(2),
                IsNullable: reader.GetString(3) == "YES",
                CharacterMaximumLength: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                NumericPrecision: reader.IsDBNull(5) ? null : reader.GetByte(5),
                NumericScale: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                DateTimePrecision: reader.IsDBNull(7) ? null : reader.GetInt16(7)
            ));
        }
        return cols;
    }

    private static string BuildConnectionString(string baseConnStr, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnStr) { InitialCatalog = database };
        return builder.ConnectionString;
    }
}
