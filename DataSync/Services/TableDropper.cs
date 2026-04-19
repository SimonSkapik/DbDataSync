using DataSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataSync.Services;

public class TableDropper
{
    private readonly SyncOptions _options;
    private readonly ILogger<TableDropper> _logger;

    public TableDropper(SyncOptions options, ILogger<TableDropper> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task DropAllAsync(CancellationToken ct = default)
    {
        var connStr = BuildConnectionString(_options.TargetConnectionString, _options.TargetDatabase);
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        var tables = await FetchTablesAsync(conn, ct);
        _logger.LogInformation("Target has {Count} existing tables to drop", tables.Count);
        if (tables.Count == 0) return;

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            for (int i = tables.Count - 1; i >= 0; i--)
            {
                var (schema, name) = tables[i];
                var sql = $"DROP TABLE IF EXISTS [{schema}].[{name}];";
                await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = _options.CommandTimeout };
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            _logger.LogInformation("Dropped {Count} target tables", tables.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<List<(string Schema, string Name)>> FetchTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"
SELECT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_TYPE = 'BASE TABLE'
ORDER BY TABLE_SCHEMA, TABLE_NAME;";

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeout };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<(string, string)>();
        while (await reader.ReadAsync(ct))
            list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    private static string BuildConnectionString(string baseConnStr, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnStr) { InitialCatalog = database };
        return builder.ConnectionString;
    }
}
