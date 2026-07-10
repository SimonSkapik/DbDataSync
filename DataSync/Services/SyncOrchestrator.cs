using DataSync.Helpers;
using DataSync.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DataSync.Services;

public class SyncOrchestrator
{
    private readonly SyncOptions _options;
    private readonly SchemaReader _schemaReader;
    private readonly TableDropper _dropper;
    private readonly SchemaWriter _schemaWriter;
    private readonly DataReader _dataReader;
    private readonly BulkWriter _bulkWriter;
    private readonly ProgressReporter _progress;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        SyncOptions options,
        SchemaReader schemaReader,
        TableDropper dropper,
        SchemaWriter schemaWriter,
        DataReader dataReader,
        BulkWriter bulkWriter,
        ProgressReporter progress,
        ILogger<SyncOrchestrator> logger)
    {
        _options = options;
        _schemaReader = schemaReader;
        _dropper = dropper;
        _schemaWriter = schemaWriter;
        _dataReader = dataReader;
        _bulkWriter = bulkWriter;
        _progress = progress;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        ValidateOptions();
        _progress.StartRun();

        var allSchemas = await _schemaReader.ReadAsync(ct);
        var schemas = ApplyFilters(allSchemas);

        if (_options.DryRun)
        {
            PrintDryRunPlan(schemas);
            return 0;
        }

        if (schemas.Count == 0)
        {
            Console.Out.WriteLine("No objects to sync after filtering.");
            return 0;
        }

        await _dropper.DropAllAsync(ct);
        await _schemaWriter.CreateAllAsync(schemas, ct);

        await _bulkWriter.OpenAsync(ct);
        foreach (var schema in schemas)
        {
            try
            {
                _progress.StartTable(schema);
                await foreach (var batch in _dataReader.ReadBatchesAsync(schema, ct))
                {
                    await _bulkWriter.WriteAsync(schema, batch, ct);
                    _progress.ReportBatch(schema, batch.Rows.Count);
                }
                _progress.EndTable(schema);
            }
            catch (Exception ex) when (_options.SkipOnError)
            {
                _progress.ReportSkipped(schema, ex);
                _logger.LogWarning(ex, "Skipping {Object}", schema.DisplayName);
            }
        }

        await VerifyAsync(schemas, ct);
        _progress.PrintSummary();
        return 0;
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(_options.SourceConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Source is missing.");
        if (string.IsNullOrWhiteSpace(_options.TargetConnectionString))
            throw new InvalidOperationException("ConnectionStrings:Target is missing.");
        if (string.IsNullOrWhiteSpace(_options.SourceDatabase))
            throw new InvalidOperationException("Sync:SourceDatabase is missing.");
        if (string.IsNullOrWhiteSpace(_options.TargetDatabase))
            throw new InvalidOperationException("Sync:TargetDatabase is missing.");
        if (_options.BatchSize < 1)
            throw new InvalidOperationException("Sync:BatchSize must be >= 1.");
    }

    private IReadOnlyList<TableSchema> ApplyFilters(IReadOnlyList<TableSchema> all)
    {
        var filtered = all.AsEnumerable();

        if (_options.Whitelist.Count > 0)
        {
            // Whitelist takes priority over the blacklist: only whitelisted objects are synced.
            var whitelist = new HashSet<string>(_options.Whitelist, StringComparer.OrdinalIgnoreCase);
            var matched = all.Where(s => whitelist.Contains(s.DisplayName) || whitelist.Contains(s.ObjectName)).ToList();
            var missing = whitelist.Where(w => !matched.Any(m =>
                string.Equals(m.DisplayName, w, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.ObjectName, w, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var m in missing)
                Console.Error.WriteLine($"WARN: whitelist entry '{m}' not found in source; skipping.");
            _logger.LogInformation("Whitelist active: {Count} of {Total} objects selected", matched.Count, all.Count);
            filtered = matched;
        }
        else if (_options.Blacklist.Count > 0)
        {
            var blacklist = new HashSet<string>(_options.Blacklist, StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(s => !blacklist.Contains(s.DisplayName) && !blacklist.Contains(s.ObjectName));
            var removed = all.Count - filtered.Count();
            if (removed > 0) _logger.LogInformation("Blacklist removed {Count} objects", removed);
        }

        if (_options.TableFilter.Count > 0)
        {
            var wanted = new HashSet<string>(_options.TableFilter, StringComparer.OrdinalIgnoreCase);
            var matched = filtered.Where(s => wanted.Contains(s.DisplayName) || wanted.Contains(s.ObjectName)).ToList();
            var missing = wanted.Where(w => !matched.Any(m =>
                string.Equals(m.DisplayName, w, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.ObjectName, w, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var m in missing)
                Console.Error.WriteLine($"WARN: --table '{m}' not found in source; skipping.");
            filtered = matched;
        }

        return filtered.ToList();
    }

    private void PrintDryRunPlan(IReadOnlyList<TableSchema> schemas)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("=== DRY RUN — no changes will be written ===");
        Console.Out.WriteLine($"Source DB : {_options.SourceDatabase}");
        Console.Out.WriteLine($"Target DB : {_options.TargetDatabase}");
        Console.Out.WriteLine($"Objects   : {schemas.Count}");
        foreach (var s in schemas)
        {
            var tag = s.SourceType == SourceType.View ? "VIEW " : "TABLE";
            Console.Out.WriteLine($"  [{tag}] {s.DisplayName}  ({s.Columns.Count} columns)");
        }
    }

    private async Task VerifyAsync(IReadOnlyList<TableSchema> schemas, CancellationToken ct)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("=== Verification ===");

        var srcConnStr = new SqlConnectionStringBuilder(_options.SourceConnectionString) { InitialCatalog = _options.SourceDatabase }.ConnectionString;
        var tgtConnStr = new SqlConnectionStringBuilder(_options.TargetConnectionString) { InitialCatalog = _options.TargetDatabase }.ConnectionString;

        await using var src = new SqlConnection(srcConnStr);
        await using var tgt = new SqlConnection(tgtConnStr);
        await src.OpenAsync(ct);
        await tgt.OpenAsync(ct);

        foreach (var schema in schemas)
        {
            long srcCount, tgtCount;
            try { srcCount = await CountAsync(src, schema, useNoLock: schema.SourceType == SourceType.Table, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"    WARN: source count failed for {schema.DisplayName}: {ex.Message}"); continue; }

            try { tgtCount = await CountAsync(tgt, schema, useNoLock: false, ct); }
            catch (Exception ex) { Console.Error.WriteLine($"    WARN: target count failed for {schema.DisplayName}: {ex.Message}"); continue; }

            _progress.ReportVerification(schema, srcCount, tgtCount);
        }
    }

    private async Task<long> CountAsync(SqlConnection conn, TableSchema schema, bool useNoLock, CancellationToken ct)
    {
        var hint = useNoLock ? " WITH (NOLOCK)" : "";
        var sql = $"SELECT COUNT_BIG(*) FROM {schema.QualifiedName}{hint}";
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _options.CommandTimeout };
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }
}
