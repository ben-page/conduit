# Diagnostics

The Conduit source generator (`Conduit.SourceGen`) emits compile-time diagnostics while it scans
the compilation for `ICommand` / `IQuery` / `IEvent` types and their handlers. The descriptors live
in `src/Conduit.SourceGen/DiagnosticDescriptors.cs`.

A message is a class (a `record` or a `class`). A `struct` implementing one of the interfaces is not
discovered — Conduit's attributes are class-only — so nothing is generated for it, its handler or its
listener.

| Code | Severity | Title | Meaning |
|---|---|---|---|
| `CONDUIT001` | Warning | No handler found | A command or query has no local handler and is not declared a remote contract. |
| `CONDUIT002` | Error | Multiple handlers found | More than one handler was found for the same command/query. Only one is allowed. |
| `CONDUIT003` | Warning | Invalid `[FireAndForget]` usage | `[FireAndForget]` was applied to a type that cannot be fire-and-forget. |
| `CONDUIT004` | Warning | Handler not accessible | A handler exists in a referenced assembly but is not accessible (e.g. internal) to the generating assembly. |
| `CONDUIT005` | Error | Conflicting audit marks | A message member is marked both `[DoNotAudit]` and `[AuditRedacted]`. |
| `CONDUIT006` | Error | Misapplied `[AuditAs]` | `[AuditAs]` is on a member of a message, or of a type a message carries, that is not `JsonElement`/`JsonElement?`, or names no sibling `string` property. |
| `CONDUIT007` | Error | Generic message types are not supported | A command, query or event is generic, or nested in a generic type. |
| `CONDUIT008` | Error | Message type declared in more than one assembly | Two assemblies the build sees each declare a message with the same full type name. |

### Where a diagnostic points

A diagnostic about a message or handler declared in the project being built points at its
declaration — the file, line and type name — so the IDE underlines it and a warning can be suppressed
right there:

```csharp
#pragma warning disable CONDUIT001 // routed to the inventory service
public sealed record ReserveStock(string Sku) : ICommand;
#pragma warning restore CONDUIT001
```

CONDUIT002 points at the message and lists each of its handlers as an additional location. A
diagnostic about a type from a referenced assembly has no source location to point at; neither do
CONDUIT004, CONDUIT007, CONDUIT008 and a carried type's CONDUIT006. Suppress those with
`[assembly: RemoteContracts]` (CONDUIT001), `<NoWarn>`, or a severity in `.editorconfig`
(`dotnet_diagnostic.CONDUIT001.severity = none`).

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

