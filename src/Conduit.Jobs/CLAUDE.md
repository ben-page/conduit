# Conduit.Jobs

Background jobs — code-declared recurring schedules and event triggers, both executing as ordinary
mediator commands. Configured via `conduit.AddJobs()`. User docs: [jobs](../../docs/jobs.md).

## Persistence engine (Conduit.Persistence)

Same arrangement as Durability and Audit: `Conduit.Jobs` depends on `Conduit.Persistence` only and
has **no** direct database-driver dependency. The engine is resolved from DI —
`IConduitConnectionFactory` + `ISqlDialect` — and is registered on the **`ConduitBuilder`**
(`conduit.UseSqlite(cs)` / `UsePostgres` / `UseSqlServer` / `UseMySql`).

`DbJobStore` extends `DbStoreBase`; `JobTableInitializer` extends `TableInitializerBase`;
`JobCleanup` extends `PeriodicCleanupServiceBase`.

**Ordering is load-bearing.** `AddJobs` inspects `builder.Services` for the engine to decide durable
vs non-durable, so `UseSqlite(...)` must be called **before** `AddJobs(...)`. Getting it wrong is
caught at startup: `JobTableInitializer.ValidateConfiguration` throws when an engine is registered
but `IJobStore` is not, naming the fix.

## Directory layout

- `Configuration/` — `JobsBuilder`, `JobsOptions`, `ScheduleBuilder`, `EventTriggerBuilder<TEvent>`, `CoalescingTriggerBuilder<TEvent>` and `CoalescingTriggerBuilder<TEvent, TCommand>`, `MisfirePolicy`
- `Runtime/` — `JobSchedule`, `JobTrigger`, `ScheduleRegistry`, `DurableJobScheduler`, `InMemoryJobScheduler`, `ClaimedJobExecutor`, `JobRunner`, `ClaimHeartbeat`, `EventTriggerListener<TEvent>`, `ScopeCoalescer`, `JobCleanup`, `DurableJobs`, `InMemoryJobs`, `InMemoryJobState`, `JobSchedulerReadiness`, `JobOutcome`, `JobAttempt`
- `Persistence/` — `IJobStore`, `DbJobStore`, `JobTableSchema` (`conduit_job_schedules`, `conduit_job_runs`, `conduit_job_scopes`), `JobRunsTableSchema`, `JobRun`, `JobRunStatus`, `JobScheduleRow`, `JobScope`, `HeartbeatResult`
- root — `ConduitBuilderExtensions` (`AddJobs`), `JobTableInitializer`, `IJobs`, `JobScheduleInfo`, `JobRunInfo`, `JobScopeInfo`, `JobFilter`, `CancellationOutcome`

## Time

**Nothing in this project may read the ambient clock.** Every scheduling decision goes through the
injected `TimeProvider` (`TryAddSingleton(TimeProvider.System)` in `AddJobs`), which is what makes
occurrence computation, misfire detection, backoff and `MaxRuntime` deterministically testable with
`FakeTimeProvider`. `grep -rn "DateTime.UtcNow\|DateTimeOffset.UtcNow" src/Conduit.Jobs/` must
return nothing.

Both schedulers read `TimeProvider.GetUtcNow()` in their **constructor** and anchor a schedule's
first occurrence to that, not to whenever the loop's first database read returns. Hosted services
are all constructed before any of them starts, so this is stable even when startup is slow.

## Registered services

`AddJobs` always registers `ScheduleRegistry`, `IOptions<JobsOptions>`, `TimeProvider` (TryAdd),
`JobSchedulerReadiness`, and `JobTableInitializer` **first** (hosted services start in registration
order and both schedulers read the tables on their first pass). Then, depending on the engine:

| Durable | Non-durable |
|---|---|
| `IJobStore` → `DbJobStore` (scoped) | — |
| `IJobs` → `DurableJobs` (scoped) | `IJobs` → `InMemoryJobs` (singleton) |
| `ScopeCoalescer` (scoped) | — |
| `JobRunner`, `ClaimedJobExecutor` (singletons) | `InMemoryJobState` (singleton) |
| `DurableJobScheduler`, `JobCleanup` (hosted) | `InMemoryJobScheduler` (hosted) |
| One closed `EventTriggerListener<T>` per declared trigger's event type | — |

## Claim reuse

