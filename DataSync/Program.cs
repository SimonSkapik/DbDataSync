using System.CommandLine;
using System.CommandLine.Invocation;
using DataSync.Helpers;
using DataSync.Models;
using DataSync.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataSync;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var dryRunOpt = new Option<bool>("--dry-run", "Print plan only, write nothing to target.");
        var tableOpt = new Option<string[]>("--table", "Sync only the named object(s). Repeatable.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var verboseOpt = new Option<bool>("--verbose", "Log every batch write.");
        var skipOnErrorOpt = new Option<bool>("--skip-on-error", "Log failed objects and continue.");

        var root = new RootCommand("DataSync — drop-and-recreate sync from Testing to Develop")
        {
            dryRunOpt, tableOpt, verboseOpt, skipOnErrorOpt
        };

        root.SetHandler(async (InvocationContext ctx) =>
        {
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOpt);
            var tables = ctx.ParseResult.GetValueForOption(tableOpt) ?? Array.Empty<string>();
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);
            var skipOnError = ctx.ParseResult.GetValueForOption(skipOnErrorOpt);
#if DEBUG
            verbose = true;
#endif

            ctx.ExitCode = await RunAsync(dryRun, tables, verbose, skipOnError, ctx.GetCancellationToken());
        });

        var exitCode = await root.InvokeAsync(args);
#if DEBUG
        if (!Console.IsInputRedirected)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Press any key to exit...");
            Console.ReadKey(intercept: true);
        }
#endif
        return exitCode;
    }

    private static async Task<int> RunAsync(bool dryRun, string[] tables, bool verbose, bool skipOnError, CancellationToken ct)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var options = new SyncOptions();
        config.GetSection("Sync").Bind(options);
        options.SourceConnectionString = config.GetConnectionString("Source") ?? "";
        options.TargetConnectionString = config.GetConnectionString("Target") ?? "";
        options.DryRun = dryRun;
        options.TableFilter = tables;
        options.Verbose = verbose;
        options.SkipOnError = skipOnError;

        using var sp = new ServiceCollection()
            .AddSingleton(options)
            .AddLogging(b => b
                .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
                .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information))
            .AddSingleton<SchemaReader>()
            .AddSingleton<TableDropper>()
            .AddSingleton<SchemaWriter>()
            .AddSingleton<DataReader>()
            .AddSingleton<BulkWriter>()
            .AddSingleton(new ProgressReporter(verbose))
            .AddSingleton<SyncOrchestrator>()
            .BuildServiceProvider();

        var orchestrator = sp.GetRequiredService<SyncOrchestrator>();
        var bulkWriter = sp.GetRequiredService<BulkWriter>();
        try
        {
            return await orchestrator.RunAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            var logger = sp.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Sync aborted");
            return 1;
        }
        finally
        {
            await bulkWriter.DisposeAsync();
        }
    }
}
