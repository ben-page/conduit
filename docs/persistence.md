# Persistence

Conduit's durable features — the [outbox](outbox.md), [inbox](inbox.md), [dead-letter queue](dead-letter-queue.md), and [audit trail](audit.md) — store data in a relational database through a shared persistence layer. You choose a database **once**, and every durable feature uses it.

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
});
```

`Use*` registers an `IConduitConnectionFactory` (scoped) and the engine's `ISqlDialect` (singleton) into DI. The durable features resolve those from DI and never reference a specific engine.

## Supported engines

| Package | `Use*` method | ADO.NET driver |
|---|---|---|
| `Conduit.Persistence.Sqlite` | `conduit.UseSqlite(connectionString)` | Microsoft.Data.Sqlite (bundled) |
| `Conduit.Persistence.Postgres` | `conduit.UsePostgres(connectionString)` | Npgsql |
| `Conduit.Persistence.SqlServer` | `conduit.UseSqlServer(connectionString)` | Microsoft.Data.SqlClient |
| `Conduit.Persistence.MySql` | `conduit.UseMySql(connectionString)` | MySQL 8.0+ via MySqlConnector |

The driver ships inside the engine package, so there is **no generic connection-type parameter** — write `conduit.UsePostgres("Host=...")`, not `UsePostgres<NpgsqlConnection>(...)`.

## Tables

Each feature creates its table on startup (`conduit_outbox`, `conduit_inbox`, `conduit_dead_letters`, and the audit table). Disable auto-creation per feature (e.g. `AddOutbox(o => o.AutoCreateTable = false)`) if you manage schema through migrations.

## The outbox needs a transaction-aware factory

The connection factory supplied by an engine package is **non-transactional** — it opens fresh connections and cannot see your handler's ambient transaction. That is fine for the inbox, dead-letter queue, and audit, but the [transactional outbox](outbox.md) must write its entry in the *same* transaction as your business data. Enabling `AddOutbox()` against the built-in factory **fails fast at startup**; supply your own transaction-aware `IConduitConnectionFactory` as shown in the [outbox guide](outbox.md#providing-a-transaction).

## One database for all features

Durability and audit share the single engine registered via `Use*`. Pointing them at *different* databases is not supported today — register one engine, and the outbox, inbox, DLQ, and audit all use it.

## Under the hood

The shared abstraction (`IConduitConnectionFactory`, `ISqlDialect`, the disposal-correct `DbStoreBase`, and the table-init / periodic-cleanup hosted-service bases) lives in `Conduit.Persistence`; each engine package implements the full `ISqlDialect` surface for its database. Timestamps are stored as ISO-8601 text for engine-agnostic round-tripping. See [`src/Conduit.Persistence/CLAUDE.md`](../src/Conduit.Persistence/CLAUDE.md) for the internals and the per-engine claim/locking strategies.