This suppresses CONDUIT001 for all types in that namespace and the namespaces beneath it, without
changing routing behavior. The match is on whole namespace segments, not a string prefix:
`RemoteContracts("Inventory")` covers `Inventory.Commands.ReserveStock` but not
`InventoryAudit.RecordAdjustment`. (A type's full name also works, for that one type.) Add one per
remote namespace. See [Transports → \[RemoteContracts\]](transports/index.md#remotecontracts). For one
type declared in this project, a `#pragma` around it works too (see
[Where a diagnostic points](#where-a-diagnostic-points)).

## CONDUIT002 — Multiple handlers found

> Multiple handlers found for `{type}`, only one handler per command/query is allowed

Commands and queries must have exactly one handler. This is an **error** (build fails).

The error points at the message, with each handler as an additional location (at the first handler
when the message comes from a referenced assembly).

**Resolve** by removing the duplicate handler. If one handler is registered by hand rather than by
the generator (e.g. a feature's `Add*` extension wires it, as the audit subsystem does
`LocalAuditListener`), mark it with `[NotAutoRegistered]`
(`Conduit.Attributes.NotAutoRegisteredAttribute`). The generator then skips the type entirely: it is
not registered, and it is not counted, so it no longer conflicts. It does not count as a handler for
CONDUIT001 either — a command whose **only** handler is `[NotAutoRegistered]` gets CONDUIT001, since
the generator cannot see the registration that makes it handled. Suppress that one with a `#pragma`
around the command or `[assembly: RemoteContracts]`.

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

## CONDUIT006 — Misapplied [AuditAs]

> '{type}' misapplies [AuditAs]: {reasons}; [AuditAs] goes on a JsonElement or JsonElement? member and names a sibling string property holding the message type name

`[AuditAs(nameof(Type))]` tells the audit serializer that a raw-JSON member holds a serialized message whose wire
type name is in a sibling property, so capture can read it as that message and redact it by its own marks (see
[Audit](audit.md)). That needs the member to be a `JsonElement` or `JsonElement?` and the named sibling to be a
`string` property of the type or a base. Each reason names the member and what is wrong: it is not a `JsonElement`,
or the name is not a property of the type, or the property is not a `string`.

The generator checks the members of every command, query and event, **base chain** included (property attributes
and constructor-parameter attributes alike) — and of every **type a message carries** that is declared in the
compilation being built **or in a referenced assembly that is or references `Conduit.Contracts`** (a contracts
library the generator does not run on, as message discovery reaches it): the types of a message's properties and of
its public constructors' parameters, through
nullables, arrays and generic type arguments (`IReadOnlyList<BatchItem>`, `BatchItem[]`, `Dictionary<string,
List<BatchItem>>`), transitively, so an envelope's item record is checked like the envelope. A generic type is
walked and checked once, as its definition (`Wrapper<T>`, however many constructions are carried), after the type
arguments at each use site are reached; so a recursively expanding generic (`Tree<T>` carrying `Tree<Tree<T>>`)
terminates, and a member typed by a type parameter is checked as declared (a `T` is not a `JsonElement`). Each type
is reported once, under its own name, however many messages reach it (two same-named types from two assemblies are
reported separately); a cycle (a type that carries itself, or A carries B carries A) is walked once. A carried type that is itself a message is reported as a message, not twice.
Every member is walked, marked or not: a misapplied `[AuditAs]` is reported even below a `[DoNotAudit]` member.

A misapplied member of a carried type from a referenced assembly is reported like one on a referenced message:
without a source location, naming the type and member. Every project that runs the generator over that library
reports it.

**Bounds.** An assembly that does not reference `Conduit.Contracts` (the BCL, most packages) cannot carry Conduit's
`[AuditAs]` and is not walked. One that does **is** walked, a third-party or another team's contracts package
included: a misapplied `[AuditAs]` on a type it declares, carried by your message, fails your build with no source
location, and you cannot fix it there. The escape hatch is the severity override in `.editorconfig`
(`dotnet_diagnostic.CONDUIT006.severity = warning`, or `none`); it applies to the whole diagnostic, so it also
downgrades your own misuses until the package is fixed. A member typed as an interface, `object` or a type parameter carries a
runtime shape the generator cannot see, and is not walked. On an unchecked type, a misapplied
`[AuditAs]` is not a build error; the serializer writes the member as the `•••` placeholder (never its raw JSON,
and without failing the dispatch). CONDUIT005 keeps the message-only scope.

**Resolve** by moving `[AuditAs]` to the `JsonElement` member that holds the serialized message, and pointing it at
the `string` property holding that message's type name, with `nameof`.

## CONDUIT007 — Generic message types are not supported

> '{type}' is a generic message type; a message is dispatched by its full type name, so it cannot have type parameters
> or be nested in a generic type. Declare one non-generic type per message.

A message's full type name is its wire name: the type discriminator every transport carries, the key of every
generated dispatch switch, and the `ConduitTypeRegistry` key. A generic definition such as `Wrap<T>` has no such
name — each construction would need its own — and the generated code could not name it. A type nested in a generic
type (`Outer<T>.Inner`) is generic in the same way. This is an **error** (build fails); the generator emits nothing
for the type, and does not wire a handler or listener of it (`IEventListener<Changed<string>>` included).

An `abstract` generic base is not a message and is not reported: `abstract record Envelope<T>(T Body) : ICommand<T>`
is fine, and each concrete, non-generic `record CreateOrder(Order Body) : Envelope<Order>(Body)` is a message.

**Resolve** by declaring one non-generic type per message; share their shape through an abstract generic base if
they have one in common.

## CONDUIT008 — Message type declared in more than one assembly

> '{type}' is declared as a message type in more than one assembly ({assemblies}); a message's full type name is its
> wire name and must name one type. Rename or move one of them.

Discovery spans the compilation and every assembly it references, so two of them (two contracts libraries, or a
library and the project itself) that each declare a message with the same namespace and name make that full type name
mean two types. The generated code could not refer to either one (C# reports the name ambiguous), and a message
arriving under that name could not say which one it is. This is an **error** (build fails); the generator names the
assemblies and emits neither copy.

**Resolve** by renaming one of the types or moving it to another namespace, or by removing the reference that brings
in the second copy — often the same contracts library referenced twice under two assembly names.

---

See also: [Dispatch Flows](dispatch-flows.md) · [Modular Monolith](modular-monolith.md) ·
the source generator's [`CLAUDE.md`](../src/Conduit.SourceGen/CLAUDE.md).
