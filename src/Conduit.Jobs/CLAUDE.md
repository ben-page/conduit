# Conduit.Jobs

Background jobs — code-declared schedules (recurring by `Cron(...)` or `Every(...)`, or `OnDemand()`
with no timetable) and event triggers, all executing as ordinary mediator commands. Configured via
`conduit.AddJobs()`. User docs: [jobs](../../docs/jobs.md).

## Persistence engine (Conduit.Persistence)

Same arrangement as Durability and Audit: `Conduit.Jobs` depends on `Conduit.Persistence` only and
has **no** direct database-driver dependency. The engine is resolved from DI —
`IConduitConnectionFactory` + `ISqlDialect` — and is registered on the **`ConduitBuilder`**
(`conduit.UseSqlite(cs)` / `UsePostgres` / `UseSqlServer` / `UseMySql`).

`DbJobStore` extends `DbStoreBase`; `JobTableInitializer` extends `TableInitializerBase`;
`JobCleanup` extends `PeriodicCleanupServiceBase`. `EnsureTablesExistAsync` runs its four creates through
`DbStoreBase.EnsureCreatedAsync`, so replicas starting together on an empty database all start (see
`src/Conduit.Persistence/CLAUDE.md`).

**Ordering is load-bearing.** `AddJobs` inspects `builder.Services` for the engine to decide durable
vs non-durable, so `UseSqlite(...)` must be called **before** `AddJobs(...)`. Getting it wrong is
caught at startup: `JobTableInitializer.ValidateConfiguration` throws when an engine is registered
but `IJobStore` is not, naming the fix.

## Directory layout

- `Configuration/` — `JobsBuilder`, `JobsOptions`, `ScheduleBuilder`, `EventTriggerBuilder<TEvent>`, `CoalescingTriggerBuilder<TEvent>` and `CoalescingTriggerBuilder<TEvent, TCommand>`, `MisfirePolicy`
- `Runtime/` — `JobSchedule`, `JobTrigger`, `ScheduleRegistry`, `DurableJobScheduler`, `InMemoryJobScheduler`, `ClaimedJobExecutor`, `JobRunner`, `ClaimHeartbeat`, `EventTriggerListener<TEvent>`, `ScopeCoalescer`, `JobCleanup`, `DurableJobs`, `InMemoryJobs`, `InMemoryJobState`, `ManualRunKey`, `JobSchedulerReadiness`, `JobSweepSignal`, `JobOutcome`, `JobAttempt`
- `Persistence/` — `IJobStore`, `DbJobStore`, `JobTableSchema` (`conduit_job_schedules`, `conduit_job_runs`, `conduit_job_scopes`, `conduit_job_run_history`), `JobRunsTableSchema`, `JobRun`, `JobRunStatus`, `JobRunOutcome`, `JobScheduleRow`, `JobScope`, `HeartbeatResult`, `MaterializeOutcome`
- root — `ConduitBuilderExtensions` (`AddJobs`), `JobTableInitializer`, `IJobs`, `JobScheduleInfo`, `JobRunInfo`, `JobRunHistoryInfo`, `JobScopeInfo`, `JobFilter`, `JobHistoryFilter`, `JobHistoryPage`, `CancellationOutcome`, `JobOverlapException`

## Time

**Nothing in this project may read the ambient clock.** Every scheduling decision goes through the
injected `TimeProvider` (`TryAddSingleton(TimeProvider.System)` in `AddJobs`), which is what makes
occurrence computation, misfire detection, backoff and `MaxRuntime` deterministically testable with
`FakeTimeProvider`. `grep -rn "DateTime.UtcNow\|DateTimeOffset.UtcNow" src/Conduit.Jobs/` must
return nothing.

Both schedulers read `TimeProvider.GetUtcNow()` in their **constructor** and wait for a schedule's
first occurrence after that, not after whenever the loop's first database read returns. Hosted services
are all constructed before any of them starts, so this is stable even when startup is slow.

**That instant picks which occurrence is first, never when occurrences fall.** Cron is absolute, and
an interval's occurrences are `UnixEpoch + k * Every` (`JobSchedule.NextIntervalOccurrenceAfter`):
`after` exactly on an occurrence yields the next one, strictly after, as the cron path's
`inclusive: false` does, because the cursor and every completed occurrence are such an `after`. It was
`after + Every`, so with no cursor each replica's timetable was its own start plus multiples of the
interval: replicas started seconds apart materialized different keys for one interval, and the cursor
and the overlap lock, both keyed on the occurrence, let each run it
(`DurableSchedulerShould.AgreeOnIntervalOccurrencesAcrossHostsWithDifferentStartTimes` saw six runs
across three hosts where three were due). A cursor written by an earlier build off the epoch's phase is harmless: the next
occurrence after it is aligned. Tests whose narrative needs the first occurrence one interval after
the host's start set `Start` one interval before an aligned occurrence (1:55 for five minutes, 1:00
for an hour). `JobScheduleShould.AlignIntervalOccurrencesToTheEpoch` pins the arithmetic, including a
seven-minute interval and a non-UTC offset.

## Registered services

