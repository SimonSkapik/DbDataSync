using System.Text;
using DataSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataSync.Services;

public class SchemaWriter
{
    private readonly SyncOptions _options;
    private readonly ILogger<SchemaWriter> _logger;

    private static readonly HashSet<string> TypesWithLength = new(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "binary", "varbinary"
    };

    private static readonly HashSet<string> TypesWithPrecisionScale = new(StringComparer.OrdinalIgnoreCase)
    {
        "decimal", "numeric"
    };

    private static readonly HashSet<string> TypesWithDatetimePrecision = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime2", "datetimeoffset", "time"
    };

    public SchemaWriter(SyncOptions options, ILogger<SchemaWriter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task CreateAllAsync(IEnumerable<TableSchema> schemas, CancellationToken ct = default)
    {
        var connStr = BuildConnectionString(_options.TargetConnectionString, _options.TargetDatabase);
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        var list = schemas.ToList();
        foreach (var schema in list)
        {
            await EnsureSchemaExistsAsync(conn, schema.SchemaName, ct);
            var ddl = BuildCreateTable(schema);
            await using var cmd = new SqlCommand(ddl, conn) { CommandTimeout = _options.CommandTimeout };
            await cmd.ExecuteNonQueryAsync(ct);
        }
        _logger.LogInformation("Created {Count} target tables", list.Count);
    }

    private async Task EnsureSchemaExistsAsync(SqlConnection conn, string schemaName, CancellationToken ct)
    {
        if (string.Equals(schemaName, "dbo", StringComparison.OrdinalIgnoreCase)) return;
        var sql = $@"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @name)
    EXEC('CREATE SCHEMA [{schemaName.Replace("]", "]]")}]');";
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeout };
        cmd.Parameters.AddWithValue("@name", schemaName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public string BuildCreateTable(TableSchema schema)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(schema.QualifiedName).AppendLine(" (");
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            var c = schema.Columns[i];
            sb.Append("    [").Append(c.ColumnName.Replace("]", "]]")).Append("] ");
            sb.Append(FormatType(c));
            var isNull = _options.StrictNullability ? c.IsNullable : true;
            sb.Append(isNull ? " NULL" : " NOT NULL");
            if (i < schema.Columns.Count - 1) sb.Append(',');
            sb.AppendLine();
        }
        sb.AppendLine(");");
        return sb.ToString();
    }

    private static string FormatType(ColumnSchema c)
    {
        var dt = c.DataType.ToLowerInvariant();

        if (dt == "hierarchyid") return "nvarchar(4000)";

        if (TypesWithLength.Contains(dt))
        {
            var len = c.CharacterMaximumLength;
            var lenStr = len is null or -1 ? "MAX" : len.Value.ToString();
            return $"{dt}({lenStr})";
        }
        if (TypesWithPrecisionScale.Contains(dt))
        {
            var p = c.NumericPrecision ?? 18;
            var s = c.NumericScale ?? 0;
            return $"{dt}({p},{s})";
        }
        if (TypesWithDatetimePrecision.Contains(dt))
        {
            var p = c.DateTimePrecision ?? 7;
            return $"{dt}({p})";
        }
        return dt;
    }

    private static string BuildConnectionString(string baseConnStr, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnStr) { InitialCatalog = database };
        return builder.ConnectionString;
    }
}
