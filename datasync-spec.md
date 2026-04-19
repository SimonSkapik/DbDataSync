# DataSync — Functional Specification
> .NET 8 console application  
> Syncs `DataSource` DB from a Testing MSSQL server to a Develop MSSQL server via full drop-and-recreate with batched bulk copy.

---

## Goal

Automate the manual process of keeping `Develop.DataSource` in sync with `Testing.DataSource`. The Develop server has no network access to Testing. The app runs with a connection to both servers and performs a full drop-and-recreate of the target database on demand.

Both tables and views from the source are recreated as **plain flat tables** on the target — no keys, no indexes, no constraints.

---

## Project structure

```
DataSync/
├── Program.cs                  # Entry point, DI wiring, CLI parsing
├── appsettings.json            # Connection strings, batch config
├── Models/
│   ├── SyncOptions.cs          # Strongly-typed config + CLI args
│   └── TableSchema.cs          # Column name, SQL type, ordinal, SourceType enum
├── Services/
│   ├── SchemaReader.cs         # Reads table + view list and column definitions from source
│   ├── SchemaWriter.cs         # Emits CREATE TABLE DDL on target
│   ├── TableDropper.cs         # Drops all tables on target safely
│   ├── DataReader.cs           # Streams rows from source in batches
│   ├── BulkWriter.cs           # SqlBulkCopy wrapper
│   └── SyncOrchestrator.cs     # Coordinates the four phases
└── Helpers/
    └── ProgressReporter.cs     # Console progress + timing
```

---

## Configuration — `appsettings.json`

```json
{
  "ConnectionStrings": {
    "Source": "<Testing server connection string>",
    "Target": "<Develop server connection string>"
  },
  "Sync": {
    "BatchSize": 5000,
    "BulkCopyTimeout": 120,
    "CommandTimeout": 60
  }
}
```

| Key | Type | Description |
|---|---|---|
| `ConnectionStrings:Source` | string | Testing server connection string |
| `ConnectionStrings:Target` | string | Develop server connection string |
| `Sync:BatchSize` | int | Rows per batch (default `5000`) |
| `Sync:BulkCopyTimeout` | int | Seconds per `SqlBulkCopy` call (default `120`) |
| `Sync:CommandTimeout` | int | Seconds for schema queries (default `60`) |

---

## CLI arguments

| Argument | Type | Effect |
|---|---|---|
| `--dry-run` | flag | Prints what would happen, writes nothing to target |
| `--table <name>` | repeatable | Sync only the named table(s); omit to sync all |
| `--verbose` | flag | Logs every batch write with row count and timing |
| `--skip-on-error` | flag | Logs failed tables/views and continues rather than aborting |

---

## Models

### `SourceType` enum

```csharp
public enum SourceType { Table, View }
```

### `TableSchema`

```csharp
public record TableSchema(
    string SchemaName,
    string ObjectName,
    SourceType SourceType,
    IReadOnlyList<ColumnSchema> Columns
);

public record ColumnSchema(
    string ColumnName,
    string DataType,
    int OrdinalPosition,
    bool IsNullable
);
```

---

## Phase 1 — `SchemaReader`

**Purpose:** Discover all objects to sync and their column definitions.

**Steps:**

1. Open a connection to source `DataSource` DB.
2. Query `INFORMATION_SCHEMA.TABLES` for **both** `TABLE_TYPE = 'BASE TABLE'` and `TABLE_TYPE = 'VIEW'` to get the full object list.
3. For each object (table or view), query `INFORMATION_SCHEMA.COLUMNS` ordered by `ORDINAL_POSITION` to collect column names and `DATA_TYPE`. This works identically for views in SQL Server.
4. Tag each entry with `SourceType.Table` or `SourceType.View` — used only for progress logging (e.g. `[VIEW] dbo.CustomerSummary → recreating as table`).
5. Return `IReadOnlyList<TableSchema>`. Downstream phases treat all entries identically regardless of `SourceType`.

**Key note on views:** Views on the source are materialised data sources (they pull from other DBs not available on Develop). Reading column metadata from `INFORMATION_SCHEMA.COLUMNS` works the same as for tables. Data is read in Phase 3 via `SELECT * FROM [viewName]` — no view definition is scripted or recreated.

---

## Phase 2 — `TableDropper` + `SchemaWriter`

### TableDropper

1. Open target connection.
2. Fetch all existing table names from target `INFORMATION_SCHEMA.TABLES`.
3. Execute `DROP TABLE IF EXISTS [schema].[name]` for each, in reverse order (defensive against any residual constraints from a prior sync).
4. Wrap all drops in a single transaction — rolls back entirely if any drop fails.

### SchemaWriter

For each `TableSchema` from Phase 1:

1. Emit a `CREATE TABLE [schema].[name]` statement using the column list.
2. Map column types directly from source SQL Server type strings — no type coercion.
3. **No primary keys. No foreign keys. No indexes. No IDENTITY specs.** Plain flat tables only.
4. Mark columns as `NULL` by default unless source schema explicitly has `NOT NULL` (controlled by optional `Sync:StrictNullability` config bool, default `false`).

---

