using System.Data;
using DataSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataSync.Services;

public class BulkWriter : IDisposable, IAsyncDisposable
{
    private readonly SyncOptions _options;
    private readonly ILogger<BulkWriter> _logger;
    private SqlConnection? _conn;

    public BulkWriter(SyncOptions options, ILogger<BulkWriter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (_conn is not null) return;
        var connStr = BuildConnectionString(_options.TargetConnectionString, _options.TargetDatabase);
        _conn = new SqlConnection(connStr);
        await _conn.OpenAsync(ct);
    }

    public async Task WriteAsync(TableSchema schema, DataTable batch, CancellationToken ct = default)
    {
        if (_conn is null) throw new InvalidOperationException("BulkWriter not opened.");
        await WriteInternalAsync(schema, batch, _options.BatchSize, retryOnTimeout: true, ct);
    }

    private async Task WriteInternalAsync(TableSchema schema, DataTable batch, int batchSize, bool retryOnTimeout, CancellationToken ct)
    {
        var opts = SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.UseInternalTransaction;
        using var bulk = new SqlBulkCopy(_conn!, opts, null)
        {
            DestinationTableName = schema.QualifiedName,
            BatchSize = batchSize,
            BulkCopyTimeout = _options.BulkCopyTimeout
        };

        foreach (DataColumn col in batch.Columns)
            bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);

        try
        {
            await bulk.WriteToServerAsync(batch, ct);
        }
        catch (SqlException ex) when (retryOnTimeout && IsTimeout(ex))
        {
            var halved = Math.Max(1, batchSize / 2);
            _logger.LogWarning("Bulk copy timeout on {Table}; retrying with BatchSize={BatchSize}", schema.DisplayName, halved);
            await WriteInternalAsync(schema, batch, halved, retryOnTimeout: false, ct);
        }
    }

    private static bool IsTimeout(SqlException ex) =>
        ex.Number == -2 || ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    private static string BuildConnectionString(string baseConnStr, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnStr) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _conn = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.DisposeAsync();
            _conn = null;
        }
    }
}
