using System.Data;
using DataSync.Models;
using Microsoft.Data.SqlClient;

namespace DataSync.Services;

public class DataReader
{
    private readonly SyncOptions _options;

    public DataReader(SyncOptions options)
    {
        _options = options;
    }

    public async IAsyncEnumerable<DataTable> ReadBatchesAsync(
        TableSchema schema,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var connStr = BuildConnectionString(_options.SourceConnectionString, _options.SourceDatabase);
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        var hint = schema.SourceType == SourceType.Table ? " WITH (NOLOCK)" : "";
        var projection = BuildProjection(schema);
        var sql = $"SELECT {projection} FROM {schema.QualifiedName}{hint}";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeout };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var table = BuildEmptyDataTable(reader);
        var rowsInBatch = 0;

        while (await reader.ReadAsync(ct))
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            table.Rows.Add(values);
            rowsInBatch++;

            if (rowsInBatch >= _options.BatchSize)
            {
                yield return table;
                table.Clear();
                rowsInBatch = 0;
            }
        }

        if (rowsInBatch > 0)
            yield return table;
    }

    private static string BuildProjection(TableSchema schema)
    {
        if (schema.Columns.Count == 0) return "*";
        var parts = new List<string>(schema.Columns.Count);
        foreach (var c in schema.Columns)
        {
            var col = $"[{c.ColumnName.Replace("]", "]]")}]";
            if (string.Equals(c.DataType, "hierarchyid", StringComparison.OrdinalIgnoreCase))
                parts.Add($"CAST({col}.ToString() AS NVARCHAR(4000)) AS {col}");
            else
                parts.Add(col);
        }
        return string.Join(", ", parts);
    }

    private static DataTable BuildEmptyDataTable(SqlDataReader reader)
    {
        var table = new DataTable();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            table.Columns.Add(reader.GetName(i), reader.GetFieldType(i) ?? typeof(object));
        }
        return table;
    }

    private static string BuildConnectionString(string baseConnStr, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnStr) { InitialCatalog = database };
        return builder.ConnectionString;
    }
}