## Phase 3 — `DataReader` + `BulkWriter` (per object, sequential)

### DataReader

- Opens a `SqlConnection` to source with `CommandTimeout` from config.
- Issues `SELECT * FROM [schema].[objectName] WITH (NOLOCK)` for tables. For views, `WITH (NOLOCK)` is omitted (not valid on views) — use plain `SELECT * FROM [schema].[viewName]`.
- Returns an open `SqlDataReader` — caller is responsible for disposal.
- Reads in a loop, accumulating rows into a `DataTable` up to `BatchSize`.
- When the `DataTable` reaches `BatchSize`, yields it to `BulkWriter` and resets.
- After `IDataReader.Read()` returns false, flushes any remaining partial batch.
- **Memory stays flat** regardless of source row count.

### BulkWriter

- Receives each `DataTable` batch.
- Calls `SqlBulkCopy.WriteToServer(dataTable)`.
- `SqlBulkCopy` settings:
  - `SqlBulkCopyOptions.TableLock = true`
  - `BatchSize = options.BatchSize`
  - `BulkCopyTimeout = options.BulkCopyTimeout`
- Column mapping is automatic — column names match by construction from Phase 2.
- Accumulates total rows written per object; reports to `ProgressReporter`.

### Batch + bulk write loop (pseudocode)

```
Open SqlDataReader on source object
Initialise empty DataTable (columns from TableSchema)
rowsInBatch = 0

while reader.Read():
    append row to DataTable
    rowsInBatch++
    if rowsInBatch == BatchSize:
        BulkWriter.Write(DataTable)
        DataTable.Clear()
        rowsInBatch = 0

if rowsInBatch > 0:
    BulkWriter.Write(DataTable)   // flush final partial batch

Close reader
```

---

## Phase 4 — Verification

- For each synced object, query `SELECT COUNT(*) FROM [schema].[name]` on both source and target.
- Log result per object:
  - `OK (12,340 rows)` — counts match
  - `MISMATCH source=12,340 target=12,331` — counts differ (warning, not hard failure; `NOLOCK` on large tables can cause minor variance)
- Print final summary at exit:
  - Total objects synced
  - Total rows written
  - Total elapsed time
  - Count of mismatches (if any)
  - Count of skipped objects (if `--skip-on-error` was used)

---

## `SyncOrchestrator` — coordination logic

```
1.  Parse + validate SyncOptions — fail fast on missing/invalid config
2.  Phase 1 → build List<TableSchema>
        if --table args provided → filter list to named objects only
3.  If --dry-run:
        print full plan (object name, type, column count) and exit
4.  Phase 2 → TableDropper.DropAll() then SchemaWriter.CreateAll()
5.  For each TableSchema in list:
        a. ProgressReporter.StartTable(name, sourceType)
        b. Open DataReader on source
        c. For each batch:
               BulkWriter.Write(batch)
               ProgressReporter.ReportBatch(rowCount)
        d. ProgressReporter.EndTable(totalRows, elapsed)
        e. On exception:
               if --skip-on-error → log warning, add to skipped list, continue
               else → rethrow (aborts remaining sync)
6.  Phase 4 → row count verification
7.  ProgressReporter.PrintSummary()
```

---

## Error handling

| Scenario | Behaviour |
|---|---|
| Source connection failure | Hard abort before any target writes — target is untouched |
| Target connection failure | Hard abort |
| Drop/recreate failure | Transaction rolls back, hard abort |
| Per-object copy failure | Abort (default) or skip + log (with `--skip-on-error`) |
| Batch write timeout | Retry once with half the batch size; if still fails, treat as object-level error |
| `--table <name>` not found in source | Warn and skip (never a hard abort — the name may be a typo) |

---

## Technology choices

| Concern | Choice | Reason |
|---|---|---|
| SQL access | `Microsoft.Data.SqlClient` | Official, actively maintained |
| Bulk insert | `SqlBulkCopy` (built-in) | No extra dependency, handles large sets natively |
| Config | `Microsoft.Extensions.Configuration` | Standard .NET 8 pattern |
| Logging | `Microsoft.Extensions.Logging` + console sink | Structured; easy to swap for file output later |
| CLI parsing | `System.CommandLine` | First-party, composable |
| DI | `Microsoft.Extensions.DependencyInjection` | Standard .NET 8 generic host |

---

## Implementation notes for Claude Code

- Target framework: `net8.0`
- Use `IHostedService` or a simple top-level `await` in `Program.cs` — no need for a full hosted service if the app is purely a one-shot CLI tool.
- `SqlBulkCopy` must be instantiated with `SqlBulkCopyOptions.UseInternalTransaction` **or** managed externally — do not mix with an ambient `SqlTransaction` on the same connection.
- `DataTable` reuse (`.Clear()` + reuse) is preferred over allocating a new one per batch to reduce GC pressure on large tables.
- `WITH (NOLOCK)` hint: apply only to `BASE TABLE` sources, not views.
- All connection strings must be read from config — never hardcoded.
- The app should return a non-zero exit code on hard failure so it can be used in scripts/CI.
- Progress output should go to stdout; errors to stderr.
