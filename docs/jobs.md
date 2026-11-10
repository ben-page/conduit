# Background Jobs

- [Two kinds of job](#two-kinds-of-job)
- [Declaring jobs](#declaring-jobs)
  - [Recurring schedules](#recurring-schedules)
  - [On-demand schedules](#on-demand-schedules)
  - [Event triggers](#event-triggers)
  - [Coalescing a burst into the work it represents](#coalescing-a-burst-into-the-work-it-represents)
- [Durability is inferred, not declared](#durability-is-inferred-not-declared)
- [How a scheduled job runs](#how-a-scheduled-job-runs)
  - [When the database is consulted](#when-the-database-is-consulted)
  - [Success deletes the run, the cursor remembers it](#success-deletes-the-run-the-cursor-remembers-it)
  - [Claim renewal](#claim-renewal)
- [How an event-triggered job runs](#how-an-event-triggered-job-runs)
- [Failure handling: two layers, in order](#failure-handling-two-layers-in-order)
- [Misfire: what happens to occurrences you were down for](#misfire-what-happens-to-occurrences-you-were-down-for)
- [Run at start](#run-at-start)
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

**The handler must run inline, so a `[FireAndForget]` command cannot be a job body.** Its dispatch
hands the handler to the background dispatcher and returns at once, so the run would be recorded
`Completed` before the handler had started, out of reach of `MaxRuntime`, cancellation, retries and
run history. `Run(...)` refuses one at startup with an `InvalidOperationException` naming the
schedule or trigger and the command. A factory declared with a wider type (`Run<ICommand>(...)`)
that returns one is refused when it builds the command, and a stored run naming one (written before
the type gained the attribute) fails its attempt. To run fire-and-forget work on a schedule, make the
job body a plain command whose handler dispatches it.

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
| `Every(TimeSpan)` | A fixed interval, aligned to the Unix epoch (see below). Interval schedules have no misfire policy. |
| `OnDemand()` | No timetable: runs only when `IJobs.TriggerNowAsync` starts it. See [On-demand schedules](#on-demand-schedules). |
| `InTimeZone(id)` | Evaluate the cron expression in this timezone. |
| `OnMisfire(policy)` | What to do about occurrences missed while the application was down. |
| `RunAtStart()` | One run when the host starts — interval schedules only; not an occurrence. See [Run at start](#run-at-start). |
| `AllowOverlap()` | Permit a new run (an occurrence or a trigger-now) to start while another is still outstanding. |
| `MaxRuntime(TimeSpan)` | Cancel a run that exceeds this duration. Positive; checked on the heartbeat, see [MaxRuntime](#maxruntime). |
| `Run(() => command)` | The command to dispatch. Completes the declaration. |

A schedule declares exactly one of `Cron(...)`, `Every(...)` and `OnDemand()`; none, or any two,
fails at startup naming the schedule. A schedule's name is its database key, so names must be
unique — `AddJobs` rejects duplicates at startup, and rejects a trigger whose name collides with a
schedule's.

An interval's occurrences are the whole multiples of the interval since the Unix epoch
(1970-01-01T00:00Z), whichever host computes them and whenever it started:
`Every(TimeSpan.FromMinutes(5))` runs at :00, :05, :10 and so on past every hour, and
`Every(TimeSpan.FromHours(1))` on the hour. An interval that divides no day, such as seven minutes,
still has the one timetable every replica shares; it just does not line up with the clock face. A
host's first occurrence is therefore the next one after it starts, up to one interval away, not one
interval after its start: anchored to each host's start instead, replicas that came up seconds apart
would each run every interval at a time of their own. The epoch was a Thursday, 00:00 UTC, so
`Every(TimeSpan.FromDays(7))` runs every Thursday at 00:00 UTC, not weekly from a deploy; use
`Cron(...)` for a weekly job on another day or at another time.

Cron is parsed by [Cronos](https://github.com/HangfireIO/Cronos). On a spring-forward, a daily job
whose local time does not exist that day runs once at the instant the clock jumps, rather than being
silently dropped; on a fall-back, a job in the repeated local hour runs once rather than twice.

### On-demand schedules

`OnDemand()` declares a schedule with no timetable: nothing ever starts it on its own, only
[`IJobs.TriggerNowAsync`](#operating-jobs-with-ijobs). Use it for one-off maintenance an operator
starts by hand — a re-derivation to run once after a deploy, a repair to run once someone has looked
at the data — so the job is declared, listed and run like every other job rather than through a
one-off script.

```csharp
jobs.Schedule("rederive-totals")
    .OnDemand()
    .MaxRuntime(TimeSpan.FromHours(1))
    .Run(() => new RederiveTotals());

// Later, from an admin endpoint or a CLI command:
var key = await jobs.TriggerNowAsync("rederive-totals", cancellationToken);
```

- **Neither scheduler starts it.** It has no occurrences, so no loop waits for one: nothing is
  computed, written or caught up for it however long a host runs, and a restart does not run it. Its
  `conduit_job_schedules` row holds its pause flag and a cursor that nothing moves (a manual run is
  not an occurrence), so it needs no new column.
- **A manual run of it is a manual run of any schedule.** The [overlap](#overlap) rule refuses a
  second trigger with `JobOverlapException` while a run is outstanding unless `AllowOverlap()` is
  declared; `MaxRuntime`, retries and [run history](#run-history) apply; and trigger-now does not
  consult pause, so pausing an on-demand schedule changes nothing. With a database, triggered on a
  host that runs jobs, the run is claimed at once (the trigger wakes that host's sweep); triggered on
  a [host that runs no jobs](#a-host-that-runs-no-jobs), it waits for a runner host's next sweep,
  within `ClaimTimeout` (default 2 minutes). Without a database it runs inline on the host that was
  called, and the call returns when it finishes.
- **Listed like any schedule.** `GetSchedulesAsync` includes it, with `IsOnDemand` set and no
  `NextOccurrence`.
- **It is not combined with a timetable.** `OnDemand()` with `Cron(...)` or `Every(...)` fails at
  startup naming both, and so does `RunAtStart()`, which would start it at every host start.
  `OnMisfire(...)` and `InTimeZone(...)` are accepted and have no effect, as on an `Every(...)`
  schedule.

### Event triggers

`jobs.On<TEvent>()` runs a job when an event is published. `.After(TimeSpan)` delays it, and
`.MaxRuntime(TimeSpan)` caps each run as it does on a schedule (see [MaxRuntime](#maxruntime)). The
command is built from the event instance, so the job carries whatever it needs:

```csharp
jobs.On<CartAbandoned>()
    .After(TimeSpan.FromMinutes(30))
    .MaxRuntime(TimeSpan.FromMinutes(5))
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
engine fails at startup. Without one, an ordinary `IEventListener<T>` is the non-durable way to react
to an event.

A trigger's event needs no listener of its own for a broker transport to deliver it: `AddJobs`
activates the event in the type registry (`ConduitTypeRegistry.ActivateEvent`), so the host
subscribes it as it does an event one of its listeners handles.

### Coalescing a burst into the work it represents

Ten corrections to one production line's output log mean **one** rebuild of that line's shift
report, not ten. A trigger can say so: `CoalesceBy` declares what makes two units of work the same,
and `Merge` declares how they combine.

```csharp
jobs.On<OutputEntryCorrected>("shift-report-rebuild")
    .CoalesceBy(entry => entry.LineId)
    .Run(entry => new RebuildShiftReport(entry.LineId, entry.From))
    .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming);
```

`Merge` comes after `Run` so the command type is inferred from the lambda rather than written twice.

**Widen, never narrow.** A merge that loses window is silent staleness; doing the work twice is the
cheaper mistake. That is why there is no default merge and `AddJobs` rejects a `CoalesceBy` without
one — "keep the newest and discard the rest" is exactly the narrowing this exists to prevent.

**And be idempotent.** A non-coalescing trigger keys its run on the delivered event's id
(`MessageContext.DeliveredMessageId`), so a broker or the outbox relay redelivering one event collapses
on the run key for free. A coalescing trigger keys on the coalesce
key instead and inherits none of that: a redelivered event is merged twice. Widening merges — keep
the earlier instant, union a set, take a max — are idempotent by construction. An accumulating one is
not, and will over-count:

```csharp
// Wrong: a redelivered event is counted twice.
.Merge((pending, incoming) => pending with { Count = pending.Count + incoming.Count });
```

Coalesced work accumulates in `conduit_job_scopes`, one row per distinct key, and becomes a run only
when the work starts. So the ledger, and the run history, hold **one row per real execution**, rather
than ten runs for ten events, nine of them doing nothing.

**A key's work yields at most two rows at a time: one run in flight, one scope accumulating behind
it.** That is a guarantee rather than a typical case, and the run key is what enforces it — a scope's
run is keyed on the scope key itself, and `JobKey` is unique, so a second run for one key cannot be
written. A pickup whose insert is refused therefore means "a run for this key is already outstanding",
and the scope stays where it is, still accumulating, to become the next run once the one ahead of it
clears. A run between attempts holds its key the same way, so a retry does not fan out either.

**Merging is leading-edge.** With `.After(delay)` the due time is set when the scope opens and a
later event never moves it, so a steady stream cannot starve the run. The merge has already widened
the scope, so the run still covers everything that arrived while it waited.

**A follow-on run starts when the run ahead of it ends.** A scope parked behind an in-flight run is
due but blocked. When the blocking run ends, the host that ran it wakes its own sweep, whose next
pass picks the scope up — the scopes table is shared, so that host need not be the one the events
reached. Only when that run's outcome is never recorded (its host died mid-run, or lost its claim)
does the follow-on wait for a sweep pass, up to `ClaimTimeout` (2 minutes by default). An undelayed
trigger also retries the pickup on every event it receives. Each sweep drains a bounded batch of
scopes, oldest due time first, so if a whole batch of them is blocked behind in-flight runs a newer,
unblocked scope waits for the next pass — self-resolving, since the blocked ones clear and stop
crowding the batch.

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
| `RunAtStart()` | One run per host start, under the [overlap](#overlap) rule ([Run at start](#run-at-start)) | One run per host start, on that host |
| Pause, trigger-now, cancel | Cluster-wide, survive a restart | This host only, lost on restart |
| Run history | Every outcome, in `conduit_job_run_history` | Not recorded |

The accepted cost of that simplicity: an application with a database cannot have a genuinely
per-host job.

## How a scheduled job runs

With an engine configured, one occurrence of one schedule goes like this:

1. Every host computes the schedule's next occurrence in memory and sleeps until it.
2. At occurrence time, each host skips if the schedule is paused.
3. Each host takes the schedule's row lock and, under it, skips if the occurrence is not strictly
   after the schedule's cursor (another replica has already run it) or — unless the schedule allows
   [overlap](#overlap) — if another run of it is outstanding. These checks and step 4's insert are one
   transaction. An `AllowOverlap()` schedule takes no lock and checks only the cursor, just before the
   insert.
4. Each host does an insert-or-ignore of a run row keyed `{schedule}@{occurrenceUtcIso}`. N replicas
   race; one row results.
5. Each host attempts an atomic claim. Exactly one wins.
6. The winner dispatches the command through `IMediator`.
7. On success the row is deleted and the cursor advances, in one transaction.

For three replicas that is three insert-or-ignores (two no-ops), three claim attempts (two returning
nothing), one delete and one cursor update. For a daily job, single-digit statements per day.

### When the database is consulted

Schedules are code-declared, so every host has the same cron expression, or the same interval
aligned to the same epoch, and computes the same occurrences without asking anyone. The database is
never consulted about **when** a job is due, only about **who runs this one**: there is no poll loop
looking for work that has come due, and steady-state traffic is proportional to occurrences rather
than to the passage of time.

A sweep does exist, but it is not a poll for due-ness: it exists to pick up runs whose timing is not
derivable from a declaration — a retry waiting out its backoff, a delayed event trigger, a manual
run (the only kind an on-demand schedule has), and the run a crashed host abandoned. It sleeps until the earliest pending run is due, capped at `ClaimTimeout`
so an abandoned claim is still recovered when nothing else is scheduled. Work the host itself writes
after the sweep went to sleep wakes it early: a manual run, a delayed trigger's run or coalescing
scope, and the end of any run it was executing (which can free a coalescing key or the overlap lock,
or schedule a retry). The wake is in-process: work written on another host wakes that host's sweep,
and work written on a [host that runs no jobs](#a-host-that-runs-no-jobs) waits for a runner's next
pass.

### Success deletes the run, the cursor remembers it

A successful run's row is deleted rather than marked complete, so the ledger's size follows the work
in flight, not the job volume. The table holds in-flight and terminal runs — plus, for a short
window, completed event-trigger runs (below) — never history: every terminal outcome is written to
[run history](#run-history) first, in the same transaction as the ledger transition, so nothing is
lost when the ledger row is later deleted.

A deleted row cannot answer "did we already run 02:00 today?", so the **cursor** in
`conduit_job_schedules` does: an occurrence is materialized only if it is strictly after
`LastCompletedOccurrence`. Completion deletes the row and advances the cursor in one transaction, so a
crash cannot leave an occurrence that has run and looks as if it has not. A replica whose timer fires
late (clock skew, a long GC pause), after another replica has already run the occurrence to the end,
finds the cursor past it and skips it. The cursor only ever moves forward, so overlapping runs that
finish out of order cannot move it back. One narrow exception: an `AllowOverlap()` schedule reads the
cursor without the schedule's row lock, so a late replica runs the occurrence again if another
replica's completion of it commits in the instant between that read and its own insert.

An event trigger has no cursor to fall back on, so a non-coalescing trigger run is the one success
that is **not** deleted. Its row is kept with status `Completed` for `CompletedRetention` (default
1 day) and then purged. Its run key is `{trigger}@{DeliveredMessageId}`, and the kept row is what makes a
broker redelivering that event *after* the job finished — the usual shape, since redelivery follows a
lost ack — collapse on the key instead of running the job a second time. Deleting it on success would
only have caught redeliveries that raced the first run. A kept row has no claim and matches none of
the queries that look for live work; only the unique key sees it. A coalescing trigger's run is still
deleted on success: its key is the coalesce key, which the next scope needs.

### Claim renewal

A job can run far longer than any reasonable claim timeout. A rollup that legitimately runs for ten
minutes would, under a five-minute timeout, have its claim treated as abandoned at minute five, be
re-claimed by another host, and **run twice**.

So a host holding a claim renews it on a heartbeat (`HeartbeatInterval`, default 30s) for as long as
it is genuinely running the job. Only a *dead* host's claim goes stale, after `ClaimTimeout`
(default 2 minutes — four missed heartbeats), at which point another replica recovers the
occurrence rather than losing it. Without renewal, "long job" and "dead host" are the same
observation and the recovery that makes the second safe executes the first twice.

Startup rejects a `ClaimTimeout` that is not more than twice `HeartbeatInterval`, because that
misconfiguration produces exactly the double execution renewal exists to prevent, and one above a
day.

Two more things ride the same round-trip, since it is already happening: the renewal reports whether
the claim is still held (so a host that lost it stops rather than finishing work it no longer owns),
and it reads back the cancellation flag (so an operator's request reaches the running host without a
second query or a poll loop).

A host that cannot renew at all — its database unreachable, its connection hung — cannot learn that
its claim was taken, so it **fences itself**: once `ClaimTimeout` has passed since its last renewal
that landed, which is the moment another host may take the claim over, it cancels the job's
`CancellationToken` and gives the run up. A renewal still outstanding at that moment counts as
failed, and after a failed renewal reaching that moment is enough: the host does not try once more
first. A handler that honours its token stops before the other host's run is far along; one that
ignores it runs on, but this host still records nothing for it. A job that finishes while a renewal
is still outstanding does not wait for it: the outcome is written under the claim, which decides.

**An outcome is recorded only under the claim it ran with.** Completing a run, counting a failed
attempt and marking it failed or cancelled each change the run row only while it is still claimed
by this host (`Status = Claiming AND ClaimToken = <this host's token>`), and the cursor and history
row written with them roll back when it is not. Should a host pause past `ClaimTimeout` and another
take the run over, the first host's late outcome cannot delete the row the second is running, move
the cursor past its occurrence, or release it to `Pending` for a third. The first host logs a Warning
with `EventId` **410** (`ClaimLost`, the run's key as a structured field) and writes nothing; the
host now holding the claim records the outcome. The same Warning follows a run the heartbeat gave
up, whether another host took the claim or the heartbeat fenced itself.

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

A trigger is a listener of its event, so for a plain (not `[FireAndForget]`) event it runs inline on
the publisher's call, and a trigger whose run or scope write fails fails the publish: the publisher
gets the `EventListenerException`, as from any inline listener, rather than an event acked with its
work written nowhere. The job itself is at most started there, so a failure of the job's own run never
reaches the publisher.

A non-coalescing trigger's run key is `{trigger}@{DeliveredMessageId}`, which makes it idempotent
against redelivery for free — no dedup store, no inbox required. `MessageContext.DeliveredMessageId` is
the id of the event being delivered, not of the dispatch delivering it: on a broker delivery it is the
event's own `MessageId`, and under an outbox it is the outbox entry's `MessageId` on every local
delivery attempt, though each attempt runs as its own dispatch with a fresh `MessageId` (see
[outbox.md](outbox.md#flush-and-relay-delivery-local-then-transport)). So a failed flush (another listener of the event threw)
followed by the relay's redelivery writes one run, not two. That holds while the run is
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
runner increments the attempt count, sets `RunAt` to now plus the backoff for that attempt
(`RetryBackoff`: by default one minute after the first failure, two after the second, four after the
third, the last entry repeating for any later one) and releases the claim to `Pending`. Any host
picks it up on a later sweep. This layer decides whether there is **another attempt**, and it is the
only one that survives a process restart, which is the thing Polly alone cannot give you.

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

A host that starts while the application is running — a rolling deploy, a new replica — counts an
occurrence as missed as soon as it is due. Under `Skip` it then moves the cursor to that occurrence,
and a running host whose timer for the same occurrence fires a moment later finds the cursor already
there and skips it too ("…its cursor is already at or past it" in the log). The window is that host's
timer latency plus a database round trip, or the clock skew between the two hosts if that is larger;
the cost is **one skipped occurrence** of that schedule. The move-to-now above behaves the same way.
This is accepted rather than covered by a grace period.

`OnMisfire` requires a persistence engine and is rejected at startup without one, rather than
silently ignored: an in-memory timer has no record of what it missed.

## Run at start

`RunAtStart()` gives an interval schedule one run when its host's scheduler starts, besides its
timetable — for work that should not wait a whole interval after a deploy or a restart.

```csharp
jobs.Schedule("sweep").Every(TimeSpan.FromMinutes(5)).RunAtStart().Run(() => new RunSweep());
```

- **Interval schedules only.** `RunAtStart()` on a `Cron(...)` schedule is rejected at startup,
  naming the schedule. A cron schedule's start-up catch-up is `OnMisfire(MisfirePolicy.RunImmediately)`,
  and the two can never both fire for one start. Without `RunAtStart()` nothing changes.
- **One start-up run per host start, outside the timetable.** When the schedule's loop begins it
  first does what it always does — reads the cursor and walks the misfire, which for an interval
  schedule is `Skip`: the cursor moves past every missed occurrence and nothing runs. Then it writes
  one start-up run the way [trigger-now](#operating-jobs-with-ijobs) writes a manual run: no
  occurrence, so completing it never moves the cursor and the cursor never refuses it; due now;
  through the same [overlap](#overlap) lock. A restart after a long outage therefore writes one run —
  not one per missed occurrence, and not one for the misfire walk plus one for the start.
- **Keyed by the start instant.** The run's key is `{schedule}@start-{startedAt}`, `startedAt` being
  when the host's scheduler was constructed. Each host's start writes its own start-up run (the keys
  differ by start instant). A second write for the **same** start collapses into the first while that
  run is outstanding — the same guarantee trigger-now's key gives, because a completed schedule run's
  key is not retained (only an event trigger's message-keyed run keeps its row). Without
  `AllowOverlap()`, the overlap lock keeps at most one run outstanding across hosts.
- **An outstanding run is that start's work.** If a run of the schedule is still outstanding when the
  host starts — a retry left from before the restart, a manual run, another replica's start-up run —
  no start-up run is written, and the log says so. A **paused** schedule gets none either (also
  logged): unlike trigger-now, nobody asked for this run, and it is not deferred to the resume. A
  database error writing it is logged, and the schedule carries on to its timetable. So is one
  claiming it: the run stays queued, and the sweep claims it within `ClaimTimeout` (default 2 minutes;
  up to twice that in the rare case the claim was recorded and only reading it back failed).
- **The timetable is untouched.** After the start-up run the loop waits for the next occurrence after
  the cursor, exactly as without `RunAtStart()`. The phase stays the interval's own, aligned to the
  Unix epoch and shared by every replica, so the next occurrence may follow the start-up run by less
  than an interval — and is skipped by the
  overlap lock if the start-up run is still outstanding.
- **It is not an occurrence.** `IJobs` lists a start-up run under its schedule as it lists a manual
  run, and one whose attempts are exhausted shows in `GetFailedAsync`; `LastCompletedOccurrence`
  never reflects it.
- **Without a database** the loop dispatches the start-up run once at start, through the same per-host
  overlap tracking as its occurrences, then follows the same epoch-aligned timetable as before.
  The host reports ready without waiting for that run: the first occurrence's timer is armed first.
  The loop never overlaps itself, so a start-up run longer than the interval delays the first
  occurrence, as a long occurrence delays the next.

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
to declare the schedule the same way.

`AllowOverlap()` opts out, for occurrences and manual runs both.

## Cancellation

Cancellation is **cooperative and best-effort**. `CancelAsync` returning does not mean the work
stopped: a handler that never checks its `CancellationToken` runs to completion. And cancelled is
not "did not run" — whatever the handler completed before the token fired stays done.

```csharp
var outcome = await jobs.CancelAsync("nightly-rollup@2026-08-25T02:00:00.0000000Z", cancellationToken);
```

| Outcome | Meaning |
|---|---|
| `Requested` | The flag is set; a running host will see it within one heartbeat, and a run not yet claimed never starts. Not "it stopped". |
| `NotFound` | No run with that key exists. |
| `AlreadyComplete` | The run had already reached a terminal state. |

Only `Requested` means the request took; `NotFound` and `AlreadyComplete` mean there was nothing to cancel.

A run cancelled **before it starts** — still pending, or waiting out a retry's backoff — is marked
`Cancelled` by the host that claims it, without its command being dispatched; its history row says
`Cancelled`. A request that lands between that check and the dispatch reaches the run through the
heartbeat, as for any running job.

A cancelled run is marked `Cancelled`, which is **terminal and distinct from `Failed`**. Two
consequences follow, and both are load-bearing: the retry path never picks it up (retrying would
defeat the cancellation), and the cursor still advances (otherwise the occurrence is re-materialized
and cancelling loops forever).

Pause is a different thing and stays different: it prevents *future* occurrences and never touches
an in-flight run.

### MaxRuntime

An optional per-declaration ceiling, on a schedule (`jobs.Schedule(...).MaxRuntime(...)`) or an event
trigger (`jobs.On<TEvent>().MaxRuntime(...)`); it must be positive. Exceeding it cancels the run's
token and records the run as `Cancelled`, turning a hung job into a bounded, visible outcome rather
than one that blocks its schedule indefinitely through the overlap check. It is enforced by the
heartbeat, which already knows when the attempt began, and it works with or without a database.

**With a database it is only as precise as the heartbeat.** The limit is checked when the heartbeat
renews the claim, so it takes effect at the first renewal at or after it: with the default
`HeartbeatInterval` of 30 seconds, a `MaxRuntime` of 10 seconds cancels the run at about 30 seconds,
and one of 45 seconds at about a minute. Lower `HeartbeatInterval` if that granularity matters.
Without a database the in-memory scheduler cancels on time.

Because cancellation is cooperative, the heartbeat keeps renewing the claim after it has asked the
job to stop — otherwise a handler that takes its time winding down would have its claim go stale and
a second host would start the same job.

### Shutdown

There is no API for this and none is needed. Each dispatch's token is linked to the host's stopping
token, so on a graceful drain a well-written handler stops promptly, and the runner releases the
claim back to `Pending` with **the attempt count untouched** — a handover is not a failed attempt.
Another host picks the job up immediately rather than waiting out `ClaimTimeout`, which turns a
rolling deploy from "stalled jobs per drained pod" into a fast handover.

The token is cancelled the moment the host begins to stop; there is no grace period in which a job
may finish undisturbed. The scheduler then waits for its running jobs to end, but **no longer than
the host's shutdown deadline** (`HostOptions.ShutdownTimeout`, 30 seconds by default, or the token
passed to the host's `StopAsync`). A handler that ignores its token is left running and the host
stops anyway, logging one Warning with `EventId` **411** (`DrainAbandoned`) that names the runs still
executing. Their claims are not released: each goes stale once the host stops renewing it, and
another host takes the run over after `ClaimTimeout`.

## Operating jobs with IJobs

```csharp
public interface IJobs
{
    Task<IReadOnlyList<JobScheduleInfo>> GetSchedulesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<JobRunInfo>> GetInFlightAsync(CancellationToken cancellationToken);
    Task<JobRunInfo?> GetRunAsync(string jobKey, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobRunInfo>> GetPendingAsync(JobFilter filter, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobScopeInfo>> GetPendingScopesAsync(JobFilter filter, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(JobFilter filter, CancellationToken cancellationToken);
    Task<JobHistoryPage> GetHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken);
    Task PauseAsync(string scheduleName, CancellationToken cancellationToken);
    Task ResumeAsync(string scheduleName, CancellationToken cancellationToken);
    Task<string> TriggerNowAsync(string scheduleName, CancellationToken cancellationToken);
    Task<CancellationOutcome> CancelAsync(string jobKey, CancellationToken cancellationToken);
}
```

Inject `IJobs` anywhere — an admin endpoint, a diagnostics page, a CLI.

- `GetPendingAsync(filter)` — runs that are waiting rather than running, soonest first. This is where
  a run **between attempts** appears: it is `Pending` with `RunAt` set to when the next attempt falls
  due, so it shows in neither `GetInFlightAsync` nor `GetFailedAsync`, the latter listing only runs
  whose attempts are exhausted.
- `GetRunAsync(jobKey)` — one run's ledger row by its key, whatever its status: pending (awaiting a
  retry when `AttemptCount` is above zero), running (`Claiming`), or terminal and still kept. It is
  `null` once the ledger holds no such row — a successful schedule run's row is deleted in the same
  transaction that writes its [history](#run-history) row, so to follow a run, read this first and,
  on `null`, history filtered by `JobKey`: the run is in one or the other until retention purges it.
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
hand cannot swallow the occurrence the schedule was already waiting for. Called on a host that runs
jobs, it wakes that host's sweep, which claims the run at once; the wake is in-process, so called on
a [host that runs no jobs](#a-host-that-runs-no-jobs) the run waits for a runner host's next sweep,
within `ClaimTimeout`. It returns the run's key,
`{schedule}@manual-{instant}` — what `GetRunAsync`, a `JobKey` history filter and `CancelAsync`
take — and that key is what sets `IsManual` on the run's `JobRunInfo` and `JobRunHistoryInfo` (an
occurrence, a start-up run and a trigger's run read `false`; so does a coalescing trigger's run unless
its coalesce key itself begins `manual-`). Without a database the run executes inline, and the key it
returns names a run that has already finished, with no ledger to read it back from. For an
[on-demand schedule](#on-demand-schedules) it is the only way a run starts. It honours the schedule's
[overlap](#overlap) rule: without `AllowOverlap()` it throws `JobOverlapException` (naming the
schedule in `ScheduleName`) while a run is outstanding, and queues nothing. The exception is
deliberately not a `ConflictException` — that is a wire refusal, and whether an overlap is a client's
business is the caller's call:

```csharp
try
{
    return await jobs.TriggerNowAsync(name, cancellationToken);
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
    // entry.IsManual: whether TriggerNowAsync queued it
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
attention right now". `JobCleanup` purges both independently, every `CleanupInterval` (default one
hour).

Without a persistence engine there is no ledger and no history either — `GetHistoryAsync` returns an
empty page, the same honest degradation `GetFailedAsync` and `GetPendingAsync` use.

## Without a database

No engine means no tables. Each host runs one timer loop per timed schedule, waiting on the injected
`TimeProvider`, and dispatches directly. An [on-demand schedule](#on-demand-schedules) gets no loop.

- The schedule fires **on every host**. A Warning is logged at startup naming each schedule and
  saying so, because the guarantee is materially weaker and should be visible in any real
  deployment's logs rather than discovered from duplicated work. An on-demand schedule gets none: it
  fires on no host by itself.
- `IJobs` degrades honestly: queries answer from the in-memory registry, the run lists are empty
  because there is no ledger, and pause, trigger-now and cancel affect only the host they were
  called on. Trigger-now runs the command inline; the overlap rule then means "not while a run of
  the schedule is in progress on this host", and a manual run skips an occurrence that comes due
  while it is still going.
- `OnMisfire` and event triggers are **rejected at startup**, not silently ignored.
- Cancellation still works in-process, and `MaxRuntime` works normally — it needs nothing more than
  a start time and a token. `CancelAsync` reaches a run in progress on this host by its run key,
  shaped as with a database (the key `TriggerNowAsync` returns, `{schedule}@{occurrenceUtcIso}` for
  an occurrence, `{schedule}@start-{instant}` for a start-up run), so code written against the
  durable API behaves the same in a test host, or by its schedule's name; the run key wins when the
  text is both.

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
| Schedules | May be declared; `GetSchedulesAsync` lists and `TriggerNowAsync` triggers only declared ones | Rejected at `AddJobs` |
| Event triggers | Rejected at `AddJobs` | Rejected at `AddJobs` |

- **Declare a schedule here only to list or trigger it, and declare it exactly as the runner does.**
  `GetSchedulesAsync` lists the local declarations, and trigger-now builds the command, and applies
  the [overlap](#overlap) rule, from the declaration on the host it is called on. Pause, resume and
  cancel, and the run, scope and history queries, need no declaration: they work by schedule name or
  run key against the shared tables.
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
| `JobKey` (unique) | `{schedule}@{occurrenceUtcIso}`, `{trigger}@{deliveredMessageId}`, or `{trigger}@{coalesceKey}` for a coalescing trigger |
| `ScheduleName` | The declaring schedule or trigger; drives overlap and the cursor |
| `Occurrence` | The occurrence this run is for, on schedule runs |
| `MessageType`, `Payload` | The command to dispatch |
| `RunAt` | Due time — the claim's eligibility column |
| `Status`, `ClaimedAt`, `ClaimToken` | Claim protocol, identical to the outbox's |
| `AttemptCount`, `LastError` | Durable retry state |
| `CancellationRequested` | Set by `CancelAsync`, observed by the running host's heartbeat |
| `StartedAt` | When the attempt began — drives `MaxRuntime` |

A table created by a migration must keep `JobKey` unique: materializing a run is an insert that
collapses into the existing row on it.

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

`ScopeId` and `Version` together guard each merge (a compare-and-set), so two hosts merging into one
scope cannot lose either's work, and a merge that lands while the scope is being picked up survives
as the next scope.

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
| `AttemptCount` | How many attempts had already failed when the final one began (0 for a first-try success) |
| `LastError` | The failure message, for `Failed` |

`JobKey` is not unique here: a coalescing key whose runs went terminal five times leaves five rows,
one per run.

## Configuration reference

| Property | Default | Purpose |
|---|---|---|
| `HeartbeatInterval` | 30 seconds | How often a running host renews its claim |
| `ClaimTimeout` | 2 minutes | How stale a claim must be before another host may take it |
| `MaxAttempts` | 3 | Durable attempts before a run is marked failed, the first included |
| `RetryBackoff` | 1, 2, 4 minutes | The wait before each further attempt; the last entry repeats |
| `FailedRetention` | 7 days | How long terminal ledger rows are kept for inspection |
| `CompletedRetention` | 1 day | How long a completed event-trigger run is kept, so a late redelivery of its event collapses |
| `CleanupInterval` | 1 hour | How often terminal runs and history past their retention are purged |
| `AutoCreateTable` | `true` | Whether to create the jobs tables (including run history) on startup |
| `HistoryRetention` | 30 days | How long run history is kept |
| `ManageOnly` | `false` | Register `IJobs` without the runner — see [A host that runs no jobs](#a-host-that-runs-no-jobs) |

```csharp
conduit.AddJobs(jobs =>
{
    jobs.HeartbeatInterval = TimeSpan.FromSeconds(15);
    jobs.ClaimTimeout = TimeSpan.FromMinutes(1);
    jobs.MaxAttempts = 5;
    jobs.RetryBackoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)];  // then 5 minutes each time
    jobs.FailedRetention = TimeSpan.FromDays(30);
    jobs.CompletedRetention = TimeSpan.FromDays(3);  // the longest redelivery delay you expect
    jobs.HistoryRetention = TimeSpan.FromDays(90);
    jobs.CleanupInterval = TimeSpan.FromMinutes(15);
    jobs.AutoCreateTable = false;                 // you manage schema through migrations

    // schedules and triggers...
});
```

`AddJobs` validates the options and fails at startup with an `InvalidOperationException` naming the
offending one:

- `HeartbeatInterval` and `MaxAttempts` must be positive.
- `ClaimTimeout` must be more than twice `HeartbeatInterval` and at most one day.
- `CleanupInterval` must be positive and at most one day: the cleanup waits it between passes, and a
  wait past about 49.7 days would end the cleanup service, and with it the host.
- `RetryBackoff` must hold at least one entry, each positive and at most one day: a backoff is added
  to now, and one near `TimeSpan.MaxValue` would leave the retry unrecorded and the run re-executing
  every `ClaimTimeout`. For no retry at all, set `MaxAttempts = 1`.
- The three retentions cannot be negative and can be at most 3650 days (zero purges a row at the
  next cleanup): each is subtracted from now, and one near `TimeSpan.MaxValue` would fail every
  cleanup pass, so nothing would ever be purged.
- A declaration's `MaxRuntime` must be positive too.

The one-day and 3650-day ceilings are the outbox's and the inbox's.

Every knob here but `ManageOnly` describes the durable path.
[Without a database](#without-a-database) none of them apply — there are no tables to create, no
claim to renew or time out, no ledger rows to retain, and no history to purge.

## Limitations

- **No imperative one-shot scheduling.** There is no `ScheduleAsync(command, runAt)`. A delayed
  *event trigger* covers the declarative version of that need.
- **No backfill.** Misfire offers skip or one catch-up, and nothing else.
- **Waking the sweep is in-process.** Trigger-now on a host that runs no jobs, and a coalesced
  follow-on whose blocking run's outcome was never recorded (its host died), wait for a runner
  host's next sweep, up to `ClaimTimeout`.
- **`MaxRuntime` is as coarse as the heartbeat** with a database: it takes effect at the first
  renewal at or after the limit.
- **Shutdown does not wait for a job that ignores its token** past the host's shutdown deadline; that
  run's claim goes stale and another host re-runs it after `ClaimTimeout`.
- **No runtime-defined schedules.** Schedules are code-declared; that is what lets every host agree
  on occurrences without coordinating.
- **Cancellation is not guaranteed.** It is cooperative, and partial work stays done.
- **No UI.** `IJobs` is the surface; build what you need on it.
