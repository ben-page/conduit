# Audit Trail

- [What the audit trail captures](#what-the-audit-trail-captures)
  - [When to use it](#when-to-use-it)
- [How it works](#how-it-works)
- [Two entry points: capture and persistence](#two-entry-points-capture-and-persistence)
- [Deployment roles](#deployment-roles)
- [Setup](#setup)
- [Database dialects](#database-dialects)
- [Capture tuning](#capture-tuning)
- [Retention](#retention)
- [Suppressing sensitive data](#suppressing-sensitive-data)
- [What gets captured](#what-gets-captured)
- [Records and retried attempts](#records-and-retried-attempts)
- [Distributed mode](#distributed-mode)
- [Limitations](#limitations)

## What the audit trail captures

The audit trail records the lifecycle of every dispatch — the message type and kind, the message/conversation/causation IDs that correlate it to other dispatches, the (optional) payload, contextual properties, a timestamp, and an ordered list of observations (`DispatchInvoked`, `HandlerStarted`, `HandlerCompleted`, `DispatchCompleted`, `DispatchFailed`, retry attempts, and so on).

It is built on `MessageContext.Observe()`, a static observation hook the generated mediator and other framework components call at the moment each lifecycle event occurs. When no observer is registered the hook is a no-op, so the audit infrastructure is **zero-cost until you opt in**. Payload serialization is guarded the same way: the generated mediator serializes a message for the audit payload only when an observer is attached (`MessageContext.HasObserver`).

### When to use it

Use the audit trail when:
- **You need an operational record** of what was dispatched, when, by whom, and whether it succeeded or failed.
- **You need cross-service traceability** — records from different services that describe the same logical message share a `MessageId`, so a central store can reconstruct the full timeline.
- **You need retention with automatic cleanup** — records are purged after a configurable window.

You may not need it when:
- Distributed tracing (the built-in OpenTelemetry `ActivitySource`) already gives you what you need for request-level observability.
- You only care about errors, which the dead-letter queue or your logging pipeline already captures.

## How it works

```
Dispatch runs
  └── generated mediator calls MessageContext.Observe(observation)
        at each boundary (DispatchInvoked, HandlerStarted, DispatchCompleted, ...)

AuditObserver  (set by AddAudit)
  ├── groups observations by MessageId into a DispatchGroup
  ├── seals the group on DispatchCompleted / DispatchFailed
  └── writes the sealed DispatchAuditRecord to a bounded channel

AuditBatchPublisher  (background service, set by AddAudit)
  ├── drains the channel
  └── publishes batches as AuditBatch events via IMediator.PublishAsync
      (a listener failure is logged and that batch dropped; the next batch is published as usual)

LocalAuditListener : IEventListener<AuditBatch>  (set by AddAuditListener)
  └── persists each batch to IAuditStore  (whether the event was published
      in-process or arrived over a transport)

AuditPurgeService  (background service, set by AddAuditListener)
  └── periodically deletes records older than the retention window
```

The key insight is that **persistence is event-driven**: records reach the store only because `LocalAuditListener` handles `AuditBatch` events. The listener does not care whether those events were published in this process or arrived over a transport from another service — which is why local storage and a central collector use the same configuration call.

## Two entry points: capture and persistence

Audit is configured by two orthogonal calls on `ConduitBuilder`:

- **`AddAudit()`** — the **capture** side. Registers the observer, the channel, and the batch publisher. Any service that wants its own dispatches audited calls this. It emits `AuditBatch` events; it does not, by itself, store anything.
- **`AddAuditListener()`** — the **persistence** side. Registers the `IEventListener<AuditBatch>`, the store, the table initializer, and the retention purge. It consumes `AuditBatch` events and writes them down.

Compose them according to where you want audit captured and where you want it stored.

## Deployment roles

```csharp
// 1. Producer only — captures its own dispatches and ships AuditBatch events
//    to a central collector over a transport. Stores nothing locally.
conduit.AddAudit();

// 2. Single service — captures and stores its own audit in one process.
conduit.UseSqlite("Data Source=audit.db");   // engine on the ConduitBuilder
conduit.AddAudit();
conduit.AddAuditListener();

// 3. Central collector — does not capture its own traffic; persists AuditBatch
//    events arriving over a transport from many services.
conduit.UsePostgres("Host=localhost;Database=Audit;...");
conduit.AddAuditListener();
```

## Setup

Add the `Conduit.Audit` package and configure inside `AddConduit`:

```xml
<PackageReference Include="Conduit.Audit" />
```

```csharp
builder.Services.AddConduit(conduit =>
{
    conduit.UseSqlite("Data Source=audit.db");   // persistence engine, shared with durability

    conduit.AddAudit(audit =>
    {
        audit.ServiceName = "OrderService";
    });

    conduit.AddAuditListener();
});
```

## Database engines

Audit shares the persistence engine with the durability subsystem — there is one engine registration on the `ConduitBuilder`, not on the audit listener. Register the engine with the matching `Conduit.Persistence.*` package's `UseXxx` extension; audit and durability both resolve it from DI:

| Engine package | Method | Timestamp column |
|---|---|---|
| `Conduit.Persistence.Sqlite` | `conduit.UseSqlite(connectionString)` | ISO-8601 text |
| `Conduit.Persistence.SqlServer` | `conduit.UseSqlServer(connectionString)` | `DATETIME2` |
| `Conduit.Persistence.Postgres` | `conduit.UsePostgres(connectionString)` | `TIMESTAMPTZ` |
| `Conduit.Persistence.MySql` | `conduit.UseMySql(connectionString)` | `DATETIME(6)` |

```csharp
// SQL Server (Conduit.Persistence.SqlServer package)
conduit.UseSqlServer("Server=.;Database=Audit;...");

// PostgreSQL (Conduit.Persistence.Postgres package)
conduit.UsePostgres("Host=localhost;Database=Audit;...");

// MySQL (Conduit.Persistence.MySql package)
conduit.UseMySql("Server=localhost;Database=Audit;...");
```

If `AddAuditListener` is called but no engine is registered, the host fails fast at startup with a message pointing at `conduit.UseSqlite(...)`.

The audit table (`conduit_audit`) is created automatically on startup.

**Timestamps are stored and read as UTC** on every engine: the store writes round-trippable ISO 8601 UTC text and parses it back stamped `Kind=Utc`, matching the durability stores — no per-engine datetime binding required.

## Capture tuning

Properties on the `AddAudit` builder control batching and buffering:

```csharp
conduit.AddAudit(audit =>
{
    audit.ServiceName = "OrderService";                  // stamped on each batch (default: host app name)
    audit.BatchSize = 100;                               // records per batch (default: 100)
    audit.ChannelCapacity = 10_000;                      // buffered records before backpressure (default: 10,000)
    audit.BackpressureMode = BoundedChannelFullMode.DropOldest; // when buffer is full (default: DropOldest)
    audit.StaleGroupTimeout = TimeSpan.FromMinutes(5);   // evict incomplete dispatch groups (default: 5m)
    audit.MaxPayloadBytes = 64 * 1024;                   // largest stored request body, UTF-8 bytes (default: 64 KB; 0 = no cap)
});
```

By default the buffer drops the oldest record under sustained overload, trading completeness for bounded memory — audit must never block or crash the dispatch path.

### Payload size cap

`MaxPayloadBytes` bounds the request body a record keeps. It is applied when the record **seals** (its terminal observation, or stale-group eviction), so it covers every capture path — in process, wire-inbound and `[AuditAs]`-typed members alike — and it measures the body's UTF-8 bytes as serialized. The body is still serialized in full before the cap applies; the cap bounds what is stored and published, not the capture cost.

- A body **at or under** the cap is stored unchanged, byte for byte.
- A body **over** the cap whose root is a JSON object is stored shortened: the longest string values anywhere in it (objects and arrays, any depth) are cut to one common length, the largest that fits, each keeping its leading characters followed by `…`; every other member is kept as it was. A redacted placeholder (`"•••"`) is never cut. A top-level member records the cut:

  ```json
  { "$truncated": { "bytes": 1048646, "cap": 65536 }, "id": 7, "description": "BEGIN-ddd…" }
  ```

  `bytes` is the original body's UTF-8 length and `cap` the cap it was cut to.
- When the root is not an object, or cutting strings cannot bring the body under the cap (a large array of numbers, say), the stored body is the marker alone: `{ "$truncated": { "bytes": N, "cap": C } }`.

The stored body is always valid JSON and never longer than the cap. A message whose root has its own `$truncated` member collides with the marker: under the cap it is stored as is and reads like a marker, and over the cap it is replaced by the marker (its value is not kept). The shortening runs when the record seals, inside the observer's terminal-observation callback, on the dispatching thread — about 15 ms per MB of body over the cap; a body at or under the cap costs only a length check. A shortened body is re-written by the serializer, so its escaping can differ from the original's (it is not a byte prefix of it). `0` or less turns the cap off; an enabled cap below 1 KB (`AuditOptions.MinimumMaxPayloadBytes`) is refused when `AddAudit` runs, so the marker always fits. Bodies that should never be kept at all still belong to `SuppressPayload<T>()`, `Suppress<T>()` and `[DoNotAudit]` — the cap only bounds what remains.

## Retention

Retention is owned by the listener, since it registers the purge service:

```csharp
conduit.UseSqlite("Data Source=audit.db");
conduit.AddAuditListener(listener =>
{
    listener.RetentionPeriod = TimeSpan.FromDays(30);    // default: 90 days
    listener.PurgeInterval = TimeSpan.FromHours(24);     // how often the purge runs (default: 24h)
});
```

## Suppressing sensitive data

Three mechanisms, from coarsest to finest:

```csharp
// Compile-time: omit the payload for a type (observations are still recorded).
[DoNotAudit]
public sealed record UserLogin(string Username, string Password) : ICommand;

// Runtime: skip ALL audit recording for a type.
conduit.AddAudit(audit => audit.Suppress<HealthCheck>());

// Runtime: record observations but omit the payload for a type.
conduit.AddAudit(audit => audit.SuppressPayload<UserLogin>());
```

`[DoNotAudit]` is also valid on a property, or on a positional-record parameter, to drop that one member and keep the rest of the body:

```csharp
// On a positional record, mark the parameter (it also drops the bound property).
public sealed record UserLogin(string Username, [DoNotAudit] string Password) : ICommand;

public sealed class Profile
{
    public string Name { get; init; } = "";
    [DoNotAudit] public string Ssn { get; init; } = "";
}
```

`[AuditRedacted]` is the middle course for a member whose *presence* matters but whose value must never be stored. On a property or a positional-record parameter, the audit payload carries the placeholder `"•••"` when the member has a value; `null` and `""` stay as themselves (so "sent" and "blanked" remain distinguishable). It works on a member of any type, and records no derived value: no hash, no length, no last four.

```csharp
public sealed record UpdateSsn(int EmployeeId, [AuditRedacted] string? SocialSecurityNumber) : ICommand;
```

`[AuditRedacted]` cannot be combined with `[DoNotAudit]` on one member (CONDUIT005; refused at serialization).

`[AuditAs(nameof(Type))]` types a raw-JSON member at capture. It goes on a `JsonElement` or `JsonElement?` property or positional-record parameter that holds a serialized Conduit message, and names the sibling `string` property holding that message's wire type name (`Type.FullName`) — an envelope such as a batch item:

```csharp
public sealed record BatchItem(string Type, [AuditAs(nameof(Type))] JsonElement Payload);
```

The audit payload then carries the inner message read into its type (with `JsonOptions`) and serialized with `AuditJsonOptions`, so the inner message's own `[DoNotAudit]`, `[AuditRedacted]` and `[AuditAs]` marks apply. The name resolves **only** among the message types a `ConduitTypeRegistry` registered in this process (commands, queries and events; the generated `AddConduit()` builds one) — never `Type.GetType` on the name, so a sender cannot make capture read an arbitrary type. Every outcome other than a typed inner message keeps the JSON out:

| The member holds | The audit payload carries |
|---|---|
| a message of a registered type | that message, redacted by its own marks |
| a message whose type is `[DoNotAudit]` at type level, or whose body an audit registration in this process suppresses (`Suppress<T>()`, `SuppressPayload<T>()`; process-wide, see below) | nothing — the member is left out, as a `[DoNotAudit]` member is |
| JSON `null` (or an unset `JsonElement`) | `null` |
| JSON under a null name, a name no registry lists (including a non-message type such as `System.String`), a name two different types registered, or JSON the named type fails to read or write (unreadable JSON, a throwing constructor or converter) | the placeholder `"•••"` |

A misapplied `[AuditAs]` — the member is not a `JsonElement`, or the name is not a `string` property — is a build error (CONDUIT006) on a message and on every type a message carries, in the compilation or in a referenced assembly that references `Conduit.Contracts` (see [Diagnostics](diagnostics.md#conduit006--misapplied-auditas) for the walk and its bounds). On a type the generator does not check — one reached only through an interface or `object` member — the serializer writes such a member as the placeholder rather than failing the dispatch. The sibling is the most-derived declaration of that name, so a `new`-hidden property resolves to the derived one. `[DoNotAudit]` or `[AuditRedacted]` on the same member takes precedence over `[AuditAs]`. The marks act only on a type serialized as a JSON object contract: a type with its own `[JsonConverter]` is written by that converter, and none of its members' marks, `[AuditAs]` included, apply — keep such a type out of the payload. `AddAudit` hands its suppressed types to the serializer through a lookup that is process-wide, as `AuditJsonOptions` is: a type any host in the process suppresses is left out of every host's `[AuditAs]` capture (it can only redact more). The same lookup keeps capture from serializing a suppressed type at all (`ConduitRuntime.CapturesAuditPayload`), in process and over the wire, so a suppressed type cannot fail its dispatch through capture.

Member-level capture serializes with `ConduitRuntime.AuditJsonOptions`: `JsonOptions` plus a modifier that drops `[DoNotAudit]` properties (and the properties bound to marked constructor parameters), writes `[AuditRedacted]` ones as their placeholder, and types `[AuditAs]` ones as above. Names match case-insensitively, and base types are walked: a base record's marked parameter counts, and so does a mark on a base virtual property its override does not repeat. Wire serialization keeps `JsonOptions`, so the member still travels on the wire. The modifier acts on members it can see as CLR properties: a raw-JSON member — `JsonElement`, `JsonElement?`, `JsonNode`, `JsonDocument` or `object` (the serializer reads `object` as a `JsonElement`), or a `[JsonExtensionData]` bag — is captured **verbatim**, with any `[DoNotAudit]`/`[AuditRedacted]` value inside it in clear, **unless** it is marked `[AuditAs]` (typed at capture), `[DoNotAudit]` (left out) or `[AuditRedacted]` (the placeholder). A message carrying an unmarked raw-JSON member that may hold a secret must be kept out of the payload (`SuppressPayload<T>()`, `Suppress<T>()` or a type-level `[DoNotAudit]`).

**Wire-inbound dispatch captures the same body.** A request or event that arrives over a transport (the generated `HandleWireRequestAsync`/`HandleWireEventAsync`) is read into its typed message, and that object is serialized with `AuditJsonOptions` (`ConduitRuntime.CaptureAuditPayload`) — never the raw inbound JSON, which would keep `[DoNotAudit]` members and any property the sender invented. A raw-JSON member follows the rule above: verbatim unless it is `[AuditAs]`, `[DoNotAudit]` or `[AuditRedacted]`. A type-level `[DoNotAudit]` type captures nothing. A request this host **forwards** to another endpoint by route is never deserialized here, so its record carries no body. The receiving host records its own. A request whose JSON cannot be read still leaves a failed record, without a body.

`[DoNotAudit]` on a type suppresses payload capture only — the dispatch is still observed and recorded, just without the message body. `Suppress<T>()` drops the type from the audit trail entirely; `SuppressPayload<T>()` keeps the record but omits the body.

## What gets captured

A `DispatchAuditRecord` contains:

| Field | Description |
|---|---|
| `MessageId`, `ConversationId`, `CausationId` | Correlation IDs from `MessageContext`. Records across services join on `MessageId`. |
| `MessageType`, `MessageKind` | Full type name, and the kind: `command` / `query` / `event` for an in-process dispatch; `Request` (command or query) / `Event` for a wire-inbound one. |
| `ServiceName` | The emitting service (from `AddAudit().ServiceName`). |
| `Payload` | The serialized message body — unless suppressed via `[DoNotAudit]` or `SuppressPayload<T>()`. A body over `MaxPayloadBytes` (64 KB by default) is stored shortened with a `$truncated` marker (see [Payload size cap](#payload-size-cap)). |
| `Properties` | Contextual properties carried on the `MessageContext`. |
| `Timestamp` | UTC timestamp of the dispatch. |
| `Observations` | Ordered lifecycle observations (invoked, handler start/complete, retries, completed/failed). |

## Records and retried attempts

When the host uses [dispatch retry](dispatch-retry.md), a dispatch can run several attempts, and the audit trail describes the dispatch, not each attempt.

- **The dispatch that owns the attempts** is one dispatch with one `MessageId` across every attempt, so it has one record. Each retry appears in its `Observations` as a `RetryAttempted`.
- **A dispatch nested in an attempt** (a command the handler sends, an event it publishes, from the attempt's own scope) has a `MessageId` and a record of its own. The observer seals that record when the nested dispatch completes, which is mid-attempt, but **holds the write until the attempt is known to be kept**: it succeeded, or its failure is the dispatch's answer (no reason to retry, or no attempts left). If the attempt is abandoned for a retry, the held record is dropped. A retried dispatch therefore records the kept attempt's nested dispatches once, and an exhausted one records its last attempt's, which is what a failed dispatch without retry records. A dispatch whose wait before a retry is cancelled ends with its last attempt abandoned, so none of its attempts' nested records is written (its own record still is).
- **A nested dispatch whose effect leaves the attempt is dropped with it too, although the effect happened.** A local `[FireAndForget]` command or event (its handler or listeners are already enqueued on the background dispatcher), an event broadcast to a transport that no outbox captures, and a command or query routed to a remote endpoint bind to the attempt like any nested dispatch. When the attempt is abandoned, the effect has still happened, and the next attempt repeats it, but only the kept attempt's record is written: the audit under-records these dispatches.
- **An independent dispatch** (one made from a different provider, such as a fresh scope opened inside an attempt) is written as soon as it seals. It committed on its own, so its record stays even when the outer attempt is abandoned.
- **Without a retry policy**, every record is written as it seals.

A record is sealed (its payload read and capped) at the moment the nested dispatch completes, so what is held is the finished record, not the live context.

## Distributed mode

When a service calls `AddAudit()` and has a transport registered, the `AuditBatch` events it publishes are broadcast to that transport like any other event (`AuditBatch` is a normal `IEvent`). A dedicated collector service that calls `AddAuditListener()` subscribes to `AuditBatch` and persists everything it receives.

Because each service stamps its own records with the shared `MessageId`, the central store can reconstruct a cross-service timeline for a single logical message — e.g. the sender's transport-send and the receiver's handler-start appear as separate records sharing one `MessageId`. Query by `MessageId` via `IAuditStore.GetByMessageIdAsync`.

## Limitations

**Audit is best-effort, by design.** The capture channel is bounded and defaults to `DropOldest` — under sustained overload, the oldest unpersisted records are dropped rather than blocking or failing the dispatch. Audit must never take down the work it observes. A listener that fails (an audit store that cannot be written) loses its batch: `AuditBatch` is a plain event, so the failure throws out of the publish, and `AuditBatchPublisher` logs it at Error and drains the next batch rather than ending the service and the host with it. If you need guaranteed capture, raise `ChannelCapacity` and choose a blocking `BackpressureMode`, accepting the dispatch-path cost.

**Persistence requires a listener.** `AddAudit()` alone emits `AuditBatch` events but stores nothing. Without either a local `AddAuditListener()` or a central collector, the events have no consumer and the records are discarded.

**Metrics are not the audit trail's job.** Audit stores the full per-dispatch observation trail for forensics and replay — it does not aggregate metrics. For real-time counts, durations, success rates, and dead-letter/transport metrics, use Conduit's built-in OpenTelemetry instrumentation (the `"Conduit"` `Meter` in `ConduitTelemetry`), wired into your OTel exporter (Prometheus/OTLP).
