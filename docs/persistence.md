# Persistence

Conduit's durable features — the [outbox](outbox.md), [inbox](inbox.md), [dead-letter queue](dead-letter-queue.md), [audit trail](audit.md), and [background jobs](jobs.md) — store data in a relational database through a shared persistence layer. You choose a database **once**, and every durable feature uses it.

## Choosing an engine

Add the engine package for your database and call its `Use*` method inside `AddConduit`. The engine is registered on the `ConduitBuilder` and shared by all durable features — you do not configure a database per feature.

```xml
<PackageReference Include="Conduit.NET.Persistence.Sqlite" />
```

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.UseSqlite("Data Source=myapp.db");   // pick the database once

    conduit.AddDurability(durability =>
    {
        durability.AddOutbox();
        durability.AddInbox();
        durability.AddDeadLetterQueue();
    });

    conduit.AddAuditListener();                   // uses the same database

    conduit.AddJobs(jobs =>                       // must come *after* UseSqlite — see below
    {
        jobs.Schedule("nightly-rollup").Cron("0 2 * * *").Run(() => new RunNightlyRollup());
    });
});
```

`Use*` registers an `IConduitConnectionFactory` (scoped) and the engine's `ISqlDialect` (singleton) into DI. The durable features resolve those from DI and never reference a specific engine.

Registration order matters for [background jobs](jobs.md) alone. `AddJobs` inspects the service collection to decide whether schedules are durable at all, so `Use*` has to be called before it; every other feature resolves the engine at startup and can be registered in any order. Getting it wrong fails at startup with a message naming the fix, rather than quietly leaving you with a cluster that runs each schedule on every host.

## Supported engines

| Package | `Use*` method | ADO.NET driver |
|---|---|---|
| `Conduit.NET.Persistence.Sqlite` | `conduit.UseSqlite(connectionString)` | Microsoft.Data.Sqlite (bundled) |
| `Conduit.NET.Persistence.Postgres` | `conduit.UsePostgres(connectionString)` | Npgsql |
| `Conduit.NET.Persistence.SqlServer` | `conduit.UseSqlServer(connectionString)` | Microsoft.Data.SqlClient |
| `Conduit.NET.Persistence.MySql` | `conduit.UseMySql(connectionString)` | MySQL 8.0+ via MySqlConnector |

The driver ships inside the engine package, so there is **no generic connection-type parameter** — write `conduit.UsePostgres("Host=...")`, not `UsePostgres<NpgsqlConnection>(...)`.

## Tables

Each feature creates its tables on startup if they are absent, before any of its background services (the outbox relay, the inbox cleanup) makes its first pass:

| Feature | Tables |
|---|---|
| [Outbox](outbox.md) | `conduit_outbox` |
| [Inbox](inbox.md) | `conduit_inbox` |
| [Dead-letter queue](dead-letter-queue.md) | `conduit_dead_letters` |
| [Audit trail](audit.md) | `conduit_audit` |
| [Background jobs](jobs.md) | `conduit_job_schedules`, `conduit_job_runs`, `conduit_job_scopes`, `conduit_job_run_history` |

Three of the five can opt out if you manage schema through migrations — `AddOutbox(o => o.AutoCreateTable = false)`, `AddDeadLetterQueue(dlq => dlq.AutoCreateTable = false)`, and `AddJobs(jobs => jobs.AutoCreateTable = false)`. The inbox and the audit trail have no such flag and always create their table.

### Replicas starting together

Several replicas of one deployment can start at once on an empty database, and each creates the tables. Every table and index is created by its own create-if-absent statement, and a statement the engine refuses because another replica created the same table or index first is passed over: the object it was creating exists, and the statements after it still run, so each replica still adds the indexes of a table another replica created. Every replica starts.

How each engine gets there:

| Engine | Two replicas creating the same table |
|---|---|
| SQLite | One writer at a time, so `CREATE ... IF NOT EXISTS` is atomic: the second waits, then finds the table |
| PostgreSQL | Both pass `IF NOT EXISTS`; the second is refused with `42P07`, `42710`, or a unique violation (`23505`) in `pg_catalog`, and passed over |
| SQL Server | Each create holds an exclusive application lock (`sp_getapplock`) for its transaction, at `READ COMMITTED`, so creates run one at a time and each finds what the one before made. Without the lock, two `CREATE INDEX` on one table deadlock. A create racing a process that takes no such lock (a migration) is refused with `2714` or `1913`, and passed over |
| MySQL | `CREATE TABLE IF NOT EXISTS` waits on the table's metadata lock, then finds the table; `1050` and `1061` are passed over should they occur |

### The outbox table

A migration that creates `conduit_outbox` itself needs these columns (timestamps are UTC ISO-8601 text, as everywhere: see [Timestamps](#timestamps)). A column an index covers needs a type the engine can index: on SQL Server and MySQL, whose general text type (`NVARCHAR(MAX)`, `LONGTEXT`) cannot be an index key, `MessageId`, `ClaimToken`, `NextAttemptAt` and `SentAt` are `NVARCHAR(450)` on SQL Server and `VARCHAR(255)` on MySQL.

| Column | Holds |
|---|---|
| `Id` | Auto-increment primary key; the relay claims in its order |
| `MessageId`, `ConversationId`, `CausationId` | The entry's message context (`CausationId` nullable) |
| `Type`, `Payload` | The message's full type name and its JSON body |
| `Status` | `0` Pending, `1` Sent, `2` Failed, `3` Claiming |
| `CreatedAt` | When the entry was written; the grace period counts from it |
| `SentAt` | When it was sent (nullable); the Sent [retention](outbox.md#retention) counts from it |
| `FailedAt` | When its last attempt failed (nullable): set when it is marked Failed, cleared by a [retry](outbox.md#retrying-failed-entries); the Failed retention counts from it |
| `RetryCount` | How many of its attempts failed and were retried |
| `NextAttemptAt` | When it is next due: its `CreatedAt` when written, moved out by each failed send's [backoff](outbox.md#backoff), reset to now by a [retry](outbox.md#retrying-failed-entries) |
| `ClaimedAt`, `ClaimToken` | The claim of the worker delivering it (nullable) |
| `Properties` | The [dispatch properties](outbox.md#carrying-dispatch-properties) it carries (nullable; read and written only by a host that lists some) |

And these indexes, besides the primary key:

| Index | Columns | Serves |
|---|---|---|
| `ix_conduit_outbox_status_next_attempt` | `Status`, `NextAttemptAt` | The relay's claim: pending entries that are due, and claims gone stale. Its `Status` prefix also finds the Failed entries, for listing, retrying and the Failed retention |
| `ix_conduit_outbox_claim` | `ClaimToken` | Reading back the rows a claim just took |
| `ix_conduit_outbox_message_id` | `MessageId` | The [post-commit flush](outbox.md#post-commit-flush)'s claim by message id |
| `ix_conduit_outbox_status_sent` | `Status`, `SentAt` | The Sent retention, over the bulk of the table |

`AutoCreateTable` creates them with the table and, on SQLite, PostgreSQL and SQL Server, adds any an existing table lacks. MySQL has no `CREATE INDEX IF NOT EXISTS`, so there the indexes come only with a new table.

Startup creates tables and indexes but never alters a column. A `conduit_outbox` created by an earlier pre-release build must therefore be dropped before this build starts, or migrated: add `FailedAt`, and give `MessageId`, `ClaimToken`, `NextAttemptAt` and `SentAt` the bounded types above. Left as it is, the table fails when an entry is marked Failed and in every Failed retention cleanup, since `FailedAt` is missing. On SQL Server, startup also fails, because the indexes cannot be created over `NVARCHAR(MAX)` columns.

### The dead-letter table

Besides the entry's own fields (see the [metadata](dead-letter-queue.md#metadata)), `conduit_dead_letters` has two nullable columns a [replay](dead-letter-queue.md#what-happens-during-replay) claims an entry with: `ReplayClaimToken` (text) and `ReplayClaimedAt` (a timestamp). A `conduit_dead_letters` created by an earlier pre-release build lacks them, so every replay fails until the table is dropped or the two columns are added.

## Timestamps

Every timestamp the stores write is the UTC instant as ISO-8601 text in one fixed form, `yyyy-MM-ddTHH:mm:ss.fffffffZ`, and every time bound they compare it with (a claim's cutoff, a retention cutoff, the dead-letter queue's `Since`/`Until`) is written the same way. The engines compare the text, and text in one form sorts as the instants do. A value in another offset is converted first: a caller's `DeadLetterFilter.Since` of `13:00+01:00` matches an entry captured at `12:00Z`, and an `OutboxEntry.CreatedAt` of kind `Local` is stored as its UTC instant.

## The clock

Every "now" the durable features read comes from the host's `TimeProvider`: the outbox's claims, backoff, outcome stamps, retention cutoffs and new entries' creation time, the inbox's record time and retention, the dead-letter queue's capture time and replay claims, the audit trail's record, batch and purge times and the inactivity after which a dispatch gets a provisional record, and every scheduling decision of [background jobs](jobs.md). `AddDurability`, `AddAudit`, `AddAuditListener` and `AddJobs` register `TimeProvider.System` when the host registers none; a host that registers its own, before or after `AddConduit`, keeps it.

The waits between passes (the outbox's `RelayInterval`, the cleanup and purge intervals) are real time. A test that registers a `FakeTimeProvider` therefore moves every cutoff by advancing the clock, and a clock that never moves holds them still: an entry written at the fake instant waits out the outbox's `GracePeriod` (10 s by default) only once the clock has advanced past it, so the relay does not claim it before then. The post-commit flush does not wait for the grace period.

## The outbox needs a transaction-aware factory

The connection factory supplied by an engine package is **non-transactional** — it opens fresh connections and cannot see your handler's ambient transaction. That is fine for the inbox, dead-letter queue, audit trail, and jobs, but the [transactional outbox](outbox.md) must write its entry in the *same* transaction as your business data. Enabling `AddOutbox()` against the built-in factory **fails fast at startup**; supply your own transaction-aware `IConduitConnectionFactory` as shown in the [outbox guide](outbox.md#providing-a-transaction).

## One database for all features

Durability, audit, and jobs share the single engine registered via `Use*`. Pointing them at *different* databases is not supported today — register one engine, and the outbox, inbox, DLQ, audit trail, and job ledger all use it.

## Under the hood

The shared abstraction (`IConduitConnectionFactory`, `ISqlDialect`, the disposal-correct `DbStoreBase`, and the table-init / periodic-cleanup hosted-service bases) lives in `Conduit.Persistence`; each engine package implements the full `ISqlDialect` surface for its database, including `IsAlreadyExists`, which recognizes the engine's refusal of a concurrent create. Timestamps are stored as UTC ISO-8601 text for engine-agnostic round-tripping. An engine package's connection factory disposes a connection that fails to open before it rethrows. The per-engine claim locking is in the [outbox guide](outbox.md#database-dialects).
