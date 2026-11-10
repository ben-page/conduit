# Pre-release upgrade notes

> These notes describe moving a host from Conduit's unreleased internal builds to the first public preview. They are
> kept for the record; a host that starts on a published package has nothing to upgrade from.

Almost every existing code-only registration still compiles unchanged — the connection-string and
hub-URI arguments became optional rather than moving; the one exception is a job declaration whose
lambda returns unrelated command types (below). A few consequences are worth knowing before you
upgrade the packages:

- **Recompile downstream assemblies.** `RabbitMQOptions.ConnectionString`,
  `ActiveMQOptions.ConnectionString`, and `AzureServiceBusOptions.ConnectionString` relaxed from `required init` to `{ get; set; }` so configuration
  can supply them. That is source-compatible — object initializers still work — but *binary*
  breaking: dropping `init` removes the setter's `IsExternalInit` modreq, so an assembly compiled
  against the previous package version must be rebuilt rather than swapped in. `UseSignalR` also
  gained parameters, so it is binary breaking in the same way.
- **`JobSchedule.FromInterval` gained a trailing optional `runsAtStart` parameter** for
  `ScheduleBuilder.RunAtStart()`. Callers still compile unchanged, but the method's signature changed,
  so an assembly compiled against the previous package version must be rebuilt rather than swapped in
  (binary breaking, as above).
- **`ScheduleBuilder.Run` and `EventTriggerBuilder<TEvent>.Run` are now generic, `Run<TCommand>(...)`**,
  so the declared command type is known when the job is declared (a `[FireAndForget]` command is
  refused there; see [jobs](../jobs.md#two-kinds-of-job)). A lambda returning one command type infers it
  and compiles unchanged; one whose branches return unrelated command types has no type to infer and
  needs `Run<ICommand>(...)`. The signatures changed, so this is binary breaking as above.
- **`AddJobs` refuses values it used to accept**, failing at startup with an
  `InvalidOperationException` naming the option: a `HeartbeatInterval` or `MaxAttempts` of zero or
  less, a declaration's `MaxRuntime` of zero or less, a negative retention, and durations above their
  ceiling (one day for `CleanupInterval` and each `RetryBackoff` entry, 3650 days for a retention).
  See [jobs](../jobs.md#configuration-reference).
- **`AddAudit` and `AddAuditListener` refuse values they used to accept**, failing at startup with an
  `OptionsValidationException` naming each option: a `BatchSize` or `ChannelCapacity` of zero or less,
  a `StaleGroupTimeout`, `RetentionPeriod` or `PurgeInterval` of zero or less, a negative
  `ShutdownGrace`, and durations above their ceiling (one day for `StaleGroupTimeout`, `ShutdownGrace`
  and `PurgeInterval`, 3650 days for `RetentionPeriod`). An enabled `MaxPayloadBytes` below 1 KB, refused
  before with an `ArgumentOutOfRangeException`, is now one of those failures. The same rules run on
  `IOptions<AuditOptions>` as the host starts, so a value set with `services.Configure<AuditOptions>`
  fails the start too. See [audit](../audit.md#validated-options).
- **`AuditBatchPublisher`'s constructor gained a `ChannelWriter<DispatchAuditRecord>` second parameter**,
  so code constructing it directly no longer compiles (source breaking); registered by `AddAudit`, it
  is unaffected. `AuditOptions.ShutdownGrace` and `AuditBuilder.ShutdownGrace` are new.
- **A dispatch silent past `StaleGroupTimeout` now stores two records** with its `MessageId`: a
  provisional one ending in a `Conduit.Audit.StaleGroupEviction` marker, with no outcome, and then its
  real one. It used to store one, recorded as failed (`Conduit.Audit.StaleGroupEviction` as a
  `DispatchFailed`), and drop its real outcome. `IAuditStore.GetByMessageIdAsync` returns the most
  recently stored record of a `MessageId` that is not provisional. See [audit](../audit.md#stale-dispatch-groups).
- **A missing required value now throws `InvalidOperationException`.**
  Where `UseRabbitMQ(null)` or `UseSignalR(null)` used to fail immediately in a guard clause with
  an `ArgumentNullException`/`ArgumentException`, the
  value is now validated after configuration and code have both been applied, and the message names
  both ways to supply it: *"RabbitMQ transport for endpoint 'orders' has no connection string. Set
  'Conduit:Endpoints:orders:ConnectionString' in configuration or pass it to UseRabbitMQ()."*
  It is still thrown from the `AddConduit` call, so a misconfigured host still fails at startup.
- **Tables an internal build created need changing.** Startup creates tables and indexes but never alters a column.
  A `conduit_outbox` created by an internal build lacks `FailedAt` and has unbounded key columns: drop it before the
  new build starts, or add `FailedAt` and give `MessageId`, `ClaimToken`, `NextAttemptAt` and `SentAt` the bounded
  types in [the outbox table](../persistence.md#the-outbox-table). Left as it is, marking an entry Failed and every
  Failed retention cleanup throw, and on SQL Server startup fails, because the indexes cannot be created over
  `NVARCHAR(MAX)` columns. A `conduit_dead_letters` created by an internal build lacks `ReplayClaimToken` and
  `ReplayClaimedAt`, so every replay fails until the table is dropped or the two columns are added
  ([the dead-letter table](../persistence.md#the-dead-letter-table)).
- **A durable RabbitMQ queue an internal build declared without `x-dead-letter-exchange`** makes the broker refuse the
  new declaration (`PRECONDITION_FAILED`, 406), and the transport fails to start: delete the queue, or re-declare it
  with the arguments in [RabbitMQ → Topology](../transports/rabbitmq.md#topology).
