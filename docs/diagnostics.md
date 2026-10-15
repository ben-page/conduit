# Diagnostics

The Conduit source generator (`Conduit.SourceGen`) emits compile-time diagnostics while it scans
the compilation for `ICommand` / `IQuery` / `IEvent` types and their handlers. The descriptors live
in `src/Conduit.SourceGen/DiagnosticDescriptors.cs`.

| Code | Severity | Title | Meaning |
|---|---|---|---|
| `CONDUIT001` | Warning | No handler found | A command or query has no local handler and is not declared a remote contract. |
| `CONDUIT002` | Error | Multiple handlers found | More than one handler was found for the same command/query. Only one is allowed. |
| `CONDUIT003` | Warning | Invalid `[FireAndForget]` usage | `[FireAndForget]` was applied to a type that cannot be fire-and-forget. |
| `CONDUIT004` | Warning | Handler not accessible | A handler exists in a referenced assembly but is not accessible (e.g. internal) to the generating assembly. |
| `CONDUIT005` | Error | Conflicting audit marks | A message member is marked both `[DoNotAudit]` and `[AuditRedacted]`. |

---

## CONDUIT001 — No handler found

> No handler found for `{type}`, create a class implementing `{handlerInterface}`

The generator discovers every `ICommand` / `IQuery` across the whole compilation (including
referenced assemblies) and warns when none of them has a matching local handler.

**Resolve** by implementing the handler (`ICommandHandler<T, TResult>` or `IQueryHandler<T, TResult>`).

**Suppress** when the type is a **remote contract** handled in another service — mark the namespace:

```csharp
[assembly: RemoteContracts("Inventory.Commands")]
```

This suppresses CONDUIT001 for all types in that namespace without changing routing behavior. Add
one per remote namespace. See [Transports → \[RemoteContracts\]](transports/index.md#remotecontracts).

## CONDUIT002 — Multiple handlers found

> Multiple handlers found for `{type}`, only one handler per command/query is allowed

Commands and queries must have exactly one handler. This is an **error** (build fails).

**Resolve** by removing the duplicate handler. If one handler should remain discoverable but not be
auto-wired by the generator (e.g. it is registered manually, or it is an internal helper like the
audit subsystem's `LocalAuditListener`), mark it with `[NotAutoRegistered]`
(`Conduit.Attributes.NotAutoRegisteredAttribute`). The type is still discovered as a message handler
for analysis purposes but is excluded from auto-registration, leaving a single registered handler.

## CONDUIT003 — Invalid `[FireAndForget]` usage

> `[FireAndForget]` on `{type}` is not supported — only void commands (`ICommand`) and events
> (`IEvent`) can be fire-and-forget. Commands with results and queries require a response.

`[FireAndForget]` is only valid on **void commands** (`ICommand`) and **events** (`IEvent`).

**Resolve** by removing `[FireAndForget]` from any `ICommand<TResult>` or `IQuery<TResult>` — those
require a response and cannot be fire-and-forget. Use it only where no reply is expected.

## CONDUIT004 — Handler not accessible

> Handler for `{type}` exists but is not accessible to the generating assembly. Make it public to
> be auto-registered.

Handler auto-discovery spans referenced assemblies, but the generating assembly can only register a
handler it can **see**. An `internal` handler in a referenced assembly is found but cannot be wired up.

**Resolve** by making the cross-assembly handler `public` so it can be auto-registered. If the
handler is genuinely meant to stay internal to its own assembly, register Conduit (and its
generator) in that assembly instead, so the handler and its generated registration live together.

## CONDUIT005 — Conflicting audit marks

> '{type}' marks member(s) {members} both [DoNotAudit] and [AuditRedacted]; an audited member takes one

`[DoNotAudit]` removes a member from the audited request payload; `[AuditRedacted]` keeps the member but
writes the `•••` placeholder instead of its value. They contradict each other, so a member may carry only one.
The generator reports the conflict on any command, query or event whose member carries both. The scan mirrors
the serializer's rule: it walks the **base chain** (a mark on a base record's parameter or a virtual base
property counts for the override) and binds a constructor parameter to a property **case-insensitively**, so
`[DoNotAudit]` on a record parameter `Ssn` and `[AuditRedacted]` on a property `ssn` conflict.

**Resolve** by keeping one: `[DoNotAudit]` when the member's presence is not worth recording, `[AuditRedacted]`
when it is. The generator checks only the message type's own members; a **nested DTO** carrying both marks is
not caught at build and is refused at serialization time (an `InvalidOperationException` when the payload is
audited), so keep one there too.

---

See also: [Dispatch Flows](dispatch-flows.md) · [Modular Monolith](modular-monolith.md) ·
the source generator's [`CLAUDE.md`](../src/Conduit.SourceGen/CLAUDE.md).
