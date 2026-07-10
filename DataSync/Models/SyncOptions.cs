namespace DataSync.Models;

public class SyncOptions
{
    public string SourceDatabase { get; set; } = "DataSource";
    public string TargetDatabase { get; set; } = "DataSource";
    public int BatchSize { get; set; } = 5000;
    public int BulkCopyTimeout { get; set; } = 120;
    public int CommandTimeout { get; set; } = 60;
    public bool StrictNullability { get; set; } = false;
    public List<string> Whitelist { get; set; } = new();
    public List<string> Blacklist { get; set; } = new();

    public string SourceConnectionString { get; set; } = "";
    public string TargetConnectionString { get; set; } = "";

    public bool DryRun { get; set; }
    public IReadOnlyList<string> TableFilter { get; set; } = Array.Empty<string>();
    public bool Verbose { get; set; }
    public bool SkipOnError { get; set; }
}