`AddJobs` always registers `ScheduleRegistry`, `IOptions<JobsOptions>`, `TimeProvider` (TryAdd),
`JobSchedulerReadiness`, and `JobTableInitializer` **first** (hosted services start in registration
order and both schedulers read the tables on their first pass). Then, depending on the engine and
on `ManageOnly` (default off):

| Durable | Durable + `ManageOnly` | Non-durable | Non-durable + `ManageOnly` |
|---|---|---|---|
| `IJobStore` → `DbJobStore` (scoped) | same | — | — |
| `IJobs` → `DurableJobs` (scoped) | same | `IJobs` → `InMemoryJobs` (singleton) | same |
| `JobSweepSignal` (singleton) | same (nothing listens) | — | — |
| `ScopeCoalescer` (scoped) | — | — | — |
| `JobRunner`, `ClaimedJobExecutor` (singletons) | — | `InMemoryJobState` (singleton) | `InMemoryJobState` (singleton) |
| `DurableJobScheduler`, `JobCleanup` (hosted) | — | `InMemoryJobScheduler` (hosted) | — |
| One closed `EventTriggerListener<T>` per declared trigger's event type, activated in `ConduitTypeRegistry` | — (a trigger is rejected) | — | — (a trigger is rejected) |

**`ManageOnly` is "the runner off", not "jobs off".** It exists for a host that must resolve `IJobs`
but shares the tables with a host that owns execution — registering the durable runner there would
have its sweep (`ClaimDueAsync`) claim *any* due row, including one whose handler lives only in the
other process. `DurableJobs` needs only `IJobStore`, `ScheduleRegistry` and `TimeProvider`, so the
cut is clean. What stays, and why:

- `JobTableInitializer` — reading needs the tables, and `PauseAsync` is an `UPDATE` of the
  schedule's row, which the initializer's reconcile creates. It runs its usual validation too.
- `JobSchedulerReadiness` — registered **already complete** (`Expect(0)`): no loop will ever report
  armed, and anything awaiting `Ready` (`JobsTestHost`, a health check) must not hang on loops that do
  not exist.
- Declarations — schedules are allowed with an engine (list and trigger another host's schedules;
  they must match that host's declaration, since trigger-now reads the command and `AllowOverlap`
  from the local one). `AddJobs` throws for an event trigger (it fires only through the listener this
  mode omits; no `IJobs` call needs it declared) and, without an engine, for any declaration at all
  (nobody would ever run it). Off + no engine + nothing declared is an `IJobs` that lists nothing.
- **No SQL at startup when there is nothing to do.** With `AutoCreateTable = false` and nothing
  declared, a manage-only host issues no statement while starting (the initializer reconciles an empty
  list, and `DbJobStore`'s constructor touches no database), and `GetSchedulesAsync` returns `[]`
  without reading. A host whose login cannot reach the jobs tables relies on that;
  `ManageOnlyHostShould.StartWithoutTouchingTheDatabaseWhenItDeclaresNothingAndCreatesNoTables` pins it
  against a database that cannot even be opened.

The name follows the two existing "keep this half" flags — `ConduitOptions.ProducerOnly` (publish,
never consume) and `OutboxOptions.CaptureOnly` (capture, never relay): positive, default `false`,
named for what the host still does. It is deliberately not coupled to `ProducerOnly`.

## Claim reuse

`conduit_job_runs` implements `IClaimableTableSchema` (renamed from `IOutboxTableSchema` for this)
with `CreatedAtColumn` mapped to **`RunAt`**. The eligibility comparison against `@GraceCutoff` then
reads as "this job is due", so `ISqlDialect.BuildClaimPendingSql` becomes the jobs claim with **zero
new per-engine SQL** — and the Postgres/MySQL `FOR UPDATE SKIP LOCKED` and SQL Server
`ROWLOCK/UPDLOCK/READPAST` variants come along free — SQL Server's with its batch-level READ COMMITTED pin,
so a jobs sweep on a pooled session left at Snapshot (SQL Server 2022, SqlClient 7.1.1) claims too. Do not add a literal `CreatedAt` column; the
mapping is the whole trick. The claim's second cutoff, `NextAttemptAtColumn <= @Now` (the outbox's
backoff), maps to **`RunAt`** too, and `ClaimDueAsync` binds `@Now` to the same instant as `@GraceCutoff`,
so it agrees with the first: a job's retry already moves `RunAt` to its backoff time.

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

## The overlap lock

