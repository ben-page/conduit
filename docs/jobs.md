# Background Jobs

- [Two kinds of job](#two-kinds-of-job)
- [Declaring jobs](#declaring-jobs)
  - [Recurring schedules](#recurring-schedules)
  - [Event triggers](#event-triggers)
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
- [Without a database](#without-a-database)
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

Event triggers are **always durable** and require a persistence engine — configuring one without an
engine fails at startup. There is no non-durable variant because an ordinary `IEventListener<T>`
already is one, at zero cost; a second non-durable path through the jobs engine would duplicate it
and earn nothing.

## Durability is inferred, not declared

There is no per-schedule durability switch. **An engine is configured, so every schedule is durable.
No engine, so every schedule is non-durable.** That is the whole model.

| | Engine configured | No engine |
|---|---|---|
| Scheduled jobs | Fire **exactly once across the cluster** | Fire **on every host** |
| Event triggers | Available | Rejected at startup |
| Misfire policy | Honoured | Rejected at startup |
| Pause, trigger-now, cancel | Cluster-wide, survive a restart | This host only, lost on restart |
| Failed-run history | In the ledger | Not recorded |

The accepted cost of that simplicity: an application with a database cannot have a genuinely
per-host job.

## How a scheduled job runs

With an engine configured, one occurrence of one schedule goes like this:

1. Every host computes the schedule's next occurrence in memory and sleeps until it.
2. At occurrence time, each host reads the schedule's cursor and skips if the occurrence is not
   strictly after it, or if the schedule is paused.
3. Each host checks for overlap, unless the schedule allows it.
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
work in flight rather than to job volume. The table holds in-flight and failed runs, never history —
history is `Conduit.Audit`, which already records every dispatch.

That creates one problem, which the second table solves. A deleted row cannot answer "did we already
run 02:00 today?", and insert-or-ignore on the run key only prevents duplicates while the row
exists. The **cursor** in `conduit_job_schedules` closes the gap: an occurrence is materialized only
if it is strictly after `LastCompletedOccurrence`. That is why completion deletes the row and
advances the cursor in a single transaction — a crash between the two would leave an occurrence that
has run and looks like it has not.

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

The run key is `{trigger}@{event MessageId}`, which makes the trigger idempotent against broker
redelivery for free — no dedup store, no inbox required.

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

`AllowOverlap()` opts out.

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
    Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(JobFilter filter, CancellationToken cancellationToken);
    Task PauseAsync(string scheduleName, CancellationToken cancellationToken);
    Task ResumeAsync(string scheduleName, CancellationToken cancellationToken);
    Task TriggerNowAsync(string scheduleName, CancellationToken cancellationToken);
    Task<CancellationOutcome> CancelAsync(string jobKey, CancellationToken cancellationToken);
}
```

Inject `IJobs` anywhere — an admin endpoint, a diagnostics page, a CLI.

**Pause** stops future occurrences; an in-flight run completes. Its state lives in
`conduit_job_schedules`, so it survives restarts and applies cluster-wide — a pause affecting only
the host that received the call would be useless. It deliberately leaves the cursor alone, so
resuming picks up from the next occurrence.

**Trigger-now** queues an extra run immediately, with no occurrence attached, so running a job by
hand cannot swallow the occurrence the schedule was already waiting for.

Detailed per-run history is **not** here. That is `Conduit.Audit`, which already captures every
dispatch with its full observation trail.

## Without a database

No engine means no tables. Each host runs one timer loop per schedule, waiting on the injected
`TimeProvider`, and dispatches directly.

- The schedule fires **on every host**. A Warning is logged at startup naming each schedule and
  saying so, because the guarantee is materially weaker and should be visible in any real
  deployment's logs rather than discovered from duplicated work.
- `IJobs` degrades honestly: queries answer from the in-memory registry, the run lists are empty
  because there is no ledger, and pause, trigger-now and cancel affect only the host they were
  called on.
- `OnMisfire` and event triggers are **rejected at startup**, not silently ignored.
- Cancellation still works in-process, and `MaxRuntime` works normally — it needs nothing more than
  a start time and a token.

This is the usual precedent: Quartz's `RAMJobStore` is explicitly single-node and clustering needs
the ADO.NET store; Hangfire likewise requires a storage backend.

## Tables

Both are created at startup if absent.

**`conduit_job_schedules`** — one row per declared schedule, reconciled at boot.

| Column | Purpose |
|---|---|
| `Name` (PK) | The schedule name from the declaration |
| `LastCompletedOccurrence` | The cursor that makes delete-on-success safe |
| `IsPaused`, `PausedAt` | Durable, cluster-wide pause state |

**`conduit_job_runs`** — in-flight and terminal runs only, never history.

| Column | Purpose |
|---|---|
| `Id` (PK, auto-increment) | Claim ordering |
| `JobKey` (unique) | `{schedule}@{occurrenceUtcIso}` or `{trigger}@{eventMessageId}` |
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

## Configuration reference

| Property | Default | Purpose |
|---|---|---|
| `HeartbeatInterval` | 30 seconds | How often a running host renews its claim |
| `ClaimTimeout` | 2 minutes | How stale a claim must be before another host may take it |
| `MaxAttempts` | 3 | Durable attempts before a run is marked failed |
| `FailedRetention` | 7 days | How long terminal runs are kept for inspection |

```csharp
conduit.AddJobs(jobs =>
{
    jobs.HeartbeatInterval = TimeSpan.FromSeconds(15);
    jobs.ClaimTimeout = TimeSpan.FromMinutes(1);
    jobs.MaxAttempts = 5;
    jobs.FailedRetention = TimeSpan.FromDays(30);

    // schedules and triggers...
});
```

`ClaimTimeout` must be more than twice `HeartbeatInterval`, or startup fails.

## Limitations

- **No imperative one-shot scheduling.** There is no `ScheduleAsync(command, runAt)`. A delayed
  *event trigger* covers the declarative version of that need.
- **No backfill.** Misfire offers skip or one catch-up, and nothing else.
- **No runtime-defined schedules.** Schedules are code-declared; that is what lets every host agree
  on occurrences without coordinating.
- **Cancellation is not guaranteed.** It is cooperative, and partial work stays done.
- **No UI.** `IJobs` is the surface; build what you need on it.