`conduit_job_runs` implements `IClaimableTableSchema` (renamed from `IOutboxTableSchema` for this)
with `CreatedAtColumn` mapped to **`RunAt`**. The eligibility comparison against `@GraceCutoff` then
reads as "this job is due", so `ISqlDialect.BuildClaimPendingSql` becomes the jobs claim with **zero
new per-engine SQL** — and the Postgres/MySQL `FOR UPDATE SKIP LOCKED` and SQL Server
`ROWLOCK/UPDLOCK/READPAST` variants come along free. Do not add a literal `CreatedAt` column; the
mapping is the whole trick.

`BuildInsertOrIgnoreSql`'s third argument is a **conflict target**, not necessarily the primary key.
The runs table's PK is the auto-increment `Id` (claims order by it) while idempotent materialization
conflicts on the unique `JobKey`, so `JobKey` is what gets passed. That requires the unique
constraint on `JobKey` — declared inline in the CREATE TABLE, because `CREATE UNIQUE INDEX IF NOT
EXISTS` is unsupported on MySQL and SQL Server.

**`claimToken` must be unique per `ClaimDueAsync` call.** Claimed rows are read back by token, so
reusing one while an earlier claim is still in flight would hand the caller the earlier claim's rows
too. `DurableJobScheduler` generates a fresh GUID per sweep.

**Coalescing adds no new per-engine SQL either, and does not touch `BuildClaimPendingSql`.**
`ScopeCoalescer.PickUpAsync` writes the run with the existing `TryMaterializeAsync` (or
`InsertClaimedAsync` on the receiving host), so hosts collapse on the existing insert-or-ignore and
the existing `ClaimDueAsync` elects the runner. The scopes table itself is composed from the same
`ISqlDialect` type vocabulary as the other two, and `git diff main -- src/Conduit.Persistence*` over
this work is empty by design.

## Rejected alternatives

Recorded because each is appealing enough to be proposed again.

**Inbox dedup instead of row claiming.** `IInboundInterceptor` gates run only on the wire-inbound
path, so a locally-dispatched scheduled command never passes one — the common case would get no
protection at all. It would also need a deterministic `MessageId`, which no API exposes
(`DispatchOptions` carries only properties; `MessageContext.StartDispatch` mints a fresh GUID), and
`InboxOptions.RetentionPeriod` defaults to 30 minutes, so dedup would silently degrade under clock
skew exceeding retention.

**Leader election instead of row claiming.** No lease primitive exists in the repo, and the fencing
gap is real: a leader that has GC-paused past its lease still believes it leads while a new leader
also fires. Raft closes that with a term number, but Raft's guarantee covers the replicated log, and
firing a job is a side effect *outside* the log — a partitioned old leader can still dispatch.
Making it safe means carrying the term into a conditional write at the point of the side effect,
which is a row claim wearing a hat. It would also change Conduit's deployment contract permanently:
today every instance talks to a broker and/or a database and **never to another instance**; Raft
adds peer discovery, intra-cluster ports, an fsync'd log, and quorum sizing. And leader election
loses the part that matters most — if the host running the 02:00 rollup dies mid-job, a claim
becomes re-claimable after `ClaimTimeout` and another replica takes it, where a leader simply loses
the occurrence.

The decisive framing: a database is already a hard dependency for durable jobs, so using it for
coordination is free. Raft would add a *second* coordination substrate alongside one we must have
anyway.

**An event-based cancellation signal instead of the heartbeat.** Publishing a
`JobCancellationRequested` event would be lower-latency but does not work with Conduit's topology.
The per-app event queue is competing-consumer (`TopologyNaming.CreateAppResourceName` is per
*application*, shared by every replica), so a broadcast reaches exactly **one** replica — almost
certainly not the one holding the claim — and the self-published-event filter would discard even
that delivery, since the publishing application's own stamp matches. Fanning out to every replica
needs per-instance topology Conduit does not have, and would work only with a broker configured,
while durable jobs are perfectly viable with a database and none.

**The scope version in the run key** (`{ScopeKey}@{Version}`) — the design this implementation
started from. It looks like it makes pickup idempotent, and it does, but a scope's version restarts
at 0 for every new generation, so a version-suffixed key does *not* collide with the run already in
flight for that key. A fresh scope opened at `Version = 0` while a run was in flight either collided
with it and had its work dropped, or — with the delete made conditional — a burst of ten events
during one rebuild started four concurrent rebuilds. Keying on the scope key alone gives the
idempotence *and* the mutex.

