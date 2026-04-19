using System.Diagnostics;
using DataSync.Models;

namespace DataSync.Helpers;

public class ProgressReporter
{
    private readonly bool _verbose;
    private readonly Stopwatch _overall = new();
    private readonly Stopwatch _current = new();
    private long _currentRows;
    private long _totalRows;
    private int _objectCount;
    private readonly List<string> _skipped = new();
    private readonly List<string> _mismatches = new();

    public ProgressReporter(bool verbose)
    {
        _verbose = verbose;
    }

    public void StartRun()
    {
        _overall.Restart();
        Console.Out.WriteLine($"[{DateTime.Now:HH:mm:ss}] Sync started");
    }

    public void StartTable(TableSchema schema)
    {
        _current.Restart();
        _currentRows = 0;
        var tag = schema.SourceType == SourceType.View ? "[VIEW]" : "[TABLE]";
        Console.Out.WriteLine($"{tag} {schema.DisplayName} → syncing...");
    }

    public void ReportBatch(TableSchema schema, int rowCount)
    {
        _currentRows += rowCount;
        if (_verbose)
        {
            Console.Out.WriteLine($"    batch: {rowCount} rows ({_currentRows} total, {_current.Elapsed.TotalSeconds:F1}s)");
        }
    }

    public void EndTable(TableSchema schema)
    {
        _objectCount++;
        _totalRows += _currentRows;
        Console.Out.WriteLine($"    done: {_currentRows:N0} rows in {_current.Elapsed.TotalSeconds:F1}s");
    }

    public void ReportSkipped(TableSchema schema, Exception ex)
    {
        _skipped.Add(schema.DisplayName);
        Console.Error.WriteLine($"    SKIPPED {schema.DisplayName}: {ex.Message}");
    }

    public void ReportVerification(TableSchema schema, long sourceCount, long targetCount)
    {
        if (sourceCount == targetCount)
        {
            Console.Out.WriteLine($"    OK ({sourceCount:N0} rows) {schema.DisplayName}");
        }
        else
        {
            _mismatches.Add(schema.DisplayName);
            Console.Out.WriteLine($"    MISMATCH source={sourceCount:N0} target={targetCount:N0} {schema.DisplayName}");
        }
    }

    public void PrintSummary()
    {
        _overall.Stop();
        Console.Out.WriteLine();
        Console.Out.WriteLine("=== Sync summary ===");
        Console.Out.WriteLine($"Objects synced : {_objectCount}");
        Console.Out.WriteLine($"Rows written   : {_totalRows:N0}");
        Console.Out.WriteLine($"Elapsed        : {_overall.Elapsed.TotalSeconds:F1}s");
        Console.Out.WriteLine($"Mismatches     : {_mismatches.Count}");
        Console.Out.WriteLine($"Skipped        : {_skipped.Count}");
        if (_skipped.Count > 0)
            foreach (var s in _skipped) Console.Out.WriteLine($"  - skipped: {s}");
        if (_mismatches.Count > 0)
            foreach (var m in _mismatches) Console.Out.WriteLine($"  - mismatch: {m}");
    }
}
