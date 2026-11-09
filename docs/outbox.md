# Transactional Outbox

- [Why use an outbox?](#why-use-an-outbox)
  - [When to use it](#when-to-use-it)
- [The dual-write problem](#the-dual-write-problem)
  - [Why not a distributed transaction?](#why-not-a-distributed-transaction)
  - [The solution](#the-solution)
- [How it works](#how-it-works)
- [Setup](#setup)
- [Providing a transaction](#providing-a-transaction)
- [Database dialects](#database-dialects)
- [Custom stores](#custom-stores)
- [Relay configuration](#relay-configuration)
- [What gets captured](#what-gets-captured)
- [The Immediate escape hatch](#the-immediate-escape-hatch)
- [Publishing after the pipeline](#publishing-after-the-pipeline)
- [Immediate send](#immediate-send)
- [Limitations](#limitations)

## Why use an outbox?

Without an outbox, publishing a message is a best-effort operation. Several things can go wrong:

**Your application crashes.** The handler saves an order to the database, then calls `PublishAsync`. The process crashes — or the container is killed, or a deployment rolls through — after the database commit but before the message reaches the broker. The order exists. No other service knows about it.

**The broker is down.** RabbitMQ is restarting, the SQS endpoint is unreachable, or the Azure Service Bus namespace is throttling. The database commit succeeds but the publish call fails. The message is lost unless you catch the exception and retry — but retry logic is fragile and doesn't survive process restarts.

**You publish before committing.** Publishing first avoids losing the message, but creates a worse problem. A downstream service receives `OrderCreated`, queries your API for the order, and gets a 404 — because the transaction hasn't committed yet, or it rolled back. Now downstream services are reacting to data that doesn't exist.

These aren't edge cases. In a distributed system with multiple services, they're inevitable. The question is whether your architecture handles them or hopes they don't happen.

### When to use it

Use the outbox when:
- **Reliability matters.** Messages must not be silently lost when the broker is temporarily unavailable or the process crashes at the wrong moment.
- **Consistency matters.** Downstream services should never see events about data that hasn't been committed, and committed data should always produce its events.
- **You have a database.** The outbox shares a transaction with your business data. If your service is stateless (no database), the outbox doesn't apply — the broker's own delivery guarantees are sufficient.

You may not need it when:
- You're comfortable with best-effort delivery and can tolerate occasional lost messages.
- Your messages are idempotent regenerations (e.g., a cache invalidation that can be repeated on the next request).
- You're in a development or prototyping phase where reliability isn't the focus yet.

## The dual-write problem

The root cause is that your handler writes to two separate systems — the database and the message broker — without a shared transaction. This is called the **dual-write problem**.

```
1. Save order to database        ← succeeds
2. Publish OrderCreated event     ← process crashes here — event lost
```

No matter which order you do the writes, one of them can fail after the other succeeds, leaving the two systems inconsistent.

### Why not a distributed transaction?

Two-phase commit (2PC) could make the database and broker atomic, but it's not viable in practice:

| Broker | 2PC Support |
|---|---|
| Azure Service Bus | Only with SQL Server on Windows via MSDTC |
| RabbitMQ | None — publisher confirms are async, not transactional with a DB |
| Amazon SQS | None |
| ActiveMQ | Requires MSDTC (AmqpNetLite doesn't support enlistment) |

Even where technically possible (ASB + SQL Server), MSDTC is Windows-only, incompatible with containers and cloud-native deployments, and adds a single point of failure. The industry moved away from 2PC for this reason.

### The solution

The outbox pattern sidesteps the problem by writing the message to your database instead of the broker. Since both writes (business data + outbox entry) hit the same database, they share a single transaction — no distributed coordination needed:

```
1. Save order to database        ← same transaction
2. Write outbox entry             ← same transaction
3. Commit                         ← atomic, single database
4. Relay sends to broker          ← eventually, with retries
```

The tradeoff is eventual consistency — messages arrive at the broker with a small delay (the relay polling interval). But delivery is guaranteed as long as the database is durable.

## How it works

```
Handler executes (in managed transaction)
  ├── Business data written to DB
  ├── Handler calls PublishAsync(event) or a routed [FireAndForget] command
  │     → IOutboundInterceptor.Intercept(envelope) offered first (skipped entirely for
  │       Immediate messages — see "The Immediate escape hatch")
  │       - ambient transaction present → captured (true): local dispatch
  │         (events only — routed commands have no local step to skip) AND
  │         the transport send are both skipped here; the outbox delivers
  │         both, post-commit
  │       - no ambient transaction → declined (false): normal path — local dispatch
  │         (events; commands go to their route) + direct transport send
  ├── OutboxBehavior writes captured envelopes to the outbox table (same transaction) —
  │     once each: it ends every pipeline in the scope, nested inline-event pipelines
  │     included, and writes only the envelopes no earlier pipeline end has written
  └── Transaction commits (business data + outbox entries, atomic)

The delivery IOutboxFlusher.FlushAsync schedules on the background dispatcher (optional —
"Immediate send" below; the caller never waits for it) and OutboxRelay (background service —
safety net) apply one per-entry delivery contract:
  ├── Local listeners first, from a fresh DI scope (redelivery-safe — see below)
  ├── Then the transport send
  ├── Marks the entry as sent on success
  └── On failure: retry count increments and the claim releases for the next sweep
      (the relay marks an entry Failed once MaxRetries is exhausted)
```

See [What gets captured](#what-gets-captured) for the full eligibility matrix and the
local-then-transport delivery contract in detail.

## Setup

Add the `Conduit.Durability` package and configure inside `AddConduit`. The persistence engine is registered once on the `ConduitBuilder` (shared with the inbox, DLQ, audit trail, and jobs), then `AddOutbox()` enables the outbox on it:

```xml
<PackageReference Include="Conduit.Durability" />
<PackageReference Include="Conduit.Persistence.Sqlite" />
```

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.UseSqlite("Data Source=myapp.db");
    conduit.AddDurability(durability =>
    {
        durability.AddOutbox();
    });
});
```

The `AddOutbox()` call registers:
- `OutboxCapture` as both `IOutboundInterceptor` and `IOutboxFlusher` (scoped)
- `IOutboxPendingWriter` (scoped) — see [Publishing after the pipeline](#publishing-after-the-pipeline)
- `OutboxBehavior<,>` as a pipeline behavior
- `OutboxRelay` as a background service
- `DbConnectionOutboxStore` with the chosen dialect

### Capture-only

Set `options.CaptureOnly = true` for a process that shares its outbox table with another process whose relay owns delivery. A second relay could claim a row whose listener lives only in the other process and mark it sent with nobody having handled it. A capture-only outbox registers `OutboxCapture` (as `IOutboundInterceptor`) and `OutboxBehavior<,>`, so rows are still written on the caller's transaction, but it registers **no** `OutboxRelay` and its `IOutboxFlusher` is the no-op `CaptureOnlyOutboxFlusher`: nothing is delivered, flushed, or cleaned up by this process. Messages published with no ambient transaction are not captured and still deliver locally, exactly as without an outbox.

## Providing a transaction

A transaction-aware connection factory is **mandatory** for the outbox. The outbox behavior writes entries using the transaction returned by `IConduitConnectionFactory.GetCurrentTransaction()` so that the business data and the outbox entry commit atomically.

The connection factory supplied by the engine package (registered by `conduit.UseSqlite()`, `conduit.UseSqlServer()`, etc.) is non-transactional: it always returns `null` from `GetCurrentTransaction()` — it only opens fresh connections for the relay and store sweeps and has no visibility into the handler's transaction. If outbox capture ran against it, it would silently persist **nothing** — the outbox would appear configured while protecting nothing.

To prevent that, **enabling `AddOutbox()` while the engine's non-transactional factory is the active `IConduitConnectionFactory` fails fast at startup** with an `InvalidOperationException` explaining that a transaction-aware factory is required. Register your own factory that tracks the handler's active transaction:

```csharp
public class AppDurabilityConnectionFactory(AppDbContext dbContext) : IConduitConnectionFactory
{
    public Task<DbConnection> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("Data Source=myapp.db");
        connection.Open();
        return Task.FromResult<DbConnection>(connection);
    }

    public DbTransaction? GetCurrentTransaction()
        => dbContext.Database.CurrentTransaction?.GetDbTransaction();
}
```

Register it after `AddConduit` so it overrides the built-in factory:

```csharp
builder.Services.AddScoped<IConduitConnectionFactory, AppDurabilityConnectionFactory>();
```

## Database dialects

Each `Use*` method registers the appropriate SQL dialect and connection factory. Claiming is **atomic**: a single `UPDATE` transitions pending rows to a `Claiming` status tagged with a per-worker token, so a row can be claimed (and dispatched) by exactly one worker — the relay and the immediate-send flush can never double-send. A claim that is not resolved within 5 minutes (e.g. a crashed worker) becomes re-claimable. The per-engine locking applied inside the claim:

| Method | Database | Claiming mechanism |
|---|---|---|
| `conduit.UseSqlite(connectionString)` | SQLite | Single-writer (atomic `UPDATE` + read-back by token) |
| `conduit.UseSqlServer(connectionString)` | SQL Server | `WITH (ROWLOCK, UPDLOCK, READPAST)` in the claim subquery, after `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` in the same batch: `READPAST` refuses Snapshot and Serializable (SQL 650), and a pooled session keeps the level its last transaction set (measured on SQL Server 2022 with Microsoft.Data.SqlClient 7.1.1, `SqlServerPooledSessionShould`) |
| `conduit.UsePostgres(connectionString)` | PostgreSQL | `FOR UPDATE SKIP LOCKED` in the claim subquery |
| `conduit.UseMySql(connectionString)` | MySQL 8.0+ | `FOR UPDATE SKIP LOCKED` in the claim subquery |

The outbox table carries two columns supporting this: `ClaimedAt` and `ClaimToken`.

These `Use*` methods are registered once on the `ConduitBuilder` (the engine is shared by the outbox, inbox, dead letter queue, audit trail, and jobs). Each engine ships in its own `Conduit.Persistence.*` package that carries the ADO.NET driver, so no generic connection-type parameter is needed:

```csharp
// SQL Server (Conduit.Persistence.SqlServer package)
conduit.UseSqlServer("Server=.;Database=MyApp;...");

// PostgreSQL (Conduit.Persistence.Postgres package)
conduit.UsePostgres("Host=localhost;Database=MyApp;...");

// MySQL (Conduit.Persistence.MySql package)
conduit.UseMySql("Server=localhost;Database=MyApp;...");

conduit.AddDurability(durability => durability.AddOutbox());
```

The outbox table (`conduit_outbox`) is created automatically on startup. Disable with `AddOutbox(o => o.AutoCreateTable = false)` if you manage schemas through migrations. A new table includes the nullable `Properties` column (`NVARCHAR(MAX)` on SQL Server, `TEXT` on SQLite and PostgreSQL, `LONGTEXT` on MySQL); an existing table is never altered, so a host that [carries dispatch properties](#carrying-dispatch-properties) on a table created before the column existed must add it itself.

## Custom stores

For non-relational databases (Cosmos DB, MongoDB, DynamoDB), implement `IOutboxStore` directly:

```csharp
public class CosmosOutboxStore : IOutboxStore
{
    // Implement SaveAsync, ClaimPendingAsync, ClaimByMessageIdsAsync, MarkSentAsync,
    // IncrementRetryAsync, MarkFailedAsync, DeleteCompletedAsync, DeleteFailedAsync, EnsureTableExistsAsync
}
```

Register it as `IOutboxStore` after `AddConduit` so it overrides the built-in `DbConnectionOutboxStore`:

```csharp
builder.Services.AddScoped<IOutboxStore, CosmosOutboxStore>();
```

## Relay configuration

```csharp
conduit.UseSqlite("Data Source=myapp.db");
conduit.AddDurability(durability =>
{
    durability.AddOutbox(outbox =>
    {
        outbox.RelayInterval = TimeSpan.FromSeconds(5);        // polling interval (default: 5s)
        outbox.GracePeriod = TimeSpan.FromSeconds(10);         // ignore entries newer than this (default: 10s)
        outbox.BatchSize = 100;                                // entries per sweep (default: 100)
        outbox.MaxRetries = 10;                                // before marking failed (default: 10)
        outbox.AutoCreateTable = true;                         // create table on startup (default: true)
        outbox.CompletedRetention = TimeSpan.FromDays(7);       // delete Sent entries older than this (default: 7d); Zero disables
        outbox.FailedRetention = null;                          // delete Failed entries older than this (default: null = follow CompletedRetention); Zero keeps them forever
        outbox.CleanupInterval = TimeSpan.FromHours(1);         // how often cleanup runs (default: 1h)
        outbox.PersistedProperties["UserId"] = typeof(int);     // dispatch properties an entry carries (default: none)
    });
});
```

The grace period prevents the relay from racing with the handler. After the transaction commits, the entry is in the database — but the relay ignores entries newer than the grace period, giving the handler time to complete any post-commit work.

### Retention

The relay periodically deletes its own history every `CleanupInterval`, checked on the same sweep
loop that processes pending entries. `Sent` entries older than `CompletedRetention` are removed;
`Failed` entries (retries exhausted) older than `FailedRetention` are removed. `FailedRetention`
defaults to `null`, which follows `CompletedRetention`. Each retention applies only when it is above
`TimeSpan.Zero`: `CompletedRetention = TimeSpan.Zero` stops Sent cleanup (and, with the default
`FailedRetention`, Failed cleanup too), so entries accumulate indefinitely, which you may prefer if
you query the outbox table for audit or observability purposes. `FailedRetention = TimeSpan.Zero`
keeps Failed rows forever while Sent rows are still cleaned: a Failed row is the only copy of a side
effect that never ran. Cleanup resolves the store from its own fresh DI scope, same as a sweep.

### Alerting on exhausted retries

When an entry exhausts `MaxRetries` the relay marks it Failed, logs at Error with
`EventId` **320** (`OutboxRetriesExhausted`; type, message id and entry id are structured fields),
and increments the `conduit.outbox.retries_exhausted` counter on the `Conduit` meter (tag
`conduit.message.type`). Both are stable: alert on either. The relay records an untagged zero on the counter
when it starts, so the series exists from its first export rather than from the first failure (an
OpenTelemetry counter exports nothing until it is recorded); a real exhaustion is a separate, tagged series.

## What gets captured

`IOutboundInterceptor.Intercept(envelope)` returns `bool`, not `void`. The generated mediator
offers each capturable envelope to it *before* any local dispatch or transport send, and the
return value decides who owns delivery:

- **`true` — captured.** An ambient database transaction was active
  (`IConduitConnectionFactory.GetCurrentTransaction()` is non-`null`). `OutboxCapture` buffers the
  envelope in memory; `OutboxBehavior` persists it to the outbox table in that same transaction
  once the handler returns. The caller — the generated mediator — skips local dispatch and the
  transport send entirely (events only — routed commands have no local listener chain); both happen later, from the outbox, once the transaction has actually
  committed.
- **`false` — declined.** No ambient transaction was active. A captured-but-unpersisted envelope
  would be a silently dropped message, so `Intercept` refuses it instead: the caller proceeds
  exactly as if no interceptor were registered at all (the pre-outbox behavior).

This is the **no-transaction decline rule**: the outbox never buffers what it cannot durably
persist. It's also why `AddOutbox()` fails fast at startup without a transaction-aware connection
factory (see [Providing a transaction](#providing-a-transaction)) — without one, every `Intercept`
call would decline, and the outbox would silently protect nothing.

### Capture-eligibility matrix

| Kind | At publish | On flush/relay |
|---|---|---|
| `[FireAndForget]` non-Immediate (incl. `[LocalOnly]`) | captured (with ambient txn) → skip local enqueue + broadcast; declined → delivered right away, exactly as without an outbox | local listeners (fresh scope) → transports (skipped if `[LocalOnly]`) → mark sent |
| `[FireAndForget(Immediate = true)]` | never captured — immediate local enqueue + direct broadcast (events only; routed commands skip straight to transport, no local listener chain), at-most-once by declaration (for messages that must survive their command's rollback — the write-then-throw analog) | never stored |
| plain event | local listeners inline (inside the handler's transaction); transport envelope captured with txn, else direct send | transports only (local listeners already ran inline — no re-run) |
| routed `[FireAndForget]` void command | captured with txn unless Immediate | its endpoint's transport, sent as at publish time (`SendEventAsync`, no reply awaited) |

The declined case ("delivered right away, exactly as without an outbox") and the `Immediate`
row's publish-time behavior are the *same* path (background-enqueued local listeners, direct
broadcast) — they just arrive there for
different reasons: one because no transaction was active to capture into, the other because it
was never offered to `Intercept` at all. See [The Immediate escape hatch](#the-immediate-escape-hatch)
below. A plain event that is *also* `[LocalOnly]` never reaches the transport-envelope step in the
table above — there's no broadcast to defer, so nothing is ever offered to `Intercept` for it
either; only its local listeners run, inline, exactly like any other plain event's.

Never eligible for capture, at any point — these all expect a reply, so dispatch can't be deferred:

| Message type | Why not |
|---|---|
| Commands with results (`ICommand<TResult>`) | Caller awaits the response |
| Queries (`IQuery<TResult>`) | Caller awaits the response |
| Non-`[FireAndForget]` void commands (`ICommand`) | Uses request/response to confirm handler execution |
| Local (unrouted) `[FireAndForget]` void commands | No transport is involved — nothing to capture; always dispatched via `ConduitBackgroundDispatcher`, per [dispatch flows](dispatch-flows.md#fire-and-forget-void-commands) |

### Flush and relay delivery: local-then-transport

For every entry a captured envelope produced, the delivery `IOutboxFlusher.FlushAsync` schedules
and `OutboxRelay` apply the identical per-entry delivery contract:

1. Resolve `ILocalEnvelopeDispatcher` from a **fresh DI scope** — one scope per entry, mirroring
   fire-and-forget's own fresh-scope semantics (the scope that captured the envelope is long gone
   by the time flush or the relay runs).
2. If the entry's type is one whose local listeners the outbox owns (`DeliversLocally(type)` —
   `true` only for captured, non-Immediate `[FireAndForget]` events), run its local listener chain
   (with pipeline behaviors) against that fresh scope.
3. Unless the type is `[LocalOnly]` (`IsLocalOnly(type)`), send the envelope via the appropriate
   transport, routed exactly as it would have been at publish time. A routed `[FireAndForget]` void
   command (`IsFireAndForget(type)`) goes to its endpoint with `SendEventAsync`, as the publish-time
   path sends it: no reply is awaited, so it neither holds the relay until the transport's response
   timeout nor counts a delivered message as failed.
4. Mark the entry `Sent`.

Plain events never reach step 2 for their *local* listeners — `DeliversLocally` is `false` for
them, because they already ran inline, inside the handler's own transaction, at publish time.
Re-running them on flush/relay would double-deliver. Only their transport envelope (when captured)
is subject to this contract.

Step 3's transport send for an event goes through the same `BrokerTransportBase` path as any other
publish, so it carries the same publishing-application stamp and is subject to the same
[self-published event filter](transports/index.md#self-published-events) on receipt. That matters
here specifically because step 2 can also run local listeners (for a captured `[FireAndForget]`
event) before step 3 broadcasts — without the filter, a broker routing that broadcast back to this
application would run the same listeners a second time, exactly as it would for a plain event
published outside the outbox.

**Step 2 is a dispatch of its own.** It runs in a child of the entry's message context, so inside a
local listener `MessageContext.Current.MessageId` is a fresh id for that delivery attempt,
`CausationId` is the outbox entry's `MessageId`, `ConversationId` is the entry's, and
`DeliveredMessageId` is the entry's `MessageId` on every attempt. With [audit](audit.md) on, each
attempt is one audit record — kind `Event`, no payload (the publish record holds the body), failed or
completed — and a listener's nested dispatches are caused by it. A listener's log line names the
attempt's `MessageId`; the attempt's audit record joins it to the entry through its `CausationId`.
Step 3's transport send runs back in the entry's own context, so remote hosts receive the entry's
`MessageId`.

**Redelivery re-runs local listeners.** If a scheduled flush delivery fails partway, or never runs
at all (e.g. a crash between commit and the background dispatcher draining the work item), the
relay claims the same entry later and repeats the full contract — including local listeners.
Outbox delivery is **at-least-once**, exactly like transport delivery; local listeners must be
idempotent for the same reason transport-side consumers must be. Key once-per-message work on
`MessageContext.Current.DeliveredMessageId`, which a redelivery repeats, not on `MessageId`, which
each attempt mints afresh — a [Conduit.Jobs event trigger](jobs.md#event-triggers) keys its run that
way, so one entry delivered twice runs its job once.

### Carrying dispatch properties

By default an entry carries only its correlation ids, so step 2 runs under a context with an empty
`MessageContext.Current.Properties`: whatever the publishing dispatch held — the user a transport
enricher stamped, say — is gone, and a listener (with every dispatch it makes) runs as nobody. A host
opts in, key by key, with the type each value has:

```csharp
durability.AddOutbox(outbox =>
{
    outbox.PersistedProperties["UserId"] = typeof(int);
    outbox.PersistedProperties["DeviceId"] = typeof(Guid);
});
```

- **What is written.** When an entry is written, the publishing dispatch's values for the listed keys
  are stored in its `Properties` column as a JSON object (`{"UserId":42,"DeviceId":"…"}`). A listed key
  that is absent, or whose value is `null`, is left out; with none present the column is `NULL`. A
  value of another type than its key declares fails the write with an `InvalidOperationException`.
- **What is restored.** The flush and the relay both start the entry's context with those values,
  each restored as its declared type, so step 2's listeners — and their nested dispatches, which
  inherit the bag — see them. A relay redelivery after a failed flush runs under exactly the bag the
  flush did. An entry with a `NULL` column (one written before the host opted in) is delivered with an
  empty bag, as before. A stored key the host no longer lists, or whose value no longer reads as its
  declared type, is dropped rather than failing the delivery, and a column that is not a JSON object
  (truncated, or edited by hand) restores as an empty bag.
- **List only keys the server sets.** A restored value is trusted by every listener and nested
  dispatch, so a listed key must come from an `IDispatchContextEnricher` that removes any client-sent
  value before it stamps its own. A transport seeds a context from the caller's headers, and with no
  enricher registered `DispatchContextEnrichment.Apply` hands those headers on unchanged: listing such
  a key would let a caller choose who a listener runs as.
- **Supported types** are `string`, `int`, `long`, `bool` and `Guid`. `AddOutbox` refuses any other,
  and an empty key, with an `OptionsValidationException` at startup.
- **The column.** A host that lists no key never writes or reads `Properties`, so its table may predate
  the column. A host that lists one needs the column: `AutoCreateTable` adds it only when it creates the
  table, so with an existing table — or `AutoCreateTable = false` — add it by migration (on SQL Server,
  `ALTER TABLE conduit_outbox ADD Properties NVARCHAR(MAX) NULL`).

**Degraded mode without the source generator.** `ILocalEnvelopeDispatcher` is implemented by the
generated mediator and resolved with `GetService` (not `GetRequiredService`). A host that never
ran `Conduit.SourceGen` — so no generated mediator is registered — resolves `null` there; flush and
the relay then skip step 2 unconditionally and always attempt the transport send. The outbox
degrades to transport-only delivery rather than failing.

## The Immediate escape hatch

Not to be confused with **[Immediate send](#immediate-send)** below (`IOutboxFlusher.FlushAsync`,
an optimization that shortens *outbox* latency, unrelated to this). This section is about
`[FireAndForget(Immediate = true)]` — a flag on the attribute itself that opts a specific event or
routed void command **out of outbox capture entirely**. It is never offered to
`IOutboundInterceptor.Intercept`, transaction or no transaction:

```csharp
[FireAndForget(Immediate = true)]
public record AccountLockedEvent(Guid AccountId, string Reason) : IEvent;
```

An `Immediate` event always takes the pre-outbox path: local listeners run through
`ConduitBackgroundDispatcher` (a fresh scope, after the publishing scope is gone) and the event
broadcasts to transports directly — the same path a non-Immediate `[FireAndForget]` event takes
when capture is declined for lack of a transaction. A routed `Immediate` void command takes the
analogous direct path for commands: straight to `transport.SendEventAsync`, skipping the
interceptor check entirely (there's no local listener chain for a command, only a handler). Either
way, the difference from the non-Immediate case is *when*: `Immediate` takes its direct path
unconditionally, including while a transaction **is** active, so delivery never waits for — or
depends on — that transaction's outcome.

### Why you'd want that

The outbox's guarantee is at-least-once delivery *of things that got committed*: if the ambient
transaction rolls back, a captured envelope rolls back with it — nothing was ever persisted to
send. That's correct for ordinary business events; an `OrderPlaced` event should never outlive the
order it describes. It's wrong for a narrow class of messages that must survive the rollback: a
security-relevant notification — a failed-login lockout, say — raised by a handler that then
throws to abort its own transaction. The message isn't reporting a committed fact; it's reporting
that the attempt happened, independent of whether the surrounding write succeeds. This is the same
shape as a write-then-throw side effect that deliberately runs outside its enclosing transaction —
`Immediate` is that pattern applied to messages instead of writes.

### At-most-once, by declaration

`Immediate` messages are never persisted, so they carry none of the outbox's durability: no retry,
no relay, no surviving a process crash between the background dispatcher's enqueue and its
execution. Choosing `Immediate` is choosing at-most-once delivery for that message type, in
exchange for guaranteed rollback-survival and zero outbox latency. Make that trade-off
deliberately, per type — it is not a global default, and nothing gets it unless you write
`Immediate = true` yourself.

## Publishing after the pipeline

`OutboxBehavior` writes captured envelopes when a pipeline ends — every pipeline, nested inline-event
pipelines included, each envelope once. A host's own transactional behavior that publishes *after*
its `next()` returns — for example, dispatching domain events just before the commit — publishes
after every `OutboxBehavior` in the scope has already exited. Those publishes are captured (the
transaction is still open), but nothing writes them. Write them yourself with `IOutboxPendingWriter`,
after the last publish and before the commit:

```csharp
public sealed class TransactionalBehavior<TRequest, TResult>(
    AppDbContext dbContext,
    DomainEventDispatcher domainEvents,
    IOutboxPendingWriter outboxWriter,
    IOutboxFlusher flusher) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var result = await next();
        await domainEvents.DispatchPendingAsync(cancellationToken);   // publishes are captured here
        await outboxWriter.WritePendingAsync(cancellationToken);      // ...and written here

        await transaction.CommitAsync(cancellationToken);
        await flusher.FlushAsync(cancellationToken);
        return result;
    }
}
```

`WritePendingAsync` writes the same entries `OutboxBehavior` would (it is the same code), adds them
to what `FlushAsync` delivers, and is a no-op when nothing is unwritten or no transaction is active.
It is registered in capture-only mode too — writing is not delivery.

When a pipeline faults, `OutboxBehavior` drops the unwritten envelopes that pipeline captured, so a
later command in the same DI scope cannot write a failed command's messages into its own
transaction. Envelopes your behavior captures after `next()` are outside every pipeline: if it
faults before `WritePendingAsync`, they stay unwritten in the scope — don't dispatch another command
in that scope after a failure.

## Immediate send

By default, outbox messages wait for the relay's next poll cycle before reaching the broker. The immediate send optimization bypasses this delay: `FlushAsync` schedules delivery from the in-memory buffer on the background dispatcher right after the transaction commits, instead of waiting for the relay's next poll — using the relay only as a safety net for failures. `FlushAsync` itself returns as soon as delivery is scheduled; it does not wait for delivery to complete, so the caller is never held up by listener or transport latency.

### Usage

Inject `IOutboxFlusher` and call `FlushAsync` after committing your transaction:

```csharp
public class PlaceOrderHandler(
    AppDbContext dbContext,
    IMediator mediator,
    IOutboxFlusher flusher) : ICommandHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var order = new Order(command.Product);
        dbContext.Orders.Add(order);

        await mediator.PublishAsync(new OrderPlacedEvent(order.Id, command.Product), cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await flusher.FlushAsync(cancellationToken);

        return order.Id;
    }
}
```

`await flusher.FlushAsync(cancellationToken)` here returns once delivery is scheduled, not once it
has happened — `HandleAsync` returns to its caller without waiting for `OrderPlacedEvent` to reach
any listener or transport.

### Behavior

`FlushAsync` schedules delivery of the outbox entries written during the current scope on `ConduitBackgroundDispatcher` and returns immediately — the caller never waits for delivery, only for its own commit. The scheduled work item runs under the dispatcher's own lifetime, not the caller's request: it claims the entries using the same atomic claim as the relay (`ClaimByMessageIds`, which transitions each row to `Claiming` under a per-worker token), then sends each one via the appropriate transport using the in-memory envelope — no database read needed. Successfully delivered entries are marked as `Sent`. If a transport fails, the entry's retry count is incremented and the claim is released so the relay picks it up on its next sweep.

Because delivery is scheduled rather than synchronous, `FlushAsync` returning is not evidence that listeners or transports have run — there is no read-your-writes guarantee. `FlushAsync` is a no-op (nothing is scheduled) when nothing was captured during the scope, and safe to call unconditionally. If the relay has already claimed an entry by the time the scheduled delivery runs, the claim returns nothing and it completes without sending duplicates.

### EF Core auto-flush

For EF Core applications, a `SaveChangesInterceptor` can call `FlushAsync` automatically after every successful save. Because `FlushAsync` only schedules delivery, the interceptor itself still completes quickly:

```csharp
public class OutboxFlushInterceptor(IOutboxFlusher flusher) : SaveChangesInterceptor
{
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await flusher.FlushAsync(cancellationToken);
        return result;
    }
}
```

Register it:

```csharp
builder.Services.AddScoped<OutboxFlushInterceptor>();

builder.Services.AddDbContext<AppDbContext>((serviceProvider, options) =>
{
    options.AddInterceptors(serviceProvider.GetRequiredService<OutboxFlushInterceptor>());
});
```

## Limitations

**At-least-once, not exactly-once.** If the relay sends a message but fails to mark it as sent (e.g., database connection blip), it will send again — and if the entry has captured local listeners, they run again too, since flush/relay redeliver local-then-transport as one unit (see [Flush and relay delivery](#flush-and-relay-delivery-local-then-transport)). Consumers and local listeners alike must handle duplicates. The [inbox](inbox.md) — also part of `Conduit.Durability`, enabled via `durability.AddInbox()` — provides application-level deduplication for *wire-inbound* messages; there is no equivalent for local-listener redelivery, so those listeners must be naturally idempotent.

**No ordering guarantees.** If message A fails and B succeeds, consumers see B first. For ordered delivery, use broker-native features (ASB sessions) or handle ordering at the consumer.

**Relay runs in-process.** The relay is a `BackgroundService` in the same process. If the process is down, pending entries wait until it restarts. Multiple instances of the same app each run a relay — atomic claiming (an `UPDATE` to a `Claiming` status tagged with a per-worker token) ensures each entry is claimed and sent by exactly one worker, on every supported engine including SQLite. The relay and the immediate-send `FlushAsync` likewise never double-send the same entry.

**Immediate send is best-effort, and asynchronous.** `FlushAsync` schedules delivery on the background dispatcher and returns immediately — it does not wait for delivery, so a caller cannot assume listeners or transports have run by the time `FlushAsync` returns. The relay is still the safety net: if the process crashes between commit and the scheduled delivery running, or delivery itself fails, the relay delivers the messages on its next sweep. This means messages may occasionally arrive with relay-scale latency even when immediate send is configured.