**Detach the `JobKey` on claim** — make the coalescing key be the `JobKey` and rewrite it to
something unique in the transaction that claims the run, freeing the slot the moment the scope
becomes fixed. There is no such transaction: `ClaimDueAsync` is three statements (the shared claim
`UPDATE`, the jobs-owned `StartedAt` `UPDATE`, the read-back `SELECT`), so a detach in the second
leaves a window where the row is `Claiming` but still holds the key, and an insert-or-ignore landing
in it is silently swallowed. A retry rides that out; a host *dying* in the window strands the
coalescing slot until `ClaimTimeout`, which is the wrong failure shape for a hot path. Closing the
window properly means folding the detach into `BuildClaimPendingSql` on all four engines, forfeiting
the zero-new-per-engine-SQL property outright.

**A generation counter in the key** (`{trigger}@{coalesceKey}@{generation}`, incremented when the
previous run is claimed). It avoids the claim path, but needs the current generation read before
every materialization — a round trip on the hot path, and a race of its own about who increments.
The scope row keeps the good half of the idea: its version lives on a row whose only job is to hold
it, so reading it is the same round trip that reads the payload to merge.

**Imperative one-shot scheduling** (`ScheduleAsync(command, runAt)`) and the handle/cancellation API
it implies. Declined deliberately; a delayed *event trigger* covers the declarative version of the
same need and reuses the run row's existing `RunAt` column and claim path.

If jobs ever needs a wire header, note that Amazon SQS request publishes already use 9 of the
SNS/SQS ceiling of 10 message attributes — there is one attribute of headroom.

## Non-obvious decisions

- **Two tables.** The runs table is deleted on success, so it cannot answer "did we already run
  02:00 today?". The cursor in `conduit_job_schedules` does. `CompleteAsync` and `MarkTerminalAsync`
  both delete/update **and** advance the cursor in one transaction — a crash between two separate
  writes leaves an occurrence that has run and looks like it has not.
- **`Occurrence` is separate from `RunAt`.** A retry moves `RunAt` to the backoff time; the cursor
  must still advance to the occurrence.
- **`ScheduleName` holds a schedule *or* trigger name.** `AddJobs` rejects a collision between the
  two, because a trigger's in-flight run would otherwise be mistaken for a schedule's and suppress
  its next occurrence.
- **The heartbeat keeps renewing after it decides to cancel.** Cancellation is cooperative, so the
  handler may keep running; stopping the renewal there lets the claim go stale and a second host
  runs the job while the first is still winding down.
- **`ReleaseAsync` is conditional on the claim token**, so a drain never hands a claim back out from
  under a host that has since taken it.
- **`ClaimedJobExecutor` is shared** by the scheduler and by event triggers. They reach a claimed run
  from different directions but owe the ledger identical bookkeeping. Neither blocks on the job: the
  scheduler would delay the next occurrence and the next sweep, and a trigger would hold the
  publishing dispatch open for the job's whole duration.
- **`JobRunner` rehydrates from the payload** rather than re-invoking the declaration, because the
  claiming host is not necessarily the one that wrote the row — a crashed host's occurrence is
  recovered elsewhere, and a delayed trigger no longer has its event instance. Type resolution goes
  through `ConduitTypeRegistry.CommandTypes`.
- **`JobSchedulerReadiness`** completes once every loop has armed its first wait. Under a real clock
  the gap between `StartAsync` returning and a timer existing is invisible; under a test's
  discontinuous clock it means advancing past an occurrence nothing was waiting for.
- **The misfire walk is capped** at 100,000 occurrences; beyond that the cursor moves to now with a
  warning rather than stalling startup.
- **Purge covers `Cancelled` as well as `Failed`.** Both are terminal history kept only for an
  operator to look at, and neither is ever retried, so leaving cancelled rows grows the ledger
  without bound.
- **A scope's run key is the scope key, with no version suffix.** `JobKey` is unique, so the coalesce
  key doubles as a per-key mutex: a refused insert means "a run for this key is already outstanding",
  which is what turns "one in flight, one accumulating" into a guarantee. It is also what makes a
  repeated pickup after a crash collapse instead of running twice.
