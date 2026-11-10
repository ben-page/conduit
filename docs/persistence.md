# Persistence

Conduit's durable features — the [outbox](outbox.md), [inbox](inbox.md), [dead-letter queue](dead-letter-queue.md), [audit trail](audit.md), and [background jobs](jobs.md) — store data in a relational database through a shared persistence layer. You choose a database **once**, and every durable feature uses it.

## Choosing an engine

Add the engine package for your database and call its `Use*` method inside `AddConduit`. The engine is registered on the `ConduitBuilder` and shared by all durable features — you do not configure a database per feature.

```xml
<PackageReference Include="Conduit.Persistence.Sqlite" />
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
| `Conduit.Persistence.Sqlite` | `conduit.UseSqlite(connectionString)` | Microsoft.Data.Sqlite (bundled) |
| `Conduit.Persistence.Postgres` | `conduit.UsePostgres(connectionString)` | Npgsql |
| `Conduit.Persistence.SqlServer` | `conduit.UseSqlServer(connectionString)` | Microsoft.Data.SqlClient |
| `Conduit.Persistence.MySql` | `conduit.UseMySql(connectionString)` | MySQL 8.0+ via MySqlConnector |

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

### The outbox table

A migration that creates `conduit_outbox` itself needs these columns (timestamps are ISO-8601 text, as everywhere). A column an index covers needs a type the engine can index: on SQL Server and MySQL, whose general text type (`NVARCHAR(MAX)`, `LONGTEXT`) cannot be an index key, `MessageId`, `ClaimToken`, `NextAttemptAt` and `SentAt` are `NVARCHAR(450)` on SQL Server and `VARCHAR(255)` on MySQL.

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
| `ix_conduit_outbox_message_id` | `MessageId` | The [immediate send](outbox.md#immediate-send)'s claim by message id |
| `ix_conduit_outbox_status_sent` | `Status`, `SentAt` | The Sent retention, over the bulk of the table |

`AutoCreateTable` creates them with the table and, on SQLite, PostgreSQL and SQL Server, adds any an existing table lacks. MySQL has no `CREATE INDEX IF NOT EXISTS`, so there the indexes come only with a new table.

## The outbox needs a transaction-aware factory

The connection factory supplied by an engine package is **non-transactional** — it opens fresh connections and cannot see your handler's ambient transaction. That is fine for the inbox, dead-letter queue, audit trail, and jobs, but the [transactional outbox](outbox.md) must write its entry in the *same* transaction as your business data. Enabling `AddOutbox()` against the built-in factory **fails fast at startup**; supply your own transaction-aware `IConduitConnectionFactory` as shown in the [outbox guide](outbox.md#providing-a-transaction).

## One database for all features

Durability, audit, and jobs share the single engine registered via `Use*`. Pointing them at *different* databases is not supported today — register one engine, and the outbox, inbox, DLQ, audit trail, and job ledger all use it.

## Under the hood

The shared abstraction (`IConduitConnectionFactory`, `ISqlDialect`, the disposal-correct `DbStoreBase`, and the table-init / periodic-cleanup hosted-service bases) lives in `Conduit.Persistence`; each engine package implements the full `ISqlDialect` surface for its database. Timestamps are stored as ISO-8601 text for engine-agnostic round-tripping. See [`src/Conduit.Persistence/CLAUDE.md`](../src/Conduit.Persistence/CLAUDE.md) for the internals and the per-engine claim/locking strategies.
