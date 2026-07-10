# DataSync

.NET 8 console app that syncs a database from a source MSSQL server (Testing) to a target MSSQL server (Develop) via a full **drop-and-recreate** with batched bulk copy.

Both tables and views from the source are recreated as **plain flat tables** on the target — no keys, no indexes, no constraints. See [datasync-spec.md](datasync-spec.md) for the full functional specification.

> ⚠️ **The sync is destructive on the target**: every run drops **all** tables in the target database before recreating the selected objects — even when a whitelist, blacklist, or `--table` filter limits what gets recreated.

## Requirements

- .NET 8 SDK
- Network access to both the source and target SQL Server instances

## Build & run

```powershell
dotnet build DataSync.sln
dotnet run --project DataSync            # full sync
dotnet run --project DataSync -- --dry-run
```

Or run the published binary:

```powershell
dotnet publish DataSync -c Release
.\DataSync\bin\Release\net8.0\publish\DataSync.exe --dry-run
```

## Configuration — `appsettings.json`

```json
{
  "ConnectionStrings": {
    "Source": "Server=SOURCE_SERVER;Database=master;Integrated Security=true;TrustServerCertificate=true;",
    "Target": "Server=TARGET_SERVER;Database=master;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Sync": {
    "SourceDatabase": "your_database",
    "TargetDatabase": "your_database",
    "BatchSize": 5000,
    "BulkCopyTimeout": 120,
    "CommandTimeout": 60,
    "StrictNullability": false,
    "Whitelist": [],
    "Blacklist": []
  }
}
```

### Local overrides — `appsettings.local.json`

`appsettings.json` in the repo holds placeholder values only. Real connection strings and database names belong in `DataSync/appsettings.local.json`, which is **gitignored** and loaded automatically (optional, applied on top of `appsettings.json`) by `Program.cs`. Copy the keys you need to override:

```json
{
  "ConnectionStrings": {
    "Source": "Server=localhost\\SQL2022;Database=master;Integrated Security=true;TrustServerCertificate=true;",
    "Target": "Server=localhost\\SQLEXPRESS;Database=master;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Sync": {
    "SourceDatabase": "esticon_db",
    "TargetDatabase": "esticon_db"
  }
}
```

You only need to include the keys you want to override — anything omitted falls back to `appsettings.json`. The build copies `appsettings.local.json` to the output directory automatically when the file exists.

| Key | Type | Description |
|---|---|---|
| `ConnectionStrings:Source` | string | Source (Testing) server connection string |
| `ConnectionStrings:Target` | string | Target (Develop) server connection string |
| `Sync:SourceDatabase` | string | Database name on the source server |
| `Sync:TargetDatabase` | string | Database name on the target server |
| `Sync:BatchSize` | int | Rows per bulk-copy batch (default `5000`) |
| `Sync:BulkCopyTimeout` | int | Seconds per `SqlBulkCopy` call (default `120`) |
| `Sync:CommandTimeout` | int | Seconds for schema/count queries (default `60`) |
| `Sync:StrictNullability` | bool | `true` = copy `NOT NULL` from source; `false` = all columns nullable (default) |
| `Sync:Whitelist` | string[] | If non-empty, **only** these objects are synced (blacklist is ignored) |
| `Sync:Blacklist` | string[] | Objects to exclude from the sync (only applies when the whitelist is empty) |

### Whitelist and blacklist

Both lists accept object names with or without the schema prefix, case-insensitive — e.g. `"dbo.Customers"` or just `"Customers"`. Tables and views are matched the same way.

- **Whitelist takes priority.** When `Whitelist` has entries, only those objects are synced and the `Blacklist` is ignored entirely.
- When `Whitelist` is empty, the `Blacklist` removes the listed objects from the sync.
- Whitelist entries that don't exist in the source produce a warning and are skipped (never a hard failure).

Example — sync only two tables:

```json
"Whitelist": [ "dbo.Customers", "dbo.Orders" ]
```

Example — sync everything except audit tables:

```json
"Whitelist": [],
"Blacklist": [ "dbo.AuditLog", "dbo.EventHistory" ]
```

## CLI arguments

| Argument | Effect |
|---|---|
| `--dry-run` | Prints the sync plan (objects, types, column counts), writes nothing to the target |
| `--table <name>` | Sync only the named object(s); repeatable (`--table A --table B` or `--table A B`). Applied on top of the whitelist/blacklist result |
| `--verbose` | Logs every batch write with row count and timing |
| `--skip-on-error` | Logs failed tables/views and continues instead of aborting |

Filter order: **whitelist → blacklist → `--table`**. The `--table` filter narrows whatever the config lists allowed, so a `--table` name excluded by the config will not sync.

Tip: use `--dry-run` to verify what a given whitelist/blacklist/`--table` combination will sync before running for real.

## How a sync run works

1. **Schema read** — discovers all tables and views in the source database and their column definitions, then applies the whitelist/blacklist/`--table` filters.
2. **Drop & recreate** — drops all existing tables on the target (single transaction, rolls back on failure), then emits `CREATE TABLE` for each selected object.
3. **Bulk copy** — streams rows from the source in `BatchSize` batches via `SqlBulkCopy`. Memory stays flat regardless of table size.
4. **Verification** — compares row counts between source and target per object and prints a summary (objects synced, rows written, elapsed time, mismatches, skips).

Exit code is `0` on success, non-zero on hard failure, so the app can be used in scripts/CI. Progress goes to stdout, errors to stderr.