A schedule without `AllowOverlap()` has at most one run outstanding (Pending or Claiming), whoever
writes it: the scheduler materializing an occurrence on any host, or `IJobs.TriggerNowAsync` on any
host. Both write through `IJobStore.TryMaterializeWithoutOverlapAsync`, which is one transaction:
`UPDATE conduit_job_schedules SET IsPaused = IsPaused WHERE Name = @Name` (the schedule's row lock;
see "a row count of zero" below for proving it was taken), for an occurrence a read of
`LastCompletedOccurrence` (`AlreadyCompleted` if it is at or past the occurrence; see "the cursor is
checked under the same lock"), `COUNT(*)` of the schedule's other outstanding runs, then the usual
insert-or-ignore. Every no-overlap write for one schedule therefore runs one at a time, and each
check reads every earlier insert's commit; a run only *becomes* outstanding by insertion, so "at most
one" holds by induction. **No per-engine SQL** — the no-op `UPDATE` is the portable row lock. The
per-engine visibility argument is in the remarks on `DbJobStore.TryMaterializeUnderScheduleLockAsync`;
keep the two in step.

- **The run's own key is not overlap.** Every replica materializes the same occurrence key, so the
  count excludes it; a replica gets `AlreadyMaterialized` and goes on to claim, exactly as before.
  For trigger-now, an identical key means a second trigger in the same tick (a frozen test clock):
  the run is queued, so it returns.
- **A `RunAtStart()` start-up run is written like a manual run** — `Occurrence = null`, through this
  lock unless the schedule declares `AllowOverlap()` (then `TryMaterializeAsync`, through its start-instant key alone; otherwise `TryMaterializeWithoutOverlapAsync`), keyed `{schedule}@start-{startedAt}` by the
  scheduler's construction instant (`DurableJobScheduler.MaterializeStartUpRunAndClaimAsync`, after
  the misfire walk). That is why it neither moves nor is refused by the cursor: `AdvanceCursorOnAsync`
  needs an occurrence, and so does the `AlreadyCompleted` check. Each host's start writes its own
  (different keys); a second write for the same start collapses into the first only while that run
  is outstanding — trigger-now's guarantee, no more, because `retainRunKey` is for message-keyed
  triggers only and a completed schedule run's row is deleted. Unlike trigger-now it honours pause
  (nobody asked for it). `Overlapping` means an outstanding run is that start's work: logged, nothing
  written. A store error on the claim after the write (a deadlock victim: a host's `RunAtStart()`
  schedules all claim at once at its start) is logged and the run left queued for the sweep, which
  claims it within `ClaimTimeout` (default 2 minutes; up to twice that if the claim committed and only
  its read-back failed) — `ClaimQueuedRunAsync`, shared with an occurrence's claim; escaping, it ended
  that schedule's loop.
  While the host is stopping, any exception there is the stop's cancellation (SqlClient reports one as
  a `SqlException`) and logs nothing — likewise in the cursor read.