- **`ScopeKey` is one column, not a composite PK.** `BuildInsertOrIgnoreSql` takes a single conflict
  identifier and Postgres/SQL Server bake it in as one; a composite key is not expressible through
  it.
- **`Version` does exactly two jobs**, and being part of the run key is not one of them: it is the
  compare-and-set on merge, and it makes the delete at pickup conditional so a merge landing between
  materialize and delete survives as the next scope.
- **`ScopeId` exists because `Version` resets to 0** on every new generation of a scope row. A guard
  on `(ScopeKey, Version)` alone could match the row that *replaced* its target and clobber it —
  narrowing the scope, which is the failure the guard exists to prevent.
- **Pickup supersedes a terminal run holding its key.** Only `CompleteAsync` deletes on success, so a
  `Failed` or `Cancelled` row sits in the ledger for `FailedRetention`; since the run key is the
  coalesce key, that would freeze every later scope for the key. `TryClearTerminalRunAsync` deletes
  it (guarded on the same status pair `PurgeTerminalAsync` targets, so live work is never touched)
  and logs at Warning. The consequence is deliberate: a permanently failing coalescing job retries
  whenever new events arrive rather than stopping at `MaxAttempts`.
- **`DueAt` is never moved by a merge.** Leading edge; trailing edge starves under a steady stream.
- **`ComputeSweepDelayAsync` must include `GetNextScopeDueAsync`.** Without it a delayed coalescing
  scope runs up to a full `ClaimTimeout` late. A scope that is due but *blocked* behind an in-flight
  run falls through to the ceiling instead, deliberately — treating its past due time as "wake now"
  would spin the sweep on work it cannot take. The cost is the accepted follow-on latency.
- **Merging is read-modify-write in the host**, because the merge is caller-supplied C# and cannot be
  expressed in SQL without restricting merges to a fixed vocabulary. The retry is bounded
  (`MaxMergeAttempts`); past it the coalescer logs an Error naming the key and that event's work is
  not merged. Unbounded retry would hang the dispatch that published the event.
- **Merging captures `CommandType` at declaration** rather than resolving through
  `ConduitTypeRegistry` as `JobRunner` does, because merging always happens on a host holding the
  declaration. `BuildScope` writes `MessageType` from that same declared type, so a `Run` lambda
  returning a subtype does not leave `MessageType` naming the derived type while the first merge
  rewrites the payload as the base type.
- **The coalescing path is not keyed on the event's `MessageId`**, so unlike a plain trigger it gets
  no redelivery dedup from the unique run key. That is why the merge function has to be idempotent as
  well as widening, and why the user guide says so.

## Source generator dependency

`EventTriggerListener<TEvent>` is registered in DI at runtime, which only works because the
generated `PublishLocalAsync` emits a dispatch case for **every discovered event type**, not only
for types with a listener found at compile time (`MediatorEmitter.EmitPublishAsync`). Each case
already resolved its listeners from the container; the old gate meant an event type could never gain
a listener registered at runtime. `MediatorEmitterShould.ResolveListenersForAnEventTypeThatHasNoneAtCompileTime`
pins it — if that changes, event triggers stop firing silently.

`EventTriggerListener<TEvent>` carries `[NotAutoRegistered]`: it is an open generic, so the
scanner would otherwise record a listener for the literal type parameter and emit a case for a type
that does not exist.

## Tests

`tests/Conduit.Jobs.Tests/` — `Unit/` for occurrence computation and configuration validation,
`Integration/` for the store and the end-to-end scheduler behaviours, `Support/` for
`JobsTestHost` (awaits readiness before returning), `SharedSqliteDatabase` (several hosts, one
database, as replicas of one application), `JobsDatabaseProbe` (ledger assertions), `JobSpy`,
`RecordedJobHandler` (honours the spy's duration and failure script on the injected `TimeProvider`),
`WorkstationSpy` (the coalescing tests' spy — `BlockUntilReleased` is what holds a run in flight
while a burst accumulates behind it) and `FakeClock.AdvanceUntilAsync` (loops re-arm
asynchronously, so one large jump is not enough).

The claim protocol itself is covered against real Postgres, SQL Server and MySQL in
`tests/Conduit.Persistence.IntegrationTests/PersistenceEngineTestBase.cs` — SQLite exercises none of
the row-locking each server engine uses.
