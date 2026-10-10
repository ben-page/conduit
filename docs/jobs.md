# Background Jobs

- [Two kinds of job](#two-kinds-of-job)
- [Declaring jobs](#declaring-jobs)
  - [Recurring schedules](#recurring-schedules)
  - [Event triggers](#event-triggers)
  - [Coalescing a burst into the work it represents](#coalescing-a-burst-into-the-work-it-represents)
- [Durability is inferred, not declared](#durability-is-inferred-not-declared)
- [How a scheduled job runs](#how-a-scheduled-job-runs)
  - [Why occurrences are computed in memory](#why-occurrences-are-computed-in-memory)
  - [Why the runs table is deleted on success](#why-the-runs-table-is-deleted-on-success)
  - [Claim renewal](#claim-renewal)
- [How an event-triggered job runs](#how-an-event-triggered-job-runs)
- [Failure handling: two layers, in order](#failure-handling-two-layers-in-order)
- [Misfire: what happens to occurrences you were down for](#misfire-what-happens-to-occurrences-you-were-down-for)
- [Overlap](#overlap)
- [Cancellation](#cancellation)
  - [MaxRuntime](#maxruntime)
  - [Shutdown](#shutdown)
- [Operating jobs with IJobs](#operating-jobs-with-ijobs)
- [Run history](#run-history)
- [Without a database](#without-a-database)
- [A host that runs no jobs](#a-host-that-runs-no-jobs)
- [Tables](#tables)
- [Configuration reference](#configuration-reference)
- [Limitations](#limitations)

## Two kinds of job

`Conduit.Jobs` runs background work two ways: **on a schedule**, and **in reaction to an event**.
Both execute as ordinary Conduit commands, so there is no `IJob` contract to implement and nothing
about a job is special at the point of execution. A job body is a command with a handler:

```csharp
public sealed record RunNightlyRollup : ICommand;

public sealed class RunNightlyRollupHandler : ICommandHandler<RunNightlyRollup>
{
    public Task HandleAsync(RunNightlyRollup command, CancellationToken cancellationToken) => /* ... */;
}
```

Because it is a command, everything Conduit already does applies unchanged: it can be routed to a
worker endpoint instead of running locally, it passes through your pipeline behaviors, a
`Conduit.Resilience` policy wraps it, and `Conduit.Audit` records the dispatch and everything it
causes. Job history is a consequence of using the mediator, not a parallel subsystem.

## Declaring jobs

Schedules and triggers are **code-declared**, in one place, on the Conduit builder:

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.UseSqlite(connectionString);   // must come before AddJobs — see below

    conduit.AddJobs(jobs =>
    {
        jobs.Schedule("nightly-rollup")
            .Cron("0 2 * * *")
            .Run(() => new RunNightlyRollup());

        jobs.Schedule("cache-warm")
            .Every(TimeSpan.FromMinutes(5))
            .Run(() => new WarmCaches());

        jobs.Schedule("business-hours-report")
            .Cron("0 9 * * 1-5")
            .InTimeZone("Europe/London")
            .OnMisfire(MisfirePolicy.RunImmediately)
            .AllowOverlap()
            .MaxRuntime(TimeSpan.FromMinutes(20))
            .Run(() => new SendDailyReport());

        jobs.On<OrderPlaced>()
            .Run(order => new SendOrderReceipt(order.OrderId));

        jobs.On<CartAbandoned>()
            .After(TimeSpan.FromMinutes(30))
            .Run(cart => new SendAbandonmentReminder(cart.CartId));
    });
});
```

> **Register the persistence engine before `AddJobs`.** `AddJobs` decides whether jobs are durable
> from what is already in the service collection, so `UseSqlite(...)` after `AddJobs(...)` would
> silently leave every schedule non-durable. That ordering mistake fails at startup with a message
> naming the fix rather than producing a cluster that quietly runs each job on every host.

### Recurring schedules

| Method | Meaning |
|---|---|
| `Cron(expression)` | Five cron fields, or six to include seconds. UTC unless `InTimeZone` says otherwise. |
| `Every(TimeSpan)` | A fixed interval. Interval schedules have no misfire policy. |
| `InTimeZone(id)` | Evaluate the cron expression in this timezone. |
| `OnMisfire(policy)` | What to do about occurrences missed while the application was down. |
| `AllowOverlap()` | Permit a new occurrence to start while a previous one is still outstanding. |
| `MaxRuntime(TimeSpan)` | Cancel a run that exceeds this duration. |
| `Run(() => command)` | The command to dispatch. Completes the declaration. |

A schedule's name is its database key, so names must be unique — `AddJobs` rejects duplicates at
startup, and rejects a trigger whose name collides with a schedule's.

Cron is parsed by [Cronos](https://github.com/HangfireIO/Cronos), chosen for DST correctness. On a
spring-forward, a daily job whose local time does not exist that day runs once at the instant the
clock jumps, rather than being silently dropped; on a fall-back, a job in the repeated local hour
runs once rather than twice. Both are asserted by tests, because both are what a naive parser gets
wrong twice a year.

### Event triggers

`jobs.On<TEvent>()` runs a job when an event is published. `.After(TimeSpan)` delays it. The command
is built from the event instance, so the job carries whatever it needs:

```csharp
jobs.On<CartAbandoned>()
    .After(TimeSpan.FromMinutes(30))
    .Run(cart => new SendAbandonmentReminder(cart.CartId));
```

Triggers can be named, and must be if two of them react to one event type. The name defaults to the
event's type name and forms half of every run's database key, so two unnamed triggers on one event
collide:

```csharp
jobs.On<CartAbandoned>("reminder").Run(cart => new SendAbandonmentReminder(cart.CartId));
jobs.On<CartAbandoned>("analytics").Run(cart => new RecordAbandonment(cart.CartId));
```

Event triggers are **always durable** and require a persistence engine — configuring one without an
engine fails at startup. There is no non-durable variant because an ordinary `IEventListener<T>`
already is one, at zero cost; a second non-durable path through the jobs engine would duplicate it
and earn nothing.

### Coalescing a burst into the work it represents

Ten punch corrections at one workstation mean **one** rebuild, not ten. A trigger can say so:
`CoalesceBy` declares what makes two units of work the same, and `Merge` declares how they combine.

```csharp
jobs.On<PunchChanged>("workstation-rebuild")
    .CoalesceBy(punch => punch.LocationId)
    .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
    .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming);
```

`Merge` comes after `Run` so the command type is inferred from the lambda rather than written twice.

**Widen, never narrow.** A merge that loses window is silent staleness; doing the work twice is the
cheaper mistake. That is why there is no default merge and `AddJobs` rejects a `CoalesceBy` without
one — "keep the newest and discard the rest" is exactly the narrowing this exists to prevent.

**And be idempotent.** A non-coalescing trigger keys its run on the event's `MessageId`, so a broker
redelivering one event collapses on the run key for free. A coalescing trigger keys on the coalesce
key instead and inherits none of that: a redelivered event is merged twice. Widening merges — keep
the earlier instant, union a set, take a max — are idempotent by construction. An accumulating one is
not, and will over-count:

```csharp
// Wrong: a redelivered event is counted twice.
.Merge((pending, incoming) => pending with { Count = pending.Count + incoming.Count });
```

Coalesced work accumulates in `conduit_job_scopes`, one row per distinct key, and becomes a run only
when the work starts. So the ledger holds **one row per real execution**, which is the point: without
this, ten events produce ten runs, nine of them no-ops that drag the reported average down and make a
struggling projection look fast.

**A key's work yields at most two rows at a time: one run in flight, one scope accumulating behind
it.** That is a guarantee rather than a typical case, and the run key is what enforces it — a scope's
run is keyed on the scope key itself, and `JobKey` is unique, so a second run for one key cannot be
written. A pickup whose insert is refused therefore means "a run for this key is already outstanding",
and the scope stays where it is, still accumulating, to become the next run once the one ahead of it
clears. A run between attempts holds its key the same way, so a retry does not fan out either.

**Merging is leading-edge.** With `.After(delay)` the due time is set when the scope opens and a
later event never moves it, so a steady stream cannot starve the run. The merge has already widened
the scope, so the run still covers everything that arrived while it waited.

**A follow-on run waits for the sweep.** A scope parked behind an in-flight run is due but blocked,
and nothing nudges the sweep when the blocking run finally clears, so the follow-on starts up to
`ClaimTimeout` (2 minutes by default) afterwards. An undelayed trigger also retries the pickup on
every event it receives, so a busy key rarely waits that long — but `ClaimTimeout` is the bound, and
it is a known, accepted limitation rather than a bug. Size it accordingly if a projection's staleness
is what you are tuning. Each sweep also drains a bounded batch of scopes, oldest due time first, so
if a whole batch of them is blocked behind in-flight runs a newer, unblocked scope waits for the next
pass — self-resolving, since the blocked ones clear and stop crowding the batch.

**A run that gave up does not wedge its key.** Because the run key is the coalesce key, a terminal
run — attempts exhausted, or cancelled by an operator — still holds that key while it waits out
`FailedRetention` (7 days by default). Left alone it would silently freeze every later unit of work
on the key for a week, so a pickup **supersedes** it: the row is deleted and a Warning is logged
naming the key. The attempts that were exhausted belonged to *that* unit of work, and the scope
waiting behind it is different work that must not inherit the give-up. Stated honestly, the
consequence is that a permanently failing coalescing job does not stop at `MaxAttempts` — it tries
again whenever new events arrive for the key, logging that Warning each time.

Superseding is a delete, but it is not a loss: the outcome was already written to
[run history](#run-history) the moment the run went terminal, before anything ever supersedes or
purges the ledger row. A busy key can supersede before anyone looks at `GetFailedAsync`, but the
failure stays queryable in history regardless — the Warning in the log is a notification, not the
only surviving record.

**A merge that cannot land fails the event rather than dropping it.** The merge is a compare-and-set
retried a bounded number of times; if hosts are still colliding on one key past that, the listener
throws instead of returning quietly, so the transport can retry or dead-letter the event. Returning
would have acked it with its work in no scope at all.

The coalesce key is formatted with the invariant culture and must be stable across hosts and
processes — no `GetHashCode()`, nothing seeded per process, or hosts stop agreeing on one scope and
coalescing silently stops coalescing. A key that formats to null or whitespace is rejected, because
every scope for the trigger would collapse into one and merge unrelated work. And `{trigger}@{key}`
must fit 200 characters, so key on an id rather than on a composite of several fields.

## Durability is inferred, not declared

There is no per-schedule durability switch. **An engine is configured, so every schedule is durable.
No engine, so every schedule is non-durable.** That is the whole model. The engine is the one
registered on the builder with `UseSqlite`/`UsePostgres`/`UseSqlServer`/`UseMySql` — the same one the
outbox, inbox, dead-letter queue and audit trail use, and configured the same way. See
[persistence](persistence.md).

| | Engine configured | No engine |
|---|---|---|
| Scheduled jobs | Fire **exactly once across the cluster** | Fire **on every host** |
| Event triggers | Available | Rejected at startup |
| Misfire policy | Honoured | Rejected at startup |
| Pause, trigger-now, cancel | Cluster-wide, survive a restart | This host only, lost on restart |
| Run history | Every outcome, in `conduit_job_run_history` | Not recorded |

The accepted cost of that simplicity: an application with a database cannot have a genuinely
per-host job.

## How a scheduled job runs

With an engine configured, one occurrence of one schedule goes like this:

1. Every host computes the schedule's next occurrence in memory and sleeps until it.
2. At occurrence time, each host reads the schedule's cursor and skips if the occurrence is not
   strictly after it, or if the schedule is paused.
3. Unless the schedule allows [overlap](#overlap), each host takes the schedule's row lock and skips
   if another run of it is outstanding; this check and step 4's insert are one transaction.
4. Each host does an insert-or-ignore of a run row keyed `{schedule}@{occurrenceUtcIso}`. N replicas
   race; one row results.
5. Each host attempts an atomic claim. Exactly one wins.
6. The winner dispatches the command through `IMediator`.
7. On success the row is deleted and the cursor advances, in one transaction.

For three replicas that is three insert-or-ignores (two no-ops), three claim attempts (two returning
nothing), one delete and one cursor update. For a daily job, single-digit statements per day.

### Why occurrences are computed in memory

Schedules are code-declared, so every host has the same cron expression and reaches the same answer
without asking anyone. The database is therefore never consulted about **when** a job is due — only
about **who runs this one**. There is no poll loop looking for work that has come due, and
steady-state traffic is proportional to occurrences rather than to the passage of time.

A sweep does exist, but it is not a poll for due-ness: it exists to pick up runs whose timing is not
derivable from a declaration — a retry waiting out its backoff, a delayed event trigger, and the run
a crashed host abandoned. It sleeps until the earliest pending run is due, capped at `ClaimTimeout`
so an abandoned claim is still recovered when nothing else is scheduled.

### Why the runs table is deleted on success

Deleting on success rather than marking complete is what keeps the ledger's size proportional to
work in flight rather than to job volume. The table holds in-flight and terminal runs — plus, for a
short window, completed event-trigger runs (below) — never history —
every terminal outcome is written to [run history](#run-history) first, in the same transaction as
the ledger transition, so nothing is lost when the ledger row is later deleted.

That creates one problem, which the second table solves. A deleted row cannot answer "did we already
run 02:00 today?", and insert-or-ignore on the run key only prevents duplicates while the row
exists. The **cursor** in `conduit_job_schedules` closes the gap: an occurrence is materialized only
if it is strictly after `LastCompletedOccurrence`. That is why completion deletes the row and
advances the cursor in a single transaction — a crash between the two would leave an occurrence that
has run and looks like it has not.

An event trigger has no cursor to fall back on, so a non-coalescing trigger run is the one success
that is **not** deleted. Its row is kept with status `Completed` for `CompletedRetention` (default
1 day) and then purged. Its run key is `{trigger}@{event MessageId}`, and the kept row is what makes a
broker redelivering that event *after* the job finished — the usual shape, since redelivery follows a
lost ack — collapse on the key instead of running the job a second time. Deleting it on success would
only have caught redeliveries that raced the first run. A kept row has no claim and matches none of
the queries that look for live work; only the unique key sees it. A coalescing trigger's run is still
deleted on success: its key is the coalesce key, which the next scope needs.

### Claim renewal

`JobsOptions.ClaimTimeout` cannot simply be inherited from the outbox. The outbox's five minutes
suits a send measured in milliseconds; for a job it is a correctness bug. A rollup that legitimately
runs for ten minutes would have its claim treated as abandoned at minute five, be re-claimed by
another host, and **run twice**.

So a host holding a claim renews it on a heartbeat (`HeartbeatInterval`, default 30s) for as long as
it is genuinely running the job. Only a *dead* host's claim goes stale, after `ClaimTimeout`
(default 2 minutes — four missed heartbeats), at which point another replica recovers the
occurrence rather than losing it. Without renewal, "long job" and "dead host" are the same
observation and the recovery that makes the second safe executes the first twice.

Startup rejects a `ClaimTimeout` that is not more than twice `HeartbeatInterval`, because that
misconfiguration produces exactly the double execution renewal exists to prevent.

Two more things ride the same round-trip, since it is already happening: the renewal reports whether
the claim is still held (so a host that lost it stops rather than finishing work it no longer owns),
and it reads back the cancellation flag (so an operator's request reaches the running host without a
second query or a poll loop).

## How an event-triggered job runs

The event reaches exactly one host in every deployment shape — broker replicas share one
competing-consumer queue, a local publish is seen only by the publishing host, and under an engaged
outbox the row claim has already elected one host. So no claim race is needed to decide who writes
the run.

**Immediate trigger.** The receiving host writes the run row **already claimed with its own token** —
it received the event, it will run it — and starts it. Two writes: insert-as-claimed, then
delete-on-success. At 10,000 triggered jobs a day that is about 0.25 writes per second.

**Delayed trigger (`.After`).** The row is written **`Pending`, not claimed**, with `RunAt` set to
now plus the delay, and left for the sweep. Writing it claimed would be wrong: the claim would look
abandoned after `ClaimTimeout` and another host would take it, collapsing a thirty-minute delay into
two minutes. Delayed triggers therefore cost three writes, and are the one case where the triggering
host is not necessarily the running host.

**Coalescing trigger (`CoalesceBy`).** The receiving host merges the event into the trigger's scope
row instead of writing a run, then picks that scope up itself whenever the scope is **already due** —
which covers an undelayed trigger, and equally a delayed one whose scope opened earlier and whose
delay has since elapsed. Either way the latency matches the immediate path above. Only a scope still
in the future is left for the sweep. Pickup is what writes the run, and it deletes the scope in the
same breath.

A non-coalescing trigger's run key is `{trigger}@{event MessageId}`, which makes it idempotent
against broker redelivery for free — no dedup store, no inbox required. That holds while the run is
in flight and for `CompletedRetention` (default 1 day) after it succeeds; a redelivery later than that
runs the job again. A coalescing trigger keys on
the coalesce key instead, which is why its merge has to be idempotent as well as widening; see
[Coalescing a burst](#coalescing-a-burst-into-the-work-it-represents).

## Failure handling: two layers, in order

These are not competing retry mechanisms. They answer different questions, and the order matters.

**Inner — `Conduit.Resilience`.** The job command flows through `IPipelineBehavior`, so a registered
Polly policy retries transient failures in-process, against the same claimed row. This layer decides
how **this attempt** behaves.

**Outer — the ledger.** If the handler still throws once the in-process policy is exhausted, the
runner increments the attempt count, sets `RunAt` to now plus an exponential backoff (one minute,
two, four, …) and releases the claim to `Pending`. Any host picks it up on a later sweep. This layer
decides whether there is **another attempt**, and it is the only one that survives a process
restart, which is the thing Polly alone cannot give you.

After `MaxAttempts` (default 3) the row is marked `Failed`, kept for `FailedRetention` (default 7
days) so `IJobs` can surface it, and **the cursor advances** — one permanently broken occurrence must
not block every later one.

A coalescing trigger is the one case where a terminal ledger row is not the end of it: its run key is
the coalesce key, so the row is superseded when new coalesced work arrives for that key rather than
holding it for the retention period. The outcome itself is unaffected — it was already written to
[run history](#run-history) when the run went terminal, independent of the ledger row's fate.

Cancellation bypasses both layers: see below.

## Misfire: what happens to occurrences you were down for

When a schedule's next occurrence is first computed after startup, any occurrences between the
cursor and now were missed.

| Policy | Behaviour |
|---|---|
| `MisfirePolicy.Skip` (default) | Advance the cursor past every missed occurrence. Nothing runs. |
| `MisfirePolicy.RunImmediately` | Run **exactly one** catch-up, for the most recent missed occurrence, then resume normally. |

There is no backfill under either policy. Replaying a weekend outage would produce a burst of runs
whose moment has passed, which for most jobs — reports, reminders, invoices — is worse than missing
them.

A schedule that has missed more than 100,000 occurrences (a one-minute job down for over two months)
has no meaningful catch-up left. Its cursor is moved to now with a warning rather than stalling
startup walking every one of them.

`OnMisfire` requires a persistence engine and is rejected at startup without one, rather than
silently ignored: an in-memory timer has no record of what it missed.

## Overlap

By default a new occurrence is skipped while a previous one is still **outstanding** — running, or
awaiting a retry. A retry-pending run counts deliberately: the previous occurrence has not
succeeded, and starting a second while the first is still being retried is precisely the overlap the
policy exists to prevent.

This means a permanently failing occurrence stalls its schedule — but only until its attempts are
exhausted, at which point it becomes terminal and stops holding things up. Bounded, and visible
through `IJobs.GetFailedAsync`.

The rule covers a [manual run](#operating-jobs-with-ijobs) too, in both directions: `TriggerNowAsync`
is refused with a `JobOverlapException` while a run is outstanding, and while a manual run is
outstanding the schedule's occurrences are skipped. The check and the insert are one atomic step for
the scheduler and for trigger-now alike, on every host: each holds the schedule's
`conduit_job_schedules` row lock (a no-op `UPDATE`) from its check until its insert commits, so no
interleaving of the two — on one host or several — leaves two runs outstanding. That needs every host
on a version with the lock, and every host declaring the schedule the same way.

`AllowOverlap()` opts out, for occurrences and manual runs both.

## Cancellation

Cancellation is **cooperative and best-effort**. `CancelAsync` returning does not mean the work
stopped: a handler that never checks its `CancellationToken` runs to completion. And cancelled is
not "did not run" — whatever the handler completed before the token fired stays done. Both are worth
stating plainly, because the word implies otherwise.

```csharp
var outcome = await jobs.CancelAsync("nightly-rollup@2026-08-25T02:00:00.0000000Z", cancellationToken);
```

| Outcome | Meaning |
|---|---|
| `Requested` | The flag is set; the running host will see it within one heartbeat. Not "it stopped". |
| `NotFound` | No run with that key exists. |
| `AlreadyComplete` | The run had already reached a terminal state. |

`CancelAsync` returns this rather than `Task` so the caller has to confront the ambiguity instead of
reading "nothing to cancel" as success.

A cancelled run is marked `Cancelled`, which is **terminal and distinct from `Failed`**. Two
consequences follow, and both are load-bearing: the retry path never picks it up (retrying would
defeat the cancellation), and the cursor still advances (otherwise the occurrence is re-materialized
and cancelling loops forever).

Pause is a different thing and stays different: it prevents *future* occurrences and never touches
an in-flight run.

### MaxRuntime

An optional per-declaration ceiling. Exceeding it cancels the run's token and records the run as
`Cancelled`, turning a hung job into a bounded, visible outcome rather than one that blocks its
schedule indefinitely through the overlap check. It is enforced by the heartbeat, which already
knows when the attempt began, and it works with or without a database.

Because cancellation is cooperative, the heartbeat keeps renewing the claim after it has asked the
job to stop — otherwise a handler that takes its time winding down would have its claim go stale and
a second host would start the same job.

### Shutdown

There is no API for this and none is needed. Each dispatch's token is linked to the host's stopping
token, so on a graceful drain a well-written handler stops promptly, and the runner releases the
claim back to `Pending` with **the attempt count untouched** — a handover is not a failed attempt.
Another host picks the job up immediately rather than waiting out `ClaimTimeout`, which turns a
rolling deploy from "stalled jobs per drained pod" into a fast handover.

## Operating jobs with IJobs

```csharp
public interface IJobs
{
    Task<IReadOnlyList<JobScheduleInfo>> GetSchedulesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JobRunInfo>> GetInFlightAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JobRunInfo>> GetPendingAsync(JobFilter filter, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobScopeInfo>> GetPendingScopesAsync(JobFilter filter, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(JobFilter filter, CancellationToken cancellationToken);
    Task<JobHistoryPage> GetHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken);
    Task PauseAsync(string scheduleName, CancellationToken cancellationToken);
    Task ResumeAsync(string scheduleName, CancellationToken cancellationToken);
    Task TriggerNowAsync(string scheduleName, CancellationToken cancellationToken);
    Task<CancellationOutcome> CancelAsync(string jobKey, CancellationToken cancellationToken);
}
```

Inject `IJobs` anywhere — an admin endpoint, a diagnostics page, a CLI.

- `GetPendingAsync(filter)` — runs that are waiting rather than running, soonest first. This is where
  a run **between attempts** appears: it is `Pending` with `RunAt` set to when the next attempt falls
  due, so it shows in neither `GetInFlightAsync` nor `GetFailedAsync`, the latter listing only runs
  whose attempts are exhausted.
- `GetPendingScopesAsync(filter)` — coalesced work waiting to become a run. For a projection this is
  queue depth. `Version` climbing while `DueAt` holds still means a scope is absorbing a steady
  stream, which is coalescing working as intended.

There is no way to cancel a waiting scope: `CancelAsync` flags a run row and a scope has none. A
scope always runs, does its work, and clears.

**Pause** stops future occurrences; an in-flight run completes. Its state lives in
`conduit_job_schedules`, so it survives restarts and applies cluster-wide — a pause affecting only
the host that received the call would be useless. It deliberately leaves the cursor alone, so
resuming picks up from the next occurrence.

**Trigger-now** queues an extra run immediately, with no occurrence attached, so running a job by
hand cannot swallow the occurrence the schedule was already waiting for. It honours the schedule's
[overlap](#overlap) rule: without `AllowOverlap()` it throws `JobOverlapException` (naming the
schedule in `ScheduleName`) while a run is outstanding, and queues nothing. The exception is
deliberately not a `ConflictException` — that is a wire refusal, and whether an overlap is a client's
business is the caller's call:

```csharp
try
{
    await jobs.TriggerNowAsync(name, cancellationToken);
}
catch (JobOverlapException overlap)
{
    throw new ConflictException($"'{overlap.ScheduleName}' is already running or queued.");
}
```

`GetHistoryAsync(filter)` is different in kind from the queries above it: it does not read the
ledger at all. See [Run history](#run-history).

## Run history

`GetFailedAsync` and the ledger it reads from answer "what is currently outstanding or stuck" — a
row disappears the moment it succeeds, is superseded, or ages out past `FailedRetention`.
`GetHistoryAsync` answers a different question — "what actually happened" — from a separate,
append-only table that nothing ever supersedes:

```csharp
var page = await jobs.GetHistoryAsync(
    new JobHistoryFilter { ScheduleName = "nightly-rollup", Outcome = JobRunOutcome.Failed },
    cancellationToken);

foreach (var entry in page.Entries)
{
    Console.WriteLine($"{entry.JobKey}: {entry.Outcome} at {entry.CompletedAt} — {entry.LastError}");
}
```

A row is written to `conduit_job_run_history` at the exact moment a run reaches **any** terminal
outcome — `Completed`, `Failed`, or `Cancelled` — in the same transaction as the ledger transition
that records it. That is what makes it durable in a way the ledger itself is not: a coalescing
pickup superseding a terminal run, or `PurgeTerminalAsync` reclaiming a failed row past
`FailedRetention`, both delete only the ledger row. The history row was already written before either
could run, so the outcome survives regardless of what the ledger does to its own copy afterward.

`JobHistoryFilter` narrows and pages the result — `ScheduleName`, `JobKey`, `Outcome`, `Since`/
`Until`, plus `PageSize` and an opaque `PageToken` for the next page — mirroring
`Conduit.Durability`'s dead letter queue filter. `JobHistoryPage.TotalCount` and `NextPageToken` come
back with every query, so a caller can page through a large result set without a second round trip
to count it.

History has its own retention, `HistoryRetention` (default 30 days) — longer than `FailedRetention`,
because it is meant to answer "how has this job been doing" over a window wider than "what needs
attention right now". `JobCleanup`'s hourly sweep purges both independently.

Without a persistence engine there is no ledger and no history either — `GetHistoryAsync` returns an
empty page, the same honest degradation `GetFailedAsync` and `GetPendingAsync` use.

## Without a database

No engine means no tables. Each host runs one timer loop per schedule, waiting on the injected
`TimeProvider`, and dispatches directly.

- The schedule fires **on every host**. A Warning is logged at startup naming each schedule and
  saying so, because the guarantee is materially weaker and should be visible in any real
  deployment's logs rather than discovered from duplicated work.
- `IJobs` degrades honestly: queries answer from the in-memory registry, the run lists are empty
  because there is no ledger, and pause, trigger-now and cancel affect only the host they were
  called on. Trigger-now runs the command inline; the overlap rule then means "not while a run of
  the schedule is in progress on this host", and a manual run skips an occurrence that comes due
  while it is still going.
- `OnMisfire` and event triggers are **rejected at startup**, not silently ignored.
- Cancellation still works in-process, and `MaxRuntime` works normally — it needs nothing more than
  a start time and a token.

This is the usual precedent: Quartz's `RAMJobStore` is explicitly single-node and clustering needs
the ADO.NET store; Hangfire likewise requires a storage backend.

## A host that runs no jobs

Some processes need `IJobs` without being a place jobs run — a second host that shares the
application's database and composes handlers that take `IJobs`, say, or an admin tool. `AddJobs`
with an engine registered would make that host a full runner, and its claim sweep takes **any** due
run in the shared `conduit_job_runs`, including one whose handler is registered only in the process
that owns execution. `ManageOnly` turns the runner off and keeps `IJobs`:

```csharp
conduit.UseSqlServer(connectionString);       // the shared database, registered first as always
conduit.AddJobs(jobs => jobs.ManageOnly = true);
```

| | `ManageOnly` with an engine | `ManageOnly` without an engine |
|---|---|---|
| `IJobs` | Durable — reads and writes the shared ledger | In-memory, and lists nothing |
| Scheduler, claim sweep, cleanup, trigger listeners | None | None |
| Tables | Created at startup (unless `AutoCreateTable` is off); declared schedules reconciled | None |
| Schedules | May be declared, to list, pause and trigger them | Rejected at `AddJobs` |
| Event triggers | Rejected at `AddJobs` | Rejected at `AddJobs` |

- **Declare a schedule here only to operate it, and declare it exactly as the runner does.**
  Trigger-now builds the command, and applies the [overlap](#overlap) rule, from the declaration on
  the host it is called on.
- **An event trigger is rejected** rather than ignored: it fires only through the listener the
  runner registers, and no `IJobs` call needs it declared — `GetPendingAsync`,
  `GetPendingScopesAsync` and `CancelAsync` all take a trigger's name or a run's key as plain text.
- **Without an engine, any declaration is rejected**: there is no shared ledger for another host to
  run it from, and this host runs nothing. A host with nothing declared gets an `IJobs` that answers
  with empty lists.
- The tables are still created, because reading needs them; set `AutoCreateTable = false` when the
  runner (or your migrations) owns the schema and this host's login has no DDL rights.

It sits beside `ConduitOptions.ProducerOnly` (publish, never consume) and the outbox's `CaptureOnly`
(capture, never relay), and is independent of the former: a producer-only transport host may still
run jobs.

## Tables

All four are created at startup if absent, unless `AutoCreateTable` is `false`.

**`conduit_job_schedules`** — one row per declared schedule, reconciled at boot.

| Column | Purpose |
|---|---|
| `Name` (PK) | The schedule name from the declaration |
| `LastCompletedOccurrence` | The cursor that makes delete-on-success safe |
| `IsPaused`, `PausedAt` | Durable, cluster-wide pause state |

**`conduit_job_runs`** — in-flight and terminal runs, plus completed non-coalescing trigger runs
kept for `CompletedRetention`; never history.

| Column | Purpose |
|---|---|
| `Id` (PK, auto-increment) | Claim ordering |
| `JobKey` (unique) | `{schedule}@{occurrenceUtcIso}`, `{trigger}@{eventMessageId}`, or `{trigger}@{coalesceKey}` for a coalescing trigger |
| `ScheduleName` | The declaring schedule or trigger; drives overlap and the cursor |
| `Occurrence` | The occurrence this run is for, on schedule runs |
| `MessageType`, `Payload` | The command to dispatch |
| `RunAt` | Due time — the claim's eligibility column |
| `Status`, `ClaimedAt`, `ClaimToken` | Claim protocol, identical to the outbox's |
| `AttemptCount`, `LastError` | Durable retry state |
| `CancellationRequested` | Set by `CancelAsync`, observed by the running host's heartbeat |
| `StartedAt` | When the attempt began — drives `MaxRuntime` |

The unique index on `JobKey` is load-bearing, not an optimisation: idempotent materialization
conflicts on it.

`Occurrence` is separate from `RunAt` because a retry moves `RunAt` to the backoff time while the
cursor must still advance to the occurrence.

**`conduit_job_scopes`** — coalesced work waiting to become a run. Empty unless a coalescing trigger
is declared.

| Column | Purpose |
|---|---|
| `ScopeKey` (PK) | `{trigger}@{coalesceKey}` — one row per distinct unit of coalesced work |
| `ScopeId` | The row's generation identity: minted when the scope is opened, never changed by a merge |
| `TriggerName` | The declaring trigger; the pickup sweep and `GetPendingScopesAsync` group by it |
| `MessageType`, `Payload` | The merged command this scope will dispatch |
| `DueAt` | When the work becomes due. Written once — merging is leading-edge and never moves it |
| `Version` | Bumped by every merge; the optimistic guard on the read-modify-write |

`ScopeKey` is a single column rather than a composite `(TriggerName, CoalesceKey)` key because the
shared insert-or-ignore builder takes one conflict column, and two engines bake it into their SQL as
a single identifier.

`Version` is the compare-and-set that stops one host's merge clobbering another's — a lost merge
would *narrow* the scope, losing work rather than duplicating it — and it is what makes the delete at
pickup conditional, so a merge landing between writing the run and deleting the scope survives as the
next scope instead of vanishing. `ScopeId` is there because `Version` restarts at 0 every time a
scope row is deleted and reopened: a compare-and-set on the version alone could match the row that
*replaced* its target and narrow it, which is the exact failure the guard exists to prevent.

The run a scope becomes is keyed on the scope key itself, with no version suffix. That is what makes
the two-row guarantee hold: `JobKey` is unique, so the coalesce key doubles as a mutex and a refused
insert means the key is already outstanding.

**`conduit_job_run_history`** — every terminal outcome a run has ever reached. Unlike the other three
tables, nothing ever deletes a row here except the retention purge — not a coalescing pickup, not a
successful completion, not anything else.

| Column | Purpose |
|---|---|
| `Id` (PK, auto-increment) | Row identity, unrelated to `conduit_job_runs.Id` |
| `JobKey` | The run's key at the time it went terminal — **not unique**, unlike the ledger |
| `ScheduleName` | The declaring schedule or trigger |
| `MessageType` | The command that was dispatched |
| `Outcome` | `Completed`, `Failed`, or `Cancelled` |
| `RunAt` | When the run was due |
| `StartedAt` | When the final attempt began |
| `CompletedAt` | When the run reached this outcome — what history is ordered and purged by |
| `AttemptCount` | How many attempts had run by this point |
| `LastError` | The failure message, for `Failed` |

`JobKey` is deliberately not unique here: a coalescing key superseded and retried five times over a
bad afternoon leaves five rows, one per attempt at the underlying work, which is the whole point —
the ledger enforces "at most one row per key", history has no such constraint because superseding
never reaches it.

## Configuration reference

| Property | Default | Purpose |
|---|---|---|
| `HeartbeatInterval` | 30 seconds | How often a running host renews its claim |
| `ClaimTimeout` | 2 minutes | How stale a claim must be before another host may take it |
| `MaxAttempts` | 3 | Durable attempts before a run is marked failed |
| `FailedRetention` | 7 days | How long terminal ledger rows are kept for inspection |
| `CompletedRetention` | 1 day | How long a completed event-trigger run is kept, so a late redelivery of its event collapses |
| `AutoCreateTable` | `true` | Whether to create the jobs tables (including run history) on startup |
| `HistoryRetention` | 30 days | How long run history is kept |
| `ManageOnly` | `false` | Register `IJobs` without the runner — see [A host that runs no jobs](#a-host-that-runs-no-jobs) |

```csharp
conduit.AddJobs(jobs =>
{
    jobs.HeartbeatInterval = TimeSpan.FromSeconds(15);
    jobs.ClaimTimeout = TimeSpan.FromMinutes(1);
    jobs.MaxAttempts = 5;
    jobs.FailedRetention = TimeSpan.FromDays(30);
    jobs.CompletedRetention = TimeSpan.FromDays(3);  // the longest redelivery delay you expect
    jobs.HistoryRetention = TimeSpan.FromDays(90);
    jobs.AutoCreateTable = false;                 // you manage schema through migrations

    // schedules and triggers...
});
```

`ClaimTimeout` must be more than twice `HeartbeatInterval`, or startup fails.

Every knob here but `ManageOnly` describes the durable path.
[Without a database](#without-a-database) none of them apply — there are no tables to create, no
claim to renew or time out, no ledger rows to retain, and no history to purge.

## Limitations

- **No imperative one-shot scheduling.** There is no `ScheduleAsync(command, runAt)`. A delayed
  *event trigger* covers the declarative version of that need.
- **No backfill.** Misfire offers skip or one catch-up, and nothing else.
- **A coalesced follow-on run waits for the sweep**, so it starts up to `ClaimTimeout` after the
  run ahead of it clears. Nothing nudges the sweep when a blocking run finishes.
- **No runtime-defined schedules.** Schedules are code-declared; that is what lets every host agree
  on occurrences without coordinating.
- **Cancellation is not guaranteed.** It is cooperative, and partial work stays done.
- **No UI.** `IJobs` is the surface; build what you need on it.