- **A row count of zero proves nothing, and neither does a probe that then finds the row.** A
  positive count from the lock `UPDATE` proves the lock on every engine. Zero means no row (nothing
  locked) or a lock the count does not show (MySQL with `UseAffectedRows=true`; SQL Server under
  `SET NOCOUNT ON` reports -1), so a probe follows. If the probe finds the row, the lock is taken
  **again**: on PostgreSQL and SQL Server at READ COMMITTED an `UPDATE` matching nothing locks nothing,
  and the probe sees a row another caller committed after it — that caller can then lock, check and
  insert while this one, trusting the probe, does the same. The persistence matrix's
  `WaitForTheLockWhenTheScheduleRowAppearsAfterTheFirstLockAttempt` reproduces exactly that, and fails
  on PostgreSQL and SQL Server without the re-lock. MySQL at REPEATABLE READ cannot get there (the
  `UPDATE`'s gap lock blocks the insert; the same test asserts it), and SQLite's `BEGIN IMMEDIATE`
  excludes every other writer. Only a delete *and* re-insert of the row by something outside Conduit,
  between the re-lock and its probe, could still leave a call unlocked; Conduit never deletes one.
- **The schedule row is ensured outside the transaction.** If it is missing, the transaction rolls
  back, an autocommit insert-or-ignore creates it, and the transaction starts over (twice at most,
  then `InvalidOperationException`). Inside, MySQL would bite twice: `INSERT
  IGNORE` on an existing row takes a *shared* lock, so two replicas would deadlock upgrading it on
  the `UPDATE` — on every occurrence, since every replica materializes at once; and under
  REPEATABLE READ the probe would fix the read view before the lock was held.
- **Lock order: the schedule's row, then run rows.** `CompleteAsync` and `MarkTerminalAsync` advance
  the cursor *first* for this. On SQL Server's locking READ COMMITTED the overlap count waits on a
  run row a completing transaction has deleted; if that transaction then wanted the schedule's row
  (as it did when the cursor came last), the two deadlocked — reproducibly, in the persistence
  matrix's `NeitherDeadlockNorOverlapWhenMaterializationsRaceACompletion` on SQL Server. Every other
  runs-table write is a single statement that never waits on a schedule row.
- **No isolation level is pinned** (as `CompleteAsync`). Pinning READ COMMITTED would fail on MySQL
  with statement-based binary logging; a stricter default turns the waiter's stale read into a
  serialization error — a failed call, never a second run.
- **The cursor is checked under the same lock.** A run with an `Occurrence` at or before
  `LastCompletedOccurrence` is `AlreadyCompleted`, and nothing is written. Before this the cursor was
  read only when a schedule's loop started, so a running replica whose timer fired late re-inserted —
  and re-ran — an occurrence another replica had completed and deleted. `CompleteAsync` and
  `MarkTerminalAsync` advance the cursor first, under this lock, in the transaction that deletes the
  row (on success) or marks it `Failed`/`Cancelled` and keeps it, so a late materialization sees either
  the outstanding row (and collapses into it) or the cursor. An
  `AllowOverlap()` schedule takes no lock, so the scheduler checks the cursor from the row it already
  reads for pause. A late replica re-runs the occurrence only if another replica's completion of it
  commits between that read and its insert (earlier, the insert collapses into the row; later, the read
  sees the cursor).
- **The cursor only moves forward.** `AdvanceCursorSql` is guarded `LastCompletedOccurrence IS NULL OR
  LastCompletedOccurrence < @new`, for completion, terminal marking and misfire alike. Overlapping runs
  can finish out of order; an unconditional `SET` let the earlier one move the cursor back behind the
  later, after which a replica late for the later occurrence found neither its row nor a cursor past it.
  The text comparison is chronological because every value is the same fixed-width round-trip UTC
  form, which the claim's `RunAt` cutoff already relies on. When the guard fails the `UPDATE` may lock
  nothing; that transaction then never waits on the schedule's row, so the lock order still holds.
  `OnlyEverMoveTheCursorForward` (SQLite, and every server engine in the matrix) pins it.
  `OverlapLockShould`,
  `DurableSchedulerShould.NotReRunAnOccurrenceAnotherReplicaCompletedWhileThisOneWasLate` and
  persistence matrix section 9 pin it. **Accepted window (owner's ruling, no grace period):** a host
  starting up counts an occurrence as missed once it is due and, under `MisfirePolicy.Skip` (every
  interval schedule's), advances the cursor to it; a running host whose timer for that occurrence fires
  a moment later then skips it as well. Timer latency plus a round trip, or the clock skew if larger;
  the cost is one skipped occurrence. The move-to-now past `MaxMisfireWalk` is the same class. The log
  line says "its cursor is already at or past it" for exactly this reason — it is not always another
  host's completion.
- **Pause is still checked outside the lock**, as before: a pause racing a materialization is about
  what happens next and needs no atomicity. Trigger-now still ignores pause.
- **Outside the guarantee:** a run written by `TryMaterializeAsync` for a no-overlap schedule (a host
  on an older version during a rolling upgrade), or hosts whose declarations disagree on
  `AllowOverlap()`. A rolling upgrade on SQL Server at locking READ COMMITTED has a second, transient
  effect: an older host's `CompleteAsync` still advances the cursor last and can deadlock with a newer
  host's locked materialization. If the completion is the victim, its claim goes stale and the run
  executes again after `ClaimTimeout`; if the materialization is, that occurrence is logged and
  skipped. It lasts only as long as the rollout.
- **The in-memory engine** enforces the rule per host: `InMemoryJobState.TryTrack` is a
  `ConcurrentDictionary.TryAdd`, shared by the scheduler loop and `InMemoryJobs.TriggerNowAsync`
  (which runs inline). There is nothing shared to coordinate through — every host fires every
  schedule anyway. A manual in-memory run is now cancellable through `CancelAsync` like a scheduled
  one, by its run key or its schedule's name: `TryTrack` records the key the durable runner would
  give the run (`ManualRunKey`, `DurableJobScheduler.BuildJobKey`/`BuildStartUpJobKey`), and `TryCancel`
  matches a run key first, so code written against the durable API (cancel the key trigger-now
  returned) does not get `NotFound` in a test host. Tests: `InMemoryJobsShould.CancelByRunKey`,
  `InMemoryJobStateShould.CancelTheRunAKeyNamesBeforeTheScheduleItAlsoNames`.
- **`JobOverlapException` is not `ConflictException`.** `ConflictException` is a wire refusal: any
  handler letting it escape would answer its client 409 with this message. An `IJobs` caller decides
  whether an overlap is its client's business; an uncaught one is an unexpected failure (500, logged).
  It derives from `InvalidOperationException` — the schedule's state refuses a valid request — as
  `RemoteDispatchException` does.

## Rejected alternatives

Recorded because each is appealing enough to be proposed again.

**Ensuring the schedule row inside the overlap transaction** (insert-or-ignore, then the no-op
`UPDATE`, then check and insert). It reads naturally and is fine on SQLite, Postgres and SQL Server,
but on MySQL `INSERT IGNORE` of an existing row takes a shared lock: two replicas materializing one
occurrence — every occurrence, on every multi-replica cluster — each hold one and deadlock upgrading
to the `UPDATE`'s exclusive lock. **`SELECT ... FOR UPDATE`** for the lock is per-engine syntax (SQL
Server has none). **A partial unique index on "one outstanding run per schedule"** cannot be expressed
on MySQL or through `ISqlDialect`.

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

- **An on-demand schedule gets no loop, in either scheduler.** `JobSchedule.IsOnDemand` is "no cron
  and no interval"; `NextOccurrenceAfter` returns null for it, and both `ExecuteAsync`s drop it before
  counting loops for `JobSchedulerReadiness.Expect`. Dropping it is load-bearing, not tidiness: a
  schedule loop whose first `NextOccurrenceAfter` is null exits without ever reporting armed, so an
  on-demand schedule given a loop would hold `Ready` (and every `JobsTestHost`) forever. Its only runs
  are trigger-now's, which the sweep claims like any manual run, and it touches no persisted shape:
  the initializer still reconciles its row (pause and the overlap lock need one) and its cursor never
  moves, because a manual run has no occurrence. `OnMisfire(...)` and `InTimeZone(...)` are accepted
  and ignored as on `Every(...)` — `FromOnDemand` fixes `Misfire` at `Skip`, so the no-engine misfire
  rejection never fires for it — while `RunAtStart()` and a second timetable are rejected in
  `ScheduleBuilder.Run`. The in-memory scheduler logs no "fires on every host" Warning for it.
  `JobScheduleInfo.IsOnDemand` is an init property rather than a sixth positional parameter so the
  record's constructor and `Deconstruct` keep their signatures. `OnDemandScheduleShould` pins it: each
  never-runs test runs an hourly control beside it, so the scheduler is provably live.
- **Two tables.** The runs table is deleted on success, so it cannot answer "did we already run
  02:00 today?". The cursor in `conduit_job_schedules` does. `CompleteAsync` and `MarkTerminalAsync`
  both delete/update **and** advance the cursor in one transaction — a crash between two separate
  writes leaves an occurrence that has run and looks like it has not.
- **A non-coalescing trigger keys its run on `MessageContext.DeliveredMessageId`, not `MessageId`.**
  The outbox's local delivery runs each attempt in a child context with a fresh `MessageId` (each
  attempt is its own audit row), so keying on `MessageId` would run the job once per attempt when a
  sibling listener fails the flush and the relay redelivers. `DeliveredMessageId` is the outbox entry's id on
  every attempt, and equals `MessageId` on the wire path, so broker redelivery collapses as before.
  Pinned by Durability's `OutboxEventTriggerShould`.
- **A non-coalescing trigger run is kept on success, as `Completed`.** It has no cursor, so the row
  is its only memory: deleting it made the `{trigger}@{DeliveredMessageId}` key collapse only
  redeliveries that raced the run, while the common redelivery — after a lost ack — arrives once the job has finished.
  `ClaimedJobExecutor` passes `retainRunKey` from `ScheduleRegistry.IsMessageKeyedTrigger`, and
  `CompleteAsync` turns the delete into an update that also clears the claim, so the row matches no
  Pending/Claiming query and only the unique key sees it. `JobCleanup` purges it after
  `CompletedRetention` via `PurgeCompletedAsync`, separately from `FailedRetention` because the two
  answer different questions. Coalescing runs are still deleted: their key is the coalesce key, and a
  kept row would freeze every later scope for it.
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
- **So is every terminal write.** `CompleteAsync`, `IncrementAttemptAsync` and `MarkTerminalAsync`
  take the claim token, change the run row only `WHERE Id = @Id AND Status = @Claiming AND
  ClaimToken = @ClaimToken` (`DbJobStore.ClaimGuard`, module-owned, identical on every engine, the
  outbox's resolution guard), and return `Task<bool>` (rows affected == 1). The two transactional ones
  keep the lock order (cursor first, then history, then the guarded run-row statement) and roll the
  whole transaction back when the guard matches nothing (`CommitIfResolvedAsync`), so a lost claim
  advances no cursor and writes no history. Why: host A's claim goes stale past `ClaimTimeout` (a GC
  or VM pause, a partition), B re-claims under a new token, then A's job ends; unguarded, A's
  completion deleted the row B was running (a late replica could then re-materialize the occurrence)
  and A's failed attempt released it to `Pending` for a third host. `IncrementAttemptAsync` used to
  return the new attempt count; nothing read it. On `false`, `ClaimedJobExecutor` logs `ClaimLost`
  (EventId 410, Warning, the run's key) and writes nothing else: the retry and exhaustion logs now
  follow a write that landed. It logs the same 410 for a run the heartbeat gave up. Tests:
  `JobStoreShould.NotCompleteARunAnotherHostHolds` / `.NotMarkTerminalARunAnotherHostHolds` /
  `.NotCountAnAttemptOnARunAnotherHostHolds`, `ClaimedJobExecutorShould.WriteNothingWhenTheClaimWasLost`.
- **A heartbeat that cannot renew fences itself.** `ClaimHeartbeat` tracks the last renewal that
  landed (`ClaimedAt`, then each successful beat's `now`); `fenceAt` is that plus `ClaimTimeout`, the
  instant another host's sweep may re-claim. At `fenceAt` it sets `ClaimLost` and cancels the job token
  (`FenceAsync`, `LogClaimFenced`). Three details make "at" true rather than "a renewal later": the wait
  before a beat is clamped to the fence (the interval need not divide `ClaimTimeout`); each renewal
  begun before the fence carries a token cancelled at it, so a hung connection counts as a failure
  there; and after a failed renewal (`lastRenewalFailed`) reaching the fence fences outright, with no
  further try, because a try begun at the fence has no deadline left and fails no sooner than the last
  one did (renewals whose connect times out after 15 s would otherwise fence 15 s late). A renewal
  begun after the fence with the last one landed (the host woke late: a GC or VM pause) carries no
  deadline: if it lands, nobody took the claim. That is why a discontinuous test clock with a healthy
  store never fences. **Stopping wins:** `DisposeAsync` cancels `stop`, which is linked into every
  renewal's token, and a renewal that fails once stopping returns without fencing. The job has ended
  and its outcome's guarded write decides; fencing there would drop a finished job's outcome as a 410.
  `ClaimTimeout` is capped at one day (`JobsOptions.LongestDuration`, checked in `JobsOptions.Validate()`): the deadline's
  `CancellationTokenSource(TimeSpan, TimeProvider)` throws past about 49.7 days, which would fail every
  renewal. Tests: `ClaimHeartbeatShould.FenceItselfAfterClaimTimeoutOfFailures` /
  `.FenceAtTheClaimTimeoutWhenTheRenewalThatWouldDecideItHangs` /
  `.FenceAtTheClaimTimeoutWhenTheIntervalDoesNotDivideIt` / `.FenceItselfWhenARenewalOutlastsTheClaimTimeout` /
  `.AbandonARenewalInFlightWhenStopped` (support: `UnreachableJobStore`, whose hang ends after 30 s real
  time so a regression fails under the tests' `[Timeout(30_000)]` rather than holding the run),
  `JobsConfigurationShould.RejectAClaimTimeoutAboveADay`; the executor's two other 410 paths are pinned by
  `ClaimedJobExecutorShould.LogTheClaimLostForARunTheHeartbeatGaveUp` and
  `.WriteNothingWhenAFailedAttemptsClaimWasLost`.
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
- **A cursor that cannot be read at start is read as no cursor** (logged): the loop waits for the first
  occurrence after the host's start and this start's misfire policy is skipped, rather than the loop ending. A completed
  occurrence still cannot re-run: every occurrence's write checks the real cursor (under the overlap
  lock, or from the schedule row for `AllowOverlap()`).
- **Purge covers `Cancelled` as well as `Failed`.** Both are terminal history kept only for an
  operator to look at, and neither is ever retried, so leaving cancelled rows grows the ledger
  without bound.
- **A scope's run key is the scope key, with no version suffix.** `JobKey` is unique, so the coalesce
  key doubles as a per-key mutex: a refused insert means "a run for this key is already outstanding",
  which is what turns "one in flight, one accumulating" into a guarantee. It also collapses a
  repeated pickup **while that run is still outstanding**: a host dying between materializing the run
  and deleting the scope leaves the scope alive, and the next sweep's insert is refused by the same
  unique key. That is the whole of the property — it does not reach past the run. Once the run
  completes, `CompleteAsync` deletes its row and frees the key, so an orphaned scope re-materializes
  and the same merged command runs a second time; a transient failure of the scope delete lands in
  exactly the same place. At-least-once is the contract, and this is the direction every window in
  this design is resolved: duplicate rather than drop.
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
  whenever new events arrive rather than stopping at `MaxAttempts`. This used to also mean the
  superseded row's failure had no surviving record but that Warning — history (below) closes that
  gap without touching the retry policy itself.
- **Run history is a separate, append-only table, not a status.** `CompleteAsync` and
  `MarkTerminalAsync` both write a `conduit_job_run_history` row — `Completed`, `Failed`, or
  `Cancelled` — in the same transaction as the ledger transition, before the ledger row is deleted or
  marked terminal. That ordering is what makes `TryClearTerminalRunAsync` and `PurgeTerminalAsync`
  safe to keep deleting exactly as before: whatever they remove from the ledger was already durably
  recorded. Both methods now take the full `JobRun` (not just its `Id`) plus a `DateTimeOffset now`,
  because writing the history row needs the run's data and a timestamp for `CompletedAt`. A composite
  `UNIQUE(JobKey, TerminalId)`-style scheme on `conduit_job_runs` itself was considered and rejected:
  it would have widened `ISqlDialect.BuildInsertOrIgnoreSql`'s single-column conflict-target
  signature (shared with Durability's outbox and Audit), broken Postgres's `ON CONFLICT (JobKey)`
  target match, and needed `GetRunAsync` and everything depending on it to filter out terminal rows a
  new query has no reason to see. A decoupled table — the same shape `Conduit.Durability`'s dead
  letter queue already uses — needs none of that: no dialect changes, no relaxed uniqueness
  invariant, and a schema actually shaped for the queries a dashboard runs (`JobHistoryFilter`/
  `JobHistoryPage` mirror `DeadLetterFilter`/`DeadLetterPage` exactly). `JobRunHistoryInfo`
  deliberately omits `Payload`, matching `JobRunInfo`'s existing precedent — history's retention
  (`HistoryRetention`, default 30 days) is meant to be materially longer than the ledger's, and there
  is no reason to extend how long a command payload stays persisted just because its outcome is kept
  longer.
- **`DueAt` is never moved by a merge.** Leading edge; trailing edge starves under a steady stream.
- **`ComputeSweepDelayAsync` must include `GetNextScopeDueAsync`.** Without it a delayed coalescing
  scope runs up to a full `ClaimTimeout` late. A scope that is due but *blocked* behind an in-flight
  run falls through to the ceiling instead, deliberately — treating its past due time as "wake now"
  would spin the sweep on work it cannot take. What ends that wait is the blocking run's end, which
  wakes the sweep of the host that ran it (`JobSweepSignal`, below); only a run whose outcome is never
  recorded (its host died) leaves the follow-on to the ceiling.
- **The sweep is woken by work this process writes (`JobSweepSignal`).** It sleeps on what was due when
  it last looked, so a run written meanwhile waited up to `ClaimTimeout`: "run now" was not now. A
  singleton, released (idempotently, `TrySetResult`) by `DurableJobs.TriggerNowAsync` after the write,
  `EventTriggerListener`'s delayed `TryMaterializeAsync`, `ScopeCoalescer.MergeAsync` opening a scope due
  later, and `ClaimedJobExecutor` at the end of every run, whatever its outcome (it may free a coalescing
  key or the overlap lock, or set a retry sooner than the sleep). The sweep `Arm()`s at the start of
  each pass (a wake before that is spent: the pass reads its write) and sleeps on `Task.WhenAny(delay,
  woken)`, cancelling the delay when the wake wins. A `TaskCompletionSource` with asynchronous
  continuations, not a `SemaphoreSlim`: nothing to dispose (a run left behind past shutdown may still
  wake it), and a waker never runs the sweep's pass on its own thread. The scheduler's own occurrence
  writes do not wake it: their loop claims straight after. In-process only — registered for a
  manage-only host too, so `DurableJobs` resolves, but nothing listens there and its trigger-now waits
  for a runner's sweep. Tests: `DurableJobSchedulerShould.RunATriggeredJobWithoutWaitingForTheSweep` /
  `.RunADelayedTriggerWhenItComesDueRatherThanOnTheSweepsNextPass` /
  `.PickUpADelayedScopeWhenItComesDueRatherThanOnTheSweepsNextPass`.
- **A run cancelled before it started is never dispatched.** `RequestCancellationAsync` flags Pending
  rows too, and `ClaimDueAsync` claims them like any other; only the claim's read-back carries the flag,
  since no heartbeat has run. `ClaimedJobExecutor` checks `run.CancellationRequested` first and records
  `JobOutcome.Cancelled` through the guarded `MarkTerminalAsync` (cursor, `Cancelled` history row, a lost
  claim logs 410 as usual), with no dispatch. A cancel landing after that check is the heartbeat's, as
  before. Test: `DurableJobsShould.NotRunAPendingRunThatWasCancelled`.
- **A `[FireAndForget]` command is not a job body.** Its generated dispatch enqueues the handler and
  returns, so `JobRunner` would record Completed before it ran. `JobRunner.RequireInlineBody` (the
  attribute read with inheritance, as `RoslynScanner` reads it; `ConduitTypeRegistry` carries no F&F
  flag) refuses it in three places: at declaration, from `typeof(TCommand)` — `ScheduleBuilder.Run`,
  `EventTriggerBuilder.Run` and `CoalescingTriggerBuilder.Run` are generic for this (the first two
  were `Func<ICommand>`/`Func<TEvent, ICommand>`; every lambda call site infers the type); on each
  command built, because `Run` wraps the factory (`JobSchedule.CreateCommand`/`JobTrigger.CreateCommand`),
  which catches `Run<ICommand>(...)` returning one in `BuildRun`, the start-up run, trigger-now, the
  in-memory paths and the trigger listener (the coalescing factory is not wrapped: its scope is
  serialized and run as the declared `TCommand`); and in `Rehydrate`, failing the attempt of a stored
  run whose type gained the attribute. Tests: `ScheduleBuilderShould`,
  `JobFailureShould.FailAStoredRunWhoseCommandIsFireAndForget`.
- **Shutdown's drain is bounded by the host's deadline.** `DurableJobScheduler.StopAsync` awaits
  `DrainAsync().WaitAsync(cancellationToken)`; on the deadline it logs `DrainAbandoned` (EventId 411,
  Warning, the in-flight run keys from `ClaimedJobExecutor.RunningJobKeys`) and returns. Unbounded, a
  handler ignoring its token held the host's stop open past `ShutdownTimeout` for as long as it ran.
  No grace period: jobs stay linked to `ApplicationStopping` from its first instant. A run left behind
  keeps its claim until renewals stop; another host re-claims it after `ClaimTimeout`. Test:
  `DurableJobSchedulerShould.StopWhenTheShutdownTokenExpires`.
- **`JobsOptions.Validate()`**, called first in `AddJobs`, throws `InvalidOperationException` naming the
  option (the jobs package's and `DispatchRetryOptions`' exception, not Durability's
  `OptionsValidationException`; the two older checks' messages are unchanged): `HeartbeatInterval` not
  positive (the heartbeat would renew in a loop), `ClaimTimeout` not more than twice it or above a day,
  `MaxAttempts` not positive, a retention negative or above 3650 days, `CleanupInterval` not positive or
  above a day, `RetryBackoff` empty or with an entry not positive or above a day. A declaration's
  `MaxRuntime` is not an option, so `AddJobs` checks it beside, over schedules and triggers.
  **The ceilings are load-bearing**, and mirror Durability's `DurationLimits` (`LongestDuration` one day,
  `LongestRetention` 3650 days; mirrored, as `JobsOptions.LongestDuration`/`LongestRetention`, because
  Jobs does not reference Durability and those are internal): a `CleanupInterval` past about 49.7 days
  makes `PeriodicCleanupServiceBase`'s `Task.Delay`, which is outside its try, throw after the first pass,
  ending `JobCleanup` and, under the default `BackgroundServiceExceptionBehavior`, the host; a
  `RetryBackoff` entry near `TimeSpan.MaxValue` ("never retry") overflowed `GetUtcNow() + backoff`, the
  catch-all logged `OutcomeNotRecorded`, and the run re-executed every `ClaimTimeout` with its attempt
  count never moving (no retry is `MaxAttempts = 1`); a retention near it made `now - retention` throw on
  every cleanup pass, so nothing was purged. `RetryBackoff` (default 1, 2, 4 minutes; `BackoffAfter(n)`
  takes entry `min(n - 1, Count - 1)`, the last repeating) replaced `30 * 2^attempts` in
  `ClaimedJobExecutor`, and `CleanupInterval` (default 1 hour) the `JobCleanup` constant; both are
  mirrored on `JobsBuilder`. Tests: `JobsOptionsShould` (`.RefuseADurationAboveItsCeiling`,
  `.AcceptEveryCeilingExactly`), `JobsConfigurationShould.RejectANonPositiveMaxRuntimeOn*`,
  `JobFailureShould.DelayTheRetryByTheConfiguredBackoff`.
- **`MaxRuntime` is checked on heartbeat ticks** (durable path), so it takes effect at the first renewal
  at or after the limit: 10 s under the default 30 s interval fires at about 30 s. The in-memory scheduler
  uses a `CancellationTokenSource(limit, timeProvider)` and is exact. Documented, not changed.
- **Merging is read-modify-write in the host**, because the merge is caller-supplied C# and cannot be
  expressed in SQL without restricting merges to a fixed vocabulary. The retry is bounded
  (`MaxMergeAttempts`); past it the coalescer logs an Error naming the key and **throws**, so the
  listener failure propagates as an `EventListenerException` and the transport retries or
  dead-letters the event. Returning quietly would let the dispatch complete — and the broker ack —
  with that event's work in no scope at all, which is a drop, the one outcome this module never
  resolves toward. Unbounded retry would instead hang the dispatch that published the event.
- **The sweep's scope drain isolates each pickup.** `PickUpDueScopesAsync` runs *ahead of*
  `ClaimAndRunDueAsync` inside one `try`, so an exception escaping one scope would abort every scope
  behind it and the pass's claim with it, idling the sweep for a full `ClaimTimeout`. Each pickup is
  therefore wrapped, logging at Error and moving on, the same shape `MaterializeAndClaimAsync` uses
  for one schedule's database error.
- **Merging captures `CommandType` at declaration** rather than resolving through
  `ConduitTypeRegistry` as `JobRunner` does, because merging always happens on a host holding the
  declaration. `BuildScope` writes `MessageType` from that same declared type, so a `Run` lambda
  returning a subtype does not leave `MessageType` naming the derived type while the first merge
  rewrites the payload as the base type.
- **The coalescing path is not keyed on the event's `DeliveredMessageId`**, so unlike a plain trigger it gets
  no redelivery dedup from the unique run key. That is why the merge function has to be idempotent as
  well as widening, and why the user guide says so.

## Source generator dependency

`EventTriggerListener<TEvent>` is registered in DI at runtime, which only works because the
generated `PublishLocalAsync` emits a dispatch case for **every discovered event type**, not only
for types with a listener found at compile time (`MediatorEmitter.EmitPublishAsync`). Each case
already resolved its listeners from the container; the old gate meant an event type could never gain
a listener registered at runtime. `MediatorEmitterShould.ResolveListenersForAnEventTypeThatHasNoneAtCompileTime`
pins it — if that changes, event triggers stop firing silently.

For the same reason no broker would subscribe the event: the generated registry's active events are
the ones with a compile-time listener. `AddJobs` calls `ConduitTypeRegistry.ActivateEvent` for each
trigger's event, on the registry instance the generated `AddConduit` registers before running the
`configure` callback (found by descriptor in `builder.Services`; a hand-made `ConduitBuilder`, as the
unit tests use, has none). `EventTriggerRegistrationShould` pins it.

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

**SQLite concurrency needs `Task.Run`.** Microsoft.Data.Sqlite's async methods run synchronously,
busy retry included (up to the 30-second command timeout), so `Task.WhenAll` over store calls on one
thread takes turns rather than racing — and a call that waits on a lock the test itself holds blocks
the test's thread until the timeout. The overlap tests (`OverlapLockShould`, `TriggerNowShould`) start
each contender with `Task.Run`.

The claim protocol itself is covered against real Postgres, SQL Server and MySQL in
`tests/Conduit.Persistence.IntegrationTests/PersistenceEngineTestBase.cs` — SQLite exercises none of
the row-locking each server engine uses. So is the overlap lock (section 9): that the no-op `UPDATE`
blocks a second writer and the read after it sees the holder's commit, at-most-one-outstanding under
twenty concurrent writers, and deadlock-freedom against a completing run.
